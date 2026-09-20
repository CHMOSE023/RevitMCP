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
    /// <summary>
    /// 工具的行为提示（规范里的 annotations）。
    ///
    /// 服务端本来就知道哪个工具只是看看、哪个会改模型、哪个会把东西删掉，
    /// 这些信息不传出去，客户端就只能对所有工具一视同仁——
    /// 要么全都弹确认（烦到没人看），要么全都不弹（该拦的没拦住）。
    ///
    /// 按规范，这些是**提示而非保证**：真正的闸门在服务端（写保护、规模阈值、删除预览），
    /// 客户端拿它决定要不要多问一句。
    /// </summary>
    public sealed class ToolAnnotations
    {
        public ToolAnnotations(bool readOnlyHint, bool destructiveHint)
        {
            ReadOnlyHint = readOnlyHint;
            DestructiveHint = destructiveHint;
        }

        /// <summary>只看不改。</summary>
        public bool ReadOnlyHint { get; }

        /// <summary>可能做出不易挽回的改动（删除尤其）。只读工具恒为 false。</summary>
        public bool DestructiveHint { get; }

        public JsonValue ToJson() =>
            JsonValue.NewObject()
                .Set("readOnlyHint", ReadOnlyHint)
                .Set("destructiveHint", DestructiveHint);
    }

    public sealed class ToolDefinition
    {
        public ToolDefinition(
            string name, string title, string description, JsonValue inputSchema,
            ToolAnnotations annotations = null)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("工具名不能为空。", nameof(name));
            Name = name;
            Title = title;
            Description = description;
            // 规范要求 inputSchema 必须是合法的 JSON Schema 对象，不能为 null
            InputSchema = inputSchema ?? JsonValue.NewObject()
                .Set("type", "object")
                .Set("additionalProperties", false);
            Annotations = annotations ?? new ToolAnnotations(false, false);
        }

        public string Name { get; }
        public string Title { get; }
        public string Description { get; }
        public JsonValue InputSchema { get; }
        public ToolAnnotations Annotations { get; }

        public JsonValue ToJson()
        {
            var json = JsonValue.NewObject().Set("name", Name);
            if (!string.IsNullOrEmpty(Title)) json.Set("title", Title);
            if (!string.IsNullOrEmpty(Description)) json.Set("description", Description);
            json.Set("inputSchema", InputSchema);
            json.Set("annotations", Annotations.ToJson());
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
            var content = JsonValue.NewArray();

            // 文本为空且有结构化输出时，连那个空文本块都不放——
            // 服务端被显式配成"只给 structuredContent"时才会走到这里，
            // 放一个空字符串块只会让客户端渲染出一片空白，看起来像工具什么都没返回
            if (Text.Length > 0 || StructuredContent == null)
                content.Add(JsonValue.NewObject().Set("type", "text").Set("text", Text));

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
