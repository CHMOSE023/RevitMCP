using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Protocol.Mcp;
using RevitMCP.Tooling.Dispatch;
using RevitMCP.Tooling.Schema;

namespace RevitMCP.Tooling
{
    /// <summary>管线运行期需要读取的配置。用委托而非快照，这样 Ribbon 上改了写入开关能立即生效。</summary>
    public sealed class ToolPipelineOptions
    {
        public Func<bool> WriteEnabled { get; set; } = () => false;
        public Func<int> MaxElementsPerWrite { get; set; } = () => 500;
        public Func<int> DefaultTimeoutSeconds { get; set; } = () => 60;
        public Action<string, Exception> Log { get; set; } = (m, e) => { };

        /// <summary>
        /// 每次 tools/call 结束后收到一条审计记录，无论成败。
        /// 默认什么都不做——Tooling 层不认识文件系统，落盘由 Addin 决定。
        /// </summary>
        public Action<ToolAuditEntry> Audit { get; set; } = entry => { };
    }

    /// <summary>
    /// 执行上下文的身份。Revit 这边就是当前的活动文档。
    ///
    /// 服务始终操作"活动文档"，而用户随时可能切换它。一旦切换，
    /// 调用方手里的构件 ID 会突然全部失效——**而它没有任何办法自己察觉**，
    /// 只会看到一连串莫名其妙的 ELEMENT_NOT_FOUND。
    /// </summary>
    public sealed class ContextIdentity
    {
        public ContextIdentity(string key, string label)
        {
            Key = key;
            Label = label;
        }

        /// <summary>用于判断是不是换了上下文。同一个文档必须始终给出同一个 Key。</summary>
        public string Key { get; }

        /// <summary>给人和模型看的名字。</summary>
        public string Label { get; }
    }

    /// <summary>工具自己抛出的、带领域错误码的失败。会原样呈现给模型。</summary>
    public sealed class ToolFailureException : Exception
    {
        public ToolFailureException(string code, string message) : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    /// <summary>
    /// tools/call 的执行管线，也是协议层看到的 <see cref="IToolCatalog"/>。
    ///
    /// 顺序（每一步都有明确理由，别随意调换）：
    ///   查表 → 绑定入参 → 写保护检查 → 编组到主线程 → 执行 → 序列化 → 异常映射
    ///
    /// 写保护必须在编组**之前**：被拒绝的调用不该占用 Revit 主线程。
    /// </summary>
    public sealed class ToolPipeline<TContext> : IToolCatalog
    {
        private readonly ToolRegistry<TContext> _registry;
        private readonly IWorkDispatcher<TContext> _dispatcher;
        private readonly ToolPipelineOptions _options;
        private readonly IWriteScope<TContext> _writeScope;
        private readonly Func<TContext, ContextIdentity> _contextIdentity;

        /// <summary>
        /// 操作日志：每次写调用走到哪一步、结果是什么。幂等重放与状态查询都靠它。
        /// 只在内存里——理由见 docs/design-f09-operation-state.md §4。
        /// </summary>
        public OperationJournal Journal => _journal;

        private readonly OperationJournal _journal;

        // 上一次调用时的上下文身份。跨请求的进程内状态，与客户端无关——
        // "活动文档换了"是全局事实，不属于某一个会话
        private readonly object _identityLock = new object();
        private string _lastIdentityKey;
        private string _lastIdentityLabel;

        public ToolPipeline(
            ToolRegistry<TContext> registry,
            IWorkDispatcher<TContext> dispatcher,
            ToolPipelineOptions options = null,
            IWriteScope<TContext> writeScope = null,
            Func<TContext, ContextIdentity> contextIdentity = null,
            OperationJournal journal = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _options = options ?? new ToolPipelineOptions();
            _writeScope = writeScope ?? new PassthroughWriteScope<TContext>();
            _contextIdentity = contextIdentity;
            _journal = journal ?? new OperationJournal();
        }

