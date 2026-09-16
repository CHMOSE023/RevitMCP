using System;
using System.Collections.Generic;
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

        public ToolPipeline(
            ToolRegistry<TContext> registry,
            IWorkDispatcher<TContext> dispatcher,
            ToolPipelineOptions options = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _options = options ?? new ToolPipelineOptions();
        }

        public IReadOnlyList<ToolDefinition> ListTools() => _registry.Definitions;

        public async Task<ToolCallResult> CallToolAsync(
            string name, JsonValue arguments, CancellationToken cancellationToken)
        {
            // 工具不存在属于请求结构问题，模型很难自我纠正 → 走 JSON-RPC 错误而非 isError
            if (!_registry.TryGet(name, out var tool)) throw new ToolNotFoundException(name);

            object input;
            try
            {
                input = JsonMapper.Bind(arguments, tool.Binding.InputType);
            }
            catch (ToolInputException ex)
            {
                // 参数错误反而要走 isError：模型看得见就能改对再来一次
                return Failure(McpDomainError.InvalidParameter, ex.Message);
            }

            var writeEnabled = Invoke(_options.WriteEnabled, false);
            if (!tool.IsReadOnly && !writeEnabled)
            {
                return Failure(McpDomainError.WriteDisabled,
                    "工具 " + name + " 会修改模型，但写入模式当前未开启。" +
                    "请让用户在 Revit 的 RevitMCP 面板上点击「写入：关」将其开启。");
            }

            var timeout = TimeSpan.FromSeconds(Math.Max(1,
                tool.Metadata.TimeoutSeconds > 0
                    ? tool.Metadata.TimeoutSeconds
                    : Invoke(_options.DefaultTimeoutSeconds, 60)));

            var context = new ToolExecutionContext<TContext>(
                default(TContext), writeEnabled, Invoke(_options.MaxElementsPerWrite, 500), cancellationToken);

            try
            {
                var output = await _dispatcher.InvokeAsync(
                    host => tool.Binding.Invoke(input, WithHost(context, host)),
                    timeout,
                    cancellationToken).ConfigureAwait(false);

                var payload = JsonMapper.ToJson(output);

                // 规范建议：返回 structuredContent 的同时，也把序列化后的 JSON 放进文本块
                return ToolCallResult.Ok(payload.ToJson(indented: true), payload);
            }
            catch (OperationCanceledException)
            {
                throw;   // 客户端断开，由传输层处理
            }
            catch (ToolFailureException ex)
            {
                return Failure(ex.Code, ex.Message);
            }
            catch (ToolInputException ex)
            {
                // 有些校验只有拿到 Revit 上下文才做得了
                return Failure(McpDomainError.InvalidParameter, ex.Message);
            }
            catch (RevitBusyException ex)
            {
                _options.Log(name + "：" + ex.Message, null);
                return Failure(McpDomainError.RevitBusy, ex.Message);
            }
            catch (DispatchTimeoutException ex)
            {
                _options.Log(name + "：" + ex.Message, null);
                return Failure(McpDomainError.Timeout, ex.Message);
            }
            catch (DispatchStoppedException ex)
            {
                return Failure(McpDomainError.ServerStopped, ex.Message);
            }
            catch (Exception ex)
            {
                _options.Log(name + " 执行失败。", ex);
                // 不把堆栈丢给模型：它既看不懂也帮不上忙，只会占上下文
                return Failure(null, name + " 执行失败：" + ex.Message);
            }
        }

        private static ToolExecutionContext<TContext> WithHost(ToolExecutionContext<TContext> template, TContext host) =>
            new ToolExecutionContext<TContext>(
                host, template.WriteEnabled, template.MaxElementsPerWrite, template.CancellationToken);

        private static ToolCallResult Failure(string code, string message) =>
            ToolCallResult.Failure(code == null ? message : McpDomainError.Format(code, message));

        private static T Invoke<T>(Func<T> accessor, T fallback)
        {
            try { return accessor == null ? fallback : accessor(); }
            catch { return fallback; }
        }
    }
}
