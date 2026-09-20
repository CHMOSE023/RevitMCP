using System.Collections.Generic;
using RevitMCP.Protocol.Json;

namespace RevitMCP.Protocol.Mcp
{
    /// <summary>
    /// 协议层看到的资源定义。
    ///
    /// 资源是"服务端愿意让客户端读的文档"，与工具的区别是它没有副作用、也不需要参数：
    /// 客户端列一遍、按 URI 读一份就行。本服务用它发布**建模指引**——
    /// 指引必须跟着服务走，不能指望每个客户端先手工装一份文件：
    /// 装漏了没人知道，装旧了更糟——Agent 会对着已经修好的缺陷执行补救动作。
    /// </summary>
    public sealed class ResourceDefinition
    {
        public ResourceDefinition(string uri, string name, string description,
            string mimeType = "text/markdown", string title = null)
        {
            Uri = uri;
            Name = name;
            Description = description;
            MimeType = mimeType;
            Title = title;
        }

        /// <summary>资源 URI。客户端用它来读。</summary>
        public string Uri { get; }

        /// <summary>给程序看的名字。</summary>
        public string Name { get; }

        /// <summary>给人和模型看的标题。</summary>
        public string Title { get; }

        /// <summary>这份东西讲什么、什么时候该读它。**这句话决定它会不会被读**。</summary>
        public string Description { get; }

        public string MimeType { get; }

        public JsonValue ToJson()
        {
            var json = JsonValue.NewObject()
                .Set("uri", Uri)
                .Set("name", Name);

            if (!string.IsNullOrEmpty(Title)) json.Set("title", Title);
            if (!string.IsNullOrEmpty(Description)) json.Set("description", Description);
            if (!string.IsNullOrEmpty(MimeType)) json.Set("mimeType", MimeType);

            return json;
        }
    }

    /// <summary>一份资源的内容。目前只有文本。</summary>
    public sealed class ResourceContents
    {
        public ResourceContents(string uri, string text, string mimeType = "text/markdown")
        {
            Uri = uri;
            Text = text ?? string.Empty;
            MimeType = mimeType;
        }

        public string Uri { get; }
        public string Text { get; }
        public string MimeType { get; }

        public JsonValue ToJson() =>
            JsonValue.NewObject()
                .Set("uri", Uri)
                .Set("mimeType", MimeType)
                .Set("text", Text);
    }

    public interface IResourceCatalog
    {
        IReadOnlyList<ResourceDefinition> ListResources();

        /// <summary>按 URI 取内容。没有这份资源时返回 null——由分发层翻译成协议错误。</summary>
        ResourceContents ReadResource(string uri);
    }

    /// <summary>没有资源可发布时的兜底。<c>resources/*</c> 随之从能力声明里消失。</summary>
    public sealed class EmptyResourceCatalog : IResourceCatalog
    {
        private static readonly ResourceDefinition[] None = new ResourceDefinition[0];

        public IReadOnlyList<ResourceDefinition> ListResources() => None;

        public ResourceContents ReadResource(string uri) => null;
    }
}