        /// <summary>
        /// 比较上下文身份，换了就给出一句提示。只读不写——
        /// 同一次调用里成功路径和失败路径都要用它，写状态的动作留给 <see cref="CommitIdentity"/>。
        /// </summary>
        private string PeekContextSwitch(ContextIdentity identity)
        {
            if (identity?.Key == null) return null;

            lock (_identityLock)
            {
                // 第一次见到任何上下文都不算"切换"
                if (_lastIdentityKey == null || _lastIdentityKey == identity.Key) return null;

                return "活动文档已从「" + (_lastIdentityLabel ?? "另一个文档") + "」切换到「" +
                       (identity.Label ?? "当前文档") + "」。" +
                       "之前从本服务取得的构件 ID 属于那个文档，在这里全部无效，需要重新查询。";
            }
        }

        private void CommitIdentity(ContextIdentity identity)
        {
            if (identity?.Key == null) return;

            lock (_identityLock)
            {
                _lastIdentityKey = identity.Key;
                _lastIdentityLabel = identity.Label;
            }
        }

        /// <summary>
        /// 把一个管线级参数从 arguments 里摘出来。
        ///
        /// 摘而不是留：工具的入参绑定是严格的，留着它每个写工具都会报"未知参数"。
        /// 原对象不改动——同一个 JsonValue 还要用于审计与幂等指纹。
        /// </summary>
        private static JsonValue Take(JsonValue arguments, string name, out string taken)
        {
            taken = null;
            if (arguments == null || !arguments.IsObject) return arguments;

            JsonValue value;
            if (!arguments.TryGet(name, out value)) return arguments;

            if (value != null && value.Kind == JsonKind.String) taken = value.AsString;

            var copy = JsonValue.NewObject();
            foreach (var key in arguments.Keys)
            {
                if (key == name) continue;

                JsonValue item;
                if (arguments.TryGet(key, out item)) copy.Set(key, item);
            }

            return copy;
        }

        /// <summary>
        /// 目标文档核对。
        ///
        /// 这是**前置条件**，不是事后提示：切换警告是在工具执行完之后才附加的，
        /// 那时模型已经改完了另一个项目。所以核对必须发生在主线程上、执行之前，
        /// 不匹配就一个字节都不动。
        ///
        /// 比较对活动文档的两种写法都认：完整路径（list_documents 的 id）和标题。
        /// 模型手里常常只有其中一个，为此让它多查一次没有意义。
        /// </summary>
        private static void RequireExpectedDocument(string expected, ContextIdentity identity)
        {
            if (string.IsNullOrWhiteSpace(expected)) return;

            var wanted = expected.Trim();

            if (identity == null)
                throw new ToolFailureException(McpDomainError.WrongDocument,
                    "读不到当前活动文档的身份，无法确认它就是 expectedDocumentId 指定的「" + wanted +
                    "」。为安全起见没有执行。");

            if (Matches(identity.Key, wanted) || Matches(identity.Label, wanted)) return;

            throw new ToolFailureException(McpDomainError.WrongDocument,
                "expectedDocumentId 是「" + wanted + "」，但此刻的活动文档是「" +
                (identity.Label ?? identity.Key ?? "未知") + "」，这次写入没有执行，两个文档都没有被改动。" +
                "用 revit_list_documents 看看现在开着哪些文档；要改的那个不是活动文档时，" +
                "先用 revit_open_document 把它激活。**之前查到的构件 ID 属于原来那个文档，需要重新查询。**");
        }

        private static bool Matches(string actual, string wanted)
        {
            return !string.IsNullOrEmpty(actual) &&
                   string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase);
        }

        public IReadOnlyList<ToolDefinition> ListTools() => _registry.Definitions;

        /// <summary>不关心进度时的便利重载。</summary>
        public Task<ToolCallResult> CallToolAsync(
            string name, JsonValue arguments, CancellationToken cancellationToken) =>
            CallToolAsync(name, arguments, NullProgressSink.Instance, cancellationToken);

