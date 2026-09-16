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
            Func<TContext, ContextIdentity> contextIdentity = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _options = options ?? new ToolPipelineOptions();
            _writeScope = writeScope ?? new PassthroughWriteScope<TContext>();
            _contextIdentity = contextIdentity;
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
                var scopeInfo = new WriteScopeInfo(name, new ConcurrentQueueAdapter(warnings));

                var output = await _dispatcher.InvokeAsync(
                    host =>
                    {
                        if (_contextIdentity != null)
                        {
                            try { identity = _contextIdentity(host); }
                            catch { /* 读不到身份不值得让整个调用失败 */ }
                        }

                        var hosted = WithHost(context, host);

                        // 只读工具不开事务：既省一次 Revit 事务开销，
                        // 也保证"只读"这个承诺在实现上真的成立
                        return tool.IsReadOnly
                            ? tool.Binding.Invoke(input, hosted)
                            : _writeScope.Run(host, scopeInfo, () => tool.Binding.Invoke(input, hosted));
                    },
                    timeout,
                    cancellationToken).ConfigureAwait(false);

                var switched = PeekContextSwitch(identity);
                if (switched != null) warnings.Enqueue(switched);
                CommitIdentity(identity);

                var payload = AttachWarnings(JsonMapper.ToJson(output), warnings);

                entry.Outcome = ToolOutcome.Succeeded;
                entry.WarningCount = warnings.Count;

                var reporter = output as IReportsAffectedElements;
                if (reporter != null) entry.AffectedElements = reporter.AffectedElements;

                // 规范建议：返回 structuredContent 的同时，也把序列化后的 JSON 放进文本块
                return ToolCallResult.Ok(payload.ToJson(indented: true), payload);
            }
            catch (OperationCanceledException)
            {
                throw;   // 客户端断开，由传输层处理
            }
            catch (ToolFailureException ex)
            {
                // "找不到这个构件"配上"文档换了"，模型立刻知道该重新查而不是换个 ID 再试
                var switched = PeekContextSwitch(identity);
                CommitIdentity(identity);

                return Failure(entry, ToolOutcome.Failed, ex.Code,
                    switched == null ? ex.Message : ex.Message + "\n注意：" + switched);
            }
            catch (ToolInputException ex)
            {
                // 有些校验只有拿到 Revit 上下文才做得了
                return Failure(entry, ToolOutcome.Failed, McpDomainError.InvalidParameter, ex.Message);
            }
            catch (RevitBusyException ex)
            {
                _options.Log(name + "：" + ex.Message, null);
                // 工作从未开始，模型没被碰过——记 Rejected 而非 Failed。
                // 审计上的这条区分和 REVIT_BUSY / TIMEOUT 的区分是同一件事
                return Failure(entry, ToolOutcome.Rejected, McpDomainError.RevitBusy, ex.Message);
            }
            catch (DispatchTimeoutException ex)
            {
                _options.Log(name + "：" + ex.Message, null);
                // 已经跑起来了，模型可能已被部分修改
                return Failure(entry, ToolOutcome.Failed, McpDomainError.Timeout, ex.Message);
            }
            catch (DispatchStoppedException ex)
            {
                return Failure(entry, ToolOutcome.Rejected, McpDomainError.ServerStopped, ex.Message);
            }
            catch (Exception ex)
            {
                _options.Log(name + " 执行失败。", ex);
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
