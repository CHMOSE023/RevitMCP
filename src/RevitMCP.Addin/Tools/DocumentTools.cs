using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    public sealed class ListDocumentsInput
    {
        [McpParam("true 时把链接进来的模型也列出来，默认 false")]
        public bool? IncludeLinked { get; set; }
    }

    public sealed class OpenDocumentInfo
    {
        [McpParam("文档 ID。其他只读工具的 documentId 参数用的就是它")]
        public string Id { get; set; }

        [McpParam("文档标题")]
        public string Title { get; set; }

        [McpParam("完整路径；未保存的文档为 null")]
        public string Path { get; set; }

        [McpParam("是否为当前活动文档。**写操作只作用于活动文档**")]
        public bool IsActive { get; set; }

        [McpParam("是否为族文档。族文档与项目文档是两套 API 语义，本服务的工具基本不适用")]
        public bool IsFamilyDocument { get; set; }

        [McpParam("是否为被链接进来的模型")]
        public bool IsLinked { get; set; }

        [McpParam("是否启用了工作共享")]
        public bool IsWorkshared { get; set; }

        [McpParam("是否只读打开")]
        public bool IsReadOnly { get; set; }
    }

    public sealed class ListDocumentsOutput
    {
        [McpParam("打开的文档数")]
        public int Total { get; set; }

        [McpParam("当前活动文档的 ID")]
        public string ActiveId { get; set; }

        [McpParam("文档列表，活动的排在最前")]
        public List<OpenDocumentInfo> Documents { get; set; } = new List<OpenDocumentInfo>();
    }

    [McpTool("revit_list_documents",
        Title = "列出打开的文档",
        Description = "列出这个 Revit 实例里打开的所有项目文档。" +
                      "一个 Revit 可以同时开着十几个项目——要挨个检查它们，" +
                      "先用它拿到每个文档的 ID，再把 ID 传给各个只读工具的 documentId 参数。" +
                      "**不需要也不应该为了查询去切换活动文档**，那会打断用户正在看的东西。",
        ReadOnly = true,
        TimeoutSeconds = 30)]
    public sealed class ListDocumentsTool : RevitTool<ListDocumentsInput, ListDocumentsOutput>
    {
        public override ListDocumentsOutput Execute(
            ListDocumentsInput input, ToolExecutionContext<UIApplication> context)
        {
            var host = context.Host;
            if (host == null)
                throw new ToolFailureException(McpDomainError.NoActiveDocument, "Revit 上下文不可用。");

            var active = DocumentRef.ActiveOf(host);
            var activeKey = DocumentRef.KeyOf(active);

            var documents = new List<OpenDocumentInfo>();

            foreach (var document in DocumentRef.Opened(host, input.IncludeLinked == true))
            {
                var key = DocumentRef.KeyOf(document);
                if (key == null) continue;

                documents.Add(new OpenDocumentInfo
                {
                    Id = key,
                    Title = SafeString(() => document.Title),
                    Path = NullIfEmpty(SafeString(() => document.PathName)),
                    IsActive = activeKey != null && string.Equals(key, activeKey, StringComparison.OrdinalIgnoreCase),
                    IsFamilyDocument = SafeBool(() => document.IsFamilyDocument),
                    IsLinked = SafeBool(() => document.IsLinked),
                    IsWorkshared = SafeBool(() => document.IsWorkshared),
                    IsReadOnly = SafeBool(() => document.IsReadOnly)
                });
            }

            var output = new ListDocumentsOutput
            {
                Total = documents.Count,
                ActiveId = activeKey,
                Documents = documents.OrderByDescending(d => d.IsActive)
                                     .ThenBy(d => d.Title, StringComparer.CurrentCulture)
                                     .ToList()
            };

            if (output.Total == 0)
                context.Warnings.Add("Revit 中没有打开任何文档。");

            // 同名文档会让 documentId 失去意义——未保存的新建文档最容易撞
            var duplicates = documents.GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                                      .Where(g => g.Count() > 1)
                                      .Select(g => g.Key)
                                      .ToList();

            if (duplicates.Count > 0)
                context.Warnings.Add(
                    "有同 ID 的文档：" + string.Join("、", duplicates.ToArray()) +
                    "。未保存的文档只能用标题标识，撞名时 documentId 会指向其中任意一个——" +
                    "把它们保存到磁盘就能区分开。");

            return output;
        }

        private static string SafeString(Func<string> read)
        {
            try { return read(); }
            catch { return null; }
        }

        private static bool SafeBool(Func<bool> read)
        {
            try { return read(); }
            catch { return false; }
        }

        private static string NullIfEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