        public async Task<ToolCallResult> CallToolAsync(
            string name, JsonValue arguments, IProgressSink progress, CancellationToken cancellationToken)
        {
            // 审计要回答的是"模型到底对这个项目做了什么"，所以每条路径都要记一条，
            // 包括被写保护拒掉的——一串被拒的写请求本身就是值得看见的信号。
            // 初值设成"被拒"：没走到执行那一步就是没走到
            var entry = new ToolAuditEntry
            {
                ToolName = name,
                Arguments = ArgumentSummary.Of(arguments),
                Outcome = ToolOutcome.Rejected
            };

            var stopwatch = Stopwatch.StartNew();
            try
            {
                return await ExecuteAsync(name, arguments, entry, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                entry.DurationMs = stopwatch.ElapsedMilliseconds;
                try { _options.Audit?.Invoke(entry); }
                catch { /* 审计写失败绝不能影响调用本身的结果 */ }
            }
        }

        private async Task<ToolCallResult> ExecuteAsync(
            string name, JsonValue arguments, ToolAuditEntry entry, IProgressSink progress,
            CancellationToken cancellationToken)
        {
            // 工具不存在属于请求结构问题，模型很难自我纠正 → 走 JSON-RPC 错误而非 isError
            if (!_registry.TryGet(name, out var tool)) throw new ToolNotFoundException(name);

            entry.ReadOnly = tool.IsReadOnly;

            // 写工具的 expectedDocumentId / requestKey 不属于任何一个工具的 Input DTO：
            // 先摘出来，免得严格绑定把它当成未知参数拒掉
            string expectedDocument = null;
            string requestKey = null;

            if (!tool.IsReadOnly)
            {
                arguments = Take(arguments, SchemaGenerator.ExpectedDocumentParameter, out expectedDocument);
                arguments = Take(arguments, SchemaGenerator.RequestKeyParameter, out requestKey);
            }

            object input;
            try
            {
                input = JsonMapper.Bind(arguments, tool.Binding.InputType);
            }
            catch (ToolInputException ex)
            {
                // 参数错误反而要走 isError：模型看得见就能改对再来一次
                return Failure(entry, ToolOutcome.Rejected, McpDomainError.InvalidParameter, ex.Message);
            }

            var writeEnabled = Invoke(_options.WriteEnabled, false);
            if (!tool.IsReadOnly && !writeEnabled)
            {
                return Failure(entry, ToolOutcome.Rejected, McpDomainError.WriteDisabled,
                    "工具 " + name + " 会修改模型，但当前处于浏览模式。" +
                    "请让用户在 Revit 的 RevitMCP 选项卡上，把「操作模式」从「浏览模型」切换到「修改模型」。");
            }

            // 幂等：同键同参直接还上次的回执，一个字节都不执行。
            // 这一步必须在写保护检查**之后**——被写保护拒掉的调用不该登记成一次操作
            OperationRecord replayed;
            var idempotency = CheckIdempotency(name, requestKey, expectedDocument, arguments, out replayed);
            if (idempotency != null) return Failure(entry, ToolOutcome.Rejected, idempotency.Item1, idempotency.Item2);
            if (replayed != null) return Replay(entry, replayed);

            // 写工具一律登记：即便没给 requestKey，也要有 operationId——
            // 超时那一刻，它是调用方唯一能拿来查证的东西
            var operation = tool.IsReadOnly
                ? null
                : _journal.Begin(name, requestKey, IdentityOf(name, requestKey, expectedDocument, arguments),
                    expectedDocument);

            var timeout = TimeSpan.FromSeconds(Math.Max(1,
                tool.Metadata.TimeoutSeconds > 0
                    ? tool.Metadata.TimeoutSeconds
                    : Invoke(_options.DefaultTimeoutSeconds, 60)));

            // 警告在主线程上被写入、在 HTTP 线程上被读取，中间隔着一次 await——
            // 用并发集合而非 List，省掉一个只在"事务刚好产生警告"时才现形的竞态
            var warnings = new ConcurrentQueue<string>();
            var context = new ToolExecutionContext<TContext>(
                default(TContext), writeEnabled, Invoke(_options.MaxElementsPerWrite, 500),
                cancellationToken, new ConcurrentQueueAdapter(warnings),
                progress ?? NullProgressSink.Instance);

            // 在编组后的主线程上读到的身份。工具抛异常时它也已经被赋过值了，
            // 所以失败路径同样能告诉模型"你手里的 ID 属于另一个文档"——
            // 那恰恰是最需要这句话的时候
            ContextIdentity identity = null;

            try
            {
                var scopeInfo = new WriteScopeInfo(
                    name, new ConcurrentQueueAdapter(warnings), cancellationToken);

                var output = await _dispatcher.InvokeAsync(
                    host =>
                    {
                        if (_contextIdentity != null)
                        {
                            try { identity = _contextIdentity(host); }
                            catch { /* 读不到身份不值得让整个调用失败 */ }
                        }

                        // 目标核对与写开关复核都必须在**主线程上、动手之前**。
                        // 排队期间用户可能切了文档、也可能把「修改模型」关掉了；
                        // 入队时读到的那两个值，到这里已经不一定还成立
                        if (!tool.IsReadOnly)
                        {
                            RequireExpectedDocument(expectedDocument, identity);

                            if (!Invoke(_options.WriteEnabled, false))
                                throw new ToolFailureException(McpDomainError.WriteDisabled,
                                    "排队期间用户把「操作模式」切回了「浏览模型」，这次写入没有执行。");
                        }

                        // 前置条件都过了才算"跑起来了"：在这之前失败的，模型一个字节都没动过，
                        // 状态该是 failed 而不是 rolledBack
                        _journal.MarkRunning(operation);

                        var hosted = WithHost(context, host);

                        // 只读工具不开事务：既省一次 Revit 事务开销，
                        // 也保证"只读"这个承诺在实现上真的成立。
                        // WithoutTransaction 的工具受写保护管辖但同样不开事务（见该标志的说明）
                        return tool.IsReadOnly || tool.Metadata.WithoutTransaction
                            ? tool.Binding.Invoke(input, hosted)
                            : _writeScope.Run(host, scopeInfo, () => tool.Binding.Invoke(input, hosted));
                    },
                    timeout,
                    cancellationToken).ConfigureAwait(false);

                var switched = PeekContextSwitch(identity);
                if (switched != null) warnings.Enqueue(switched);
                CommitIdentity(identity);

                var payload = AttachWarnings(JsonMapper.ToJson(output), warnings);
                payload = AttachOperationId(payload, operation);

                entry.Outcome = ToolOutcome.Succeeded;
                entry.WarningCount = warnings.Count;

                var reporter = output as IReportsAffectedElements;
                if (reporter != null) entry.AffectedElements = reporter.AffectedElements;

                var text = payload.ToJson(indented: true);
                _journal.Complete(operation, OperationState.Committed, resultJson: payload.ToJson());

                // 规范建议：返回 structuredContent 的同时，也把序列化后的 JSON 放进文本块
                return ToolCallResult.Ok(text, payload);
            }
            catch (OperationCanceledException)
            {
                // 排队期间被取消 vs 执行中被取消：前者模型没被碰过
                _journal.Complete(operation,
                    operation != null && operation.State == OperationState.Queued
                        ? OperationState.Cancelled
                        : OperationState.RolledBack,
                    McpDomainError.Timeout, "调用被取消。");

                throw;   // 客户端断开，由传输层处理
            }
            catch (ToolFailureException ex)
            {
                // "找不到这个构件"配上"文档换了"，模型立刻知道该重新查而不是换个 ID 再试
                var switched = PeekContextSwitch(identity);
                CommitIdentity(identity);

                // 跑起来之后失败 = 事务已回滚；跑起来之前失败 = 压根没进事务
                Settle(operation, ex.Code, ex.Message);

                return Failure(entry, ToolOutcome.Failed, ex.Code,
                    switched == null ? ex.Message : ex.Message + "\n注意：" + switched);
            }
            catch (ToolInputException ex)
            {
                // 有些校验只有拿到 Revit 上下文才做得了
                Settle(operation, McpDomainError.InvalidParameter, ex.Message);
                return Failure(entry, ToolOutcome.Failed, McpDomainError.InvalidParameter, ex.Message);
            }
            catch (RevitBusyException ex)
            {
                _options.Log(name + "：" + ex.Message, null);
                // 工作从未开始，模型没被碰过——记 Rejected 而非 Failed。
                // 审计上的这条区分和 REVIT_BUSY / TIMEOUT 的区分是同一件事
                _journal.Complete(operation, OperationState.Cancelled, McpDomainError.RevitBusy, ex.Message);
                return Failure(entry, ToolOutcome.Rejected, McpDomainError.RevitBusy, ex.Message);
            }
            catch (DispatchTimeoutException ex)
            {
                _options.Log(name + "：" + ex.Message, null);

                // **结果不确定**：工作已经开始，主线程还在跑，谁也不知道它会不会提交。
                // 这正是 operationId 存在的那一刻——把它交出去，让调用方去查证而不是去重试
                _journal.Complete(operation, OperationState.Unknown, McpDomainError.Timeout, ex.Message);

                return Failure(entry, ToolOutcome.Failed, McpDomainError.Timeout,
                    ex.Message + Uncertain(operation));
            }
            catch (DispatchStoppedException ex)
            {
                _journal.Complete(operation, OperationState.Unknown, McpDomainError.ServerStopped, ex.Message);
                return Failure(entry, ToolOutcome.Rejected, McpDomainError.ServerStopped, ex.Message);
            }
            catch (Exception ex)
            {
                _options.Log(name + " 执行失败。", ex);
                Settle(operation, null, ex.Message);

                // 不把堆栈丢给模型：它既看不懂也帮不上忙，只会占上下文
                return Failure(entry, ToolOutcome.Failed, null, name + " 执行失败：" + ex.Message);
            }
        }

        private static ToolExecutionContext<TContext> WithHost(ToolExecutionContext<TContext> template, TContext host) =>
            new ToolExecutionContext<TContext>(
                host, template.WriteEnabled, template.MaxElementsPerWrite, template.CancellationToken,
                template.Warnings, template.Progress);

        /// <summary>
        /// 把被抑制的警告并入工具输出。
        /// 工具失败时不走这里——失败文本本身已经说明了原因，再挂一串警告只会喧宾夺主。
        /// </summary>
        private static JsonValue AttachWarnings(JsonValue payload, ConcurrentQueue<string> warnings)
        {
            if (warnings.Count == 0 || payload == null || !payload.IsObject) return payload;

            var array = JsonValue.NewArray();
            foreach (var warning in warnings) array.Add(JsonValue.String(warning));

            // 工具自己也有 warnings 字段时绝不能直接盖掉。
            //
            // 实测踩过一次：revit_get_warnings 的输出字段恰好同名，管线一挂上去，
            // 工具查到的警告数据就整个消失了——而且消失得悄无声息，
            // total、groupCount 这些兄弟字段还在，只有数组被换了内容，
            // 看输出的人会以为是工具没查到。
            var key = payload["warnings"] == null ? "warnings" : "serverWarnings";
            return payload.Set(key, array);
        }

        /// <summary>把 ConcurrentQueue 装成 IList 的只进不出视图：工具只会往里 Add。</summary>
        private sealed class ConcurrentQueueAdapter : IList<string>
        {
            private readonly ConcurrentQueue<string> _queue;

            public ConcurrentQueueAdapter(ConcurrentQueue<string> queue) => _queue = queue;

            public void Add(string item) { if (!string.IsNullOrWhiteSpace(item)) _queue.Enqueue(item); }
            public int Count => _queue.Count;
            public bool IsReadOnly => false;
            public IEnumerator<string> GetEnumerator() => _queue.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            public bool Contains(string item) => System.Linq.Enumerable.Contains(_queue, item);
            public void CopyTo(string[] array, int arrayIndex) => _queue.CopyTo(array, arrayIndex);
            public int IndexOf(string item) => throw new NotSupportedException();

            // 以下都不该被调用：警告只应追加，删改历史等于抹掉证据
            public string this[int index]
            {
                get => System.Linq.Enumerable.ElementAt(_queue, index);
                set => throw new NotSupportedException();
            }
            public void Clear() => throw new NotSupportedException();
            public void Insert(int index, string item) => throw new NotSupportedException();
            public bool Remove(string item) => throw new NotSupportedException();
            public void RemoveAt(int index) => throw new NotSupportedException();
        }

        // ---------- 幂等与操作状态 ----------

        /// <summary>
        /// 这次调用的身份指纹：工具 + 幂等键 + 目标文档 + 规范化后的参数。
        ///
        /// 参数要走**规范化**序列化：客户端重试时重新序列化一遍，key 的顺序很可能变
        /// （多数语言的字典不保证顺序），拿普通 JSON 去比就会把同一次请求判成不同的请求。
        /// 目标文档进指纹：同一批参数打到另一个文档上是**另一次操作**，结果不能复用。
        /// </summary>
        private static string IdentityOf(string name, string requestKey, string expectedDocument, JsonValue arguments)
        {
            return JsonCanonical.Fingerprint(
                name, requestKey, expectedDocument,
                JsonCanonical.Write(arguments ?? JsonValue.NewObject()));
        }

        /// <summary>
        /// 幂等检查。返回非 null 表示要直接拒绝（错误码 + 文案）；
        /// <paramref name="replay"/> 非 null 表示要把上次的回执原样还回去。
        /// </summary>
        private Tuple<string, string> CheckIdempotency(
            string name, string requestKey, string expectedDocument, JsonValue arguments,
            out OperationRecord replay)
        {
            replay = null;
            if (string.IsNullOrWhiteSpace(requestKey)) return null;

            var previous = _journal.FindByRequestKey(requestKey);
            if (previous == null) return null;

            var identity = IdentityOf(name, requestKey, expectedDocument, arguments);

            if (!string.Equals(previous.IdentityHash, identity, StringComparison.Ordinal))
                return Tuple.Create(McpDomainError.IdempotencyConflict,
                    "requestKey \"" + requestKey + "\" 上次用在另一组参数上（操作 " + previous.OperationId +
                    "，工具 " + previous.ToolName + "，" + previous.QueuedAtUtc.ToString("HH:mm:ss") +
                    " UTC，状态 " + State(previous.State) + "）。" +
                    "**这次没有执行。**同一个键必须配同一份参数——" +
                    "键被复用却改了参数，执行下去会造出一个谁都没预期的东西。换一个 requestKey。");

            if (!previous.IsTerminal)
                return Tuple.Create(McpDomainError.OperationInFlight,
                    "requestKey \"" + requestKey + "\" 的上一次调用（" + previous.OperationId +
                    "）还没结束，这次没有执行。用 revit_get_operation_status 查它，别另发一次。");

            replay = previous;
            return null;
        }

        /// <summary>把上次的结果原样还回去，并说清楚"这是重放，没有再执行一次"。</summary>
        private static ToolCallResult Replay(ToolAuditEntry entry, OperationRecord previous)
        {
            entry.Outcome = ToolOutcome.Succeeded;

            var note = "这是重放：requestKey \"" + previous.RequestKey + "\" 已经执行过（操作 " +
                       previous.OperationId + "，" + previous.QueuedAtUtc.ToString("HH:mm:ss") +
                       " UTC，状态 " + State(previous.State) + "），**本次没有再执行一遍**。";

            if (previous.State != OperationState.Committed)
            {
                // 上次就没成功：不能装作成功，但也不能假装没发生过
                return Failure(entry, ToolOutcome.Rejected,
                    previous.ErrorCode ?? McpDomainError.IdempotencyConflict,
                    note + " 上次的结果是：" + (previous.ErrorMessage ?? "（没有记录）") +
                    " 要重来请换一个 requestKey。");
            }

            if (previous.ResultJson == null)
                return ToolCallResult.Ok(
                    note + " 上次的回执太大没有保留，" +
                    (previous.AffectedElementIds.Count > 0
                        ? "涉及构件：" + string.Join("、", previous.AffectedElementIds.ToArray())
                        : "请用 revit_query_elements 查证实际结果。"));

            JsonValue payload;
            try { payload = JsonValue.Parse(previous.ResultJson); }
            catch { return ToolCallResult.Ok(note + " 上次的回执无法还原，请用 revit_query_elements 查证。"); }

            if (payload.IsObject) AppendWarning(payload, note);

            return ToolCallResult.Ok(payload.ToJson(indented: true), payload);
        }

        /// <summary>
        /// 把 operationId 挂进回执。
        ///
        /// 放 structuredContent 而不是 `_meta`：有些客户端会把 `_meta` 直接丢掉，
        /// 而这个字段恰恰在超时那一刻最值钱。代价是每个写回执多约 30 字节。
        /// </summary>
        private static JsonValue AttachOperationId(JsonValue payload, OperationRecord operation)
        {
            if (operation == null || payload == null || !payload.IsObject) return payload;

            JsonValue existing;
            if (payload.TryGet("operationId", out existing)) return payload;   // 工具自己有同名字段就不动它

            return payload.Set("operationId", operation.OperationId);
        }

        private static void AppendWarning(JsonValue payload, string note)
        {
            JsonValue warnings;
            if (payload.TryGet("warnings", out warnings) && warnings.IsArray)
            {
                warnings.Add(JsonValue.String(note));
                return;
            }

            var array = JsonValue.NewArray();
            array.Add(JsonValue.String(note));
            payload.Set("warnings", array);
        }

        /// <summary>
        /// 跑起来之后失败 = 事务已回滚（模型原样）；跑起来之前失败 = 压根没进事务。
        /// 两者对调用方的意义不同：前者可以原样重试，后者得先把前置条件弄对。
        /// </summary>
        private void Settle(OperationRecord operation, string code, string message)
        {
            if (operation == null) return;

            _journal.Complete(operation,
                operation.State == OperationState.Running ? OperationState.RolledBack : OperationState.Failed,
                code, message);
        }

        private static string Uncertain(OperationRecord operation)
        {
            if (operation == null) return string.Empty;

            return "\noperationId=" + operation.OperationId +
                   "\n**结果不确定，不要直接重试。** 先用 revit_get_operation_status 查这个 ID；" +
                   "状态若是 unknown，再用 revit_get_model_changes 或按类别、位置查一遍那批构件——" +
                   "在 Revit 里重复创建最难发现：不报错、不产生警告、撤销栈里只是一步普通的创建。";
        }

        private static string State(OperationState state)
        {
            switch (state)
            {
                case OperationState.Queued: return "排队中";
                case OperationState.Running: return "执行中";
                case OperationState.Committed: return "已提交";
                case OperationState.RolledBack: return "已回滚";
                case OperationState.Failed: return "失败（未进入事务）";
                case OperationState.Cancelled: return "已取消（未执行）";
                default: return "未知";
            }
        }

        private static ToolCallResult Failure(
            ToolAuditEntry entry, ToolOutcome outcome, string code, string message)
        {
            entry.Outcome = outcome;
            entry.ErrorCode = code;
            return ToolCallResult.Failure(code == null ? message : McpDomainError.Format(code, message));
        }

        private static T Invoke<T>(Func<T> accessor, T fallback)
        {
            try { return accessor == null ? fallback : accessor(); }
            catch { return fallback; }
        }
    }
}
