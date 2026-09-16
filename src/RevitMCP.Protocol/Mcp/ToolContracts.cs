using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;

namespace RevitMCP.Protocol.Mcp
{
    /// <summary>
    /// 协议层看到的工具定义。刻意只依赖 JSON，不认识 Revit——
    /// M3 的工具框架负责把 [McpTool] 标注的类映射成这个结构。
    /// </summary>
    public sealed class ToolDefinition
    {
        public ToolDefinition(string name, string title, string description, JsonValue inputSchema)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("工具名不能为空。", nameof(name));
            Name = name;
            Title = title;
            Description = description;
            // 规范要求 inputSchema 必须是合法的 JSON Schema 对象，不能为 null
            InputSchema = inputSchema ?? JsonValue.NewObject()
                .Set("type", "object")
                .Set("additionalProperties", false);
        }

        public string Name { get; }
        public string Title { get; }
        public string Description { get; }
        public JsonValue InputSchema { get; }

        public JsonValue ToJson()
        {
            var json = JsonValue.NewObject().Set("name", Name);
            if (!string.IsNullOrEmpty(Title)) json.Set("title", Title);
            if (!string.IsNullOrEmpty(Description)) json.Set("description", Description);
            json.Set("inputSchema", InputSchema);
            return json;
        }
    }

    /// <summary>
    /// 工具执行结果。
    /// 注意 <see cref="IsError"/> 与 JSON-RPC 错误的区别：工具**执行**失败走这里，
    /// 模型能看到失败原因并自我纠正；只有请求结构本身有问题才返回 JSON-RPC 错误。
    /// </summary>
    public sealed class ToolCallResult
    {
        private ToolCallResult(string text, bool isError, JsonValue structuredContent)
        {
            Text = text ?? string.Empty;
            IsError = isError;
            StructuredContent = structuredContent;
        }

        public string Text { get; }
        public bool IsError { get; }
        public JsonValue StructuredContent { get; }

        public static ToolCallResult Ok(string text, JsonValue structuredContent = null) =>
            new ToolCallResult(text, false, structuredContent);

        public static ToolCallResult Failure(string text) =>
            new ToolCallResult(text, true, null);

        public JsonValue ToJson(McpRequestContext context)
        {
            var content = JsonValue.NewArray()
                .Add(JsonValue.NewObject().Set("type", "text").Set("text", Text));

            var result = context.NewResult().Set("content", content);
            if (StructuredContent != null) result.Set("structuredContent", StructuredContent);
            result.Set("isError", IsError);
            return result;
        }
    }

    /// <summary>工具的来源。M1 用空实现，M3 换成 Revit 工具注册表。</summary>
    public interface IToolCatalog
    {
        /// <summary>规范要求顺序稳定，以便客户端缓存工具列表。</summary>
        IReadOnlyList<ToolDefinition> ListTools();

        /// <summary>
        /// 工具不存在时抛 <see cref="ToolNotFoundException"/>。
        /// <paramref name="progress"/> 在客户端没要进度时是 <see cref="NullProgressSink"/>，永远不为 null。
        /// </summary>
        Task<ToolCallResult> CallToolAsync(
            string name, JsonValue arguments, IProgressSink progress, CancellationToken cancellationToken);
    }

    public sealed class ToolNotFoundException : Exception
    {
        public ToolNotFoundException(string name) : base("未知工具：" + name)
        {
            ToolName = name;
        }

        public string ToolName { get; }
    }

    /// <summary>M1 占位：声明 tools 能力但暂时没有工具，握手与列表调用都能正常完成。</summary>
    public sealed class EmptyToolCatalog : IToolCatalog
    {
        private static readonly ToolDefinition[] None = new ToolDefinition[0];

        public IReadOnlyList<ToolDefinition> ListTools() => None;

        public Task<ToolCallResult> CallToolAsync(
            string name, JsonValue arguments, IProgressSink progress, CancellationToken cancellationToken) =>
            throw new ToolNotFoundException(name);
    }
}
