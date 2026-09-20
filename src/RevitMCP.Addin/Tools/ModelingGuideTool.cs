using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Guide;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    public sealed class GetModelingGuideInput
    {
        [McpParam("要读哪一节：overview（建模总则，省略即它）、sequence（顺序与批次）、" +
                  "validation（验收清单：每类构件建完量什么）、recovery（失败与超时后怎么办）、" +
                  "limitations（版本限制与**已经修好、不必再绕**的坑）",
                  AllowedValues = new[] { "overview", "sequence", "validation", "recovery", "limitations" })]
        public string Section { get; set; }
    }

    public sealed class GuideSectionInfo
    {
        [McpParam("节名，作为本工具的 section 参数")]
        public string Name { get; set; }

        [McpParam("标题")]
        public string Title { get; set; }

        [McpParam("什么时候该读它")]
        public string Description { get; set; }

        [McpParam("对应的资源 URI，支持 resources/read 的客户端也可以直接读它")]
        public string Uri { get; set; }
    }

    public sealed class GetModelingGuideOutput
    {
        [McpParam("本次返回的是哪一节")]
        public string Section { get; set; }

        [McpParam("标题")]
        public string Title { get; set; }

        [McpParam("正文，Markdown")]
        public string Content { get; set; }

        [McpParam("还有哪些节可读，以及各自什么时候该读")]
        public List<GuideSectionInfo> Available { get; set; } = new List<GuideSectionInfo>();
    }

    /// <summary>
    /// 把建模指引作为工具暴露出来。
    ///
    /// 同一份内容已经作为 MCP 资源发布（<c>resources/list</c> / <c>resources/read</c>），
    /// 这里再给一个工具入口，理由很实际：**不是每个客户端都会去列资源**。
    /// 几乎所有客户端都会加载 <c>tools/list</c>，于是工具是唯一一条
    /// "Agent 一定看得见"的路。资源那条留给支持它的客户端——
    /// 它更省上下文（按 URI 取，不进工具清单）。
    ///
    /// 只读、不需要打开任何文档：在决定怎么建模之前就该能读到它。
    /// </summary>
    [McpTool("revit_get_modeling_guide",
        Title = "读建模指引",
        Description = "读这套工具的建模指引。**开始建模任务前先读 overview**——" +
                      "这套工具的失败模式不是调用报错，而是调用成功、模型不对。" +
                      "建完某类构件后读 validation；失败或超时后读 recovery；" +
                      "**写绕行代码之前先读 limitations**（很多坑已经修了）。不需要打开文档。",
        ReadOnly = true,
        TimeoutSeconds = 30)]
    public sealed class GetModelingGuideTool : RevitTool<GetModelingGuideInput, GetModelingGuideOutput>
    {
        public override GetModelingGuideOutput Execute(
            GetModelingGuideInput input, ToolExecutionContext<UIApplication> context)
        {
            var section = ModelingGuide.Find(input.Section);

            if (section == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "这个版本的插件里没有带上建模指引（构建时没有嵌入）。请向维护者反馈。");

            if (!string.IsNullOrWhiteSpace(input.Section) &&
                !string.Equals(section.Name, input.Section.Trim(),
                    System.StringComparison.OrdinalIgnoreCase))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "没有名为 \"" + input.Section + "\" 的章节。可用的是：" +
                    string.Join("、", ModelingGuide.Sections.Select(s => s.Name).ToArray()) + "。");

            return new GetModelingGuideOutput
            {
                Section = section.Name,
                Title = section.Title,
                Content = section.Text,
                Available = ModelingGuide.Sections
                    .Where(s => s.Name != section.Name)
                    .Select(s => new GuideSectionInfo
                    {
                        Name = s.Name,
                        Title = s.Title,
                        Description = s.Description,
                        Uri = s.Uri
                    })
                    .ToList()
            };
        }
    }
}
