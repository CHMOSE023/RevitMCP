using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 文档信息 ====================

    public sealed class DocumentInfoInput
    {
    }

    public sealed class DocumentInfoOutput
    {
        [McpParam("文档标题")]
        public string Title { get; set; }

        [McpParam("完整路径；未保存的新文档为 null")]
        public string PathName { get; set; }

        [McpParam("是否为族文档")]
        public bool IsFamilyDocument { get; set; }

        [McpParam("是否启用了工作共享")]
        public bool IsWorkshared { get; set; }

        [McpParam("当前活动视图名")]
        public string ActiveView { get; set; }

        [McpParam("宿主 Revit 版本号")]
        public string RevitVersion { get; set; }

        [McpParam("写入模式是否已开启。未开启时所有修改类工具都会被拒绝")]
        public bool WriteEnabled { get; set; }
    }

    [McpTool("revit_get_document_info",
        Title = "当前文档信息",
        Description = "返回 Revit 中当前打开文档的标题、路径、视图等基本信息，并告知写入模式是否已开启。" +
                      "开始任何操作前先调用它，可以确认连上的是不是预期的模型。",
        ReadOnly = true,
        TimeoutSeconds = 15)]
    public sealed class DocumentInfoTool : RevitTool<DocumentInfoInput, DocumentInfoOutput>
    {
        public override DocumentInfoOutput Execute(DocumentInfoInput input, ToolExecutionContext<UIApplication> context)
        {
            var uiDocument = RequireUiDocument(context);
            var document = uiDocument.Document;

            string activeView = null;
            try { activeView = uiDocument.ActiveView?.Name; }
            catch { /* 某些文档状态下取活动视图会抛异常，不值得让整个工具失败 */ }

            return new DocumentInfoOutput
            {
                Title = document.Title,
                PathName = string.IsNullOrEmpty(document.PathName) ? null : document.PathName,
                IsFamilyDocument = document.IsFamilyDocument,
                IsWorkshared = document.IsWorkshared,
                ActiveView = activeView,
                RevitVersion = context.Host.Application.VersionNumber,
                WriteEnabled = context.WriteEnabled
            };
        }
    }

    // ==================== 类别清单 ====================

    public sealed class ListCategoriesInput
    {
        [McpParam("只返回名称或 BuiltInCategory 中包含该文本的类别（不区分大小写）")]
        public string Filter { get; set; }

        [McpParam("true 时只返回当前模型中确实存在构件的类别，默认 true")]
        public bool? OnlyNonEmpty { get; set; }
    }

    public sealed class CategoryInfo
    {
        [McpParam("BuiltInCategory 名，如 OST_Walls。作为 revit_query_elements 的 category 参数")]
        public string BuiltInCategory { get; set; }

        [McpParam("Revit 界面中显示的类别名（受语言设置影响）")]
        public string DisplayName { get; set; }

        [McpParam("该类别下的构件实例数量")]
        public int ElementCount { get; set; }
    }

    public sealed class ListCategoriesOutput
    {
        [McpParam("匹配的类别数")]
        public int Total { get; set; }

        [McpParam("类别列表，按构件数量从多到少排列")]
        public List<CategoryInfo> Categories { get; set; } = new List<CategoryInfo>();
    }

    [McpTool("revit_list_categories",
        Title = "列出模型中的类别",
        Description = "列出当前模型里实际存在构件的类别及其数量。" +
                      "在调用 revit_query_elements 之前用它确认类别名，比凭记忆猜 BuiltInCategory 可靠。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListCategoriesTool : RevitTool<ListCategoriesInput, ListCategoriesOutput>
    {
        public override ListCategoriesOutput Execute(ListCategoriesInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);
            var onlyNonEmpty = input.OnlyNonEmpty ?? true;

            // 一次遍历统计各类别数量，比逐类别建 collector 快一个数量级
            var counts = new Dictionary<long, int>();
            foreach (var element in new FilteredElementCollector(document).WhereElementIsNotElementType())
            {
                var category = element.Category;
                if (category == null) continue;

                var key = category.Id.GetValue();
                counts.TryGetValue(key, out var current);
                counts[key] = current + 1;
            }

            var results = new List<CategoryInfo>();
            foreach (BuiltInCategory builtIn in Enum.GetValues(typeof(BuiltInCategory)))
            {
                Category category;
                try { category = Category.GetCategory(document, builtIn); }
                catch { continue; }   // 部分枚举值在当前文档类型下无效
                if (category == null) continue;

                counts.TryGetValue(category.Id.GetValue(), out var count);
                if (onlyNonEmpty && count == 0) continue;

                var name = builtIn.ToString();
                if (!Matches(input.Filter, name, category.Name)) continue;

                results.Add(new CategoryInfo
                {
                    BuiltInCategory = name,
                    DisplayName = category.Name,
                    ElementCount = count
                });
            }

            results = results.OrderByDescending(c => c.ElementCount)
                             .ThenBy(c => c.BuiltInCategory, StringComparer.Ordinal)
                             .ToList();

            return new ListCategoriesOutput { Total = results.Count, Categories = results };
        }

        private static bool Matches(string filter, string builtIn, string displayName)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            return builtIn.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                   || (displayName != null && displayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    // ==================== 构件查询 ====================

    public sealed class QueryElementsInput
    {
        [McpParam("BuiltInCategory 名，如 OST_Walls、OST_Doors。可省略 OST_ 前缀。" +
                  "不确定时先调用 revit_list_categories", Required = true)]
        public string Category { get; set; }

        [McpParam("true 时只查当前活动视图中可见的构件，默认 false（查整个模型）")]
        public bool? ActiveViewOnly { get; set; }

        [McpParam("按构件名过滤（不区分大小写的子串匹配）")]
        public string NameContains { get; set; }

        [McpParam("最多返回多少条，默认 100，上限 1000。超出部分通过 truncated 字段告知")]
        public int? Limit { get; set; }
    }

    public sealed class QueryElementsOutput
    {
        [McpParam("匹配到的构件总数（不受 limit 影响）")]
        public int Total { get; set; }

        [McpParam("本次实际返回的条数")]
        public int Returned { get; set; }

        [McpParam("是否因 limit 而被截断")]
        public bool Truncated { get; set; }

        [McpParam("构件列表")]
        public List<ElementSummary> Elements { get; set; } = new List<ElementSummary>();
    }

    [McpTool("revit_query_elements",
        Title = "按类别查询构件",
        Description = "在当前模型中按类别查询构件实例，返回 ID、名称、类型与标高。" +
                      "拿到 ID 后可用 revit_get_element_parameters 读取详细参数。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class QueryElementsTool : RevitTool<QueryElementsInput, QueryElementsOutput>
    {
        private const int DefaultLimit = 100;
        private const int MaxLimit = 1000;

        public override QueryElementsOutput Execute(QueryElementsInput input, ToolExecutionContext<UIApplication> context)
        {
            var uiDocument = RequireUiDocument(context);
            var document = uiDocument.Document;
            var category = ParseCategory(input.Category);

            var limit = Math.Min(Math.Max(input.Limit ?? DefaultLimit, 1), MaxLimit);

            FilteredElementCollector collector;
            if (input.ActiveViewOnly == true)
            {
                var view = uiDocument.ActiveView;
                if (view == null)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "当前没有活动视图，无法使用 activeViewOnly。");
                collector = new FilteredElementCollector(document, view.Id);
            }
            else
            {
                collector = new FilteredElementCollector(document);
            }

            var matched = collector
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .Where(e => MatchesName(e, input.NameContains))
                .ToList();

            var output = new QueryElementsOutput
            {
                Total = matched.Count,
                Truncated = matched.Count > limit
            };

            foreach (var element in matched.Take(limit))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                output.Elements.Add(Summarize(element));
            }

            output.Returned = output.Elements.Count;
            return output;
        }

        private static bool MatchesName(Element element, string needle)
        {
            if (string.IsNullOrWhiteSpace(needle)) return true;
            try
            {
                return element.Name != null &&
                       element.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;   // 取不到名字的元素视为不匹配
            }
        }
    }

    // ==================== 参数读取 ====================

    public sealed class GetParametersInput
    {
        [McpParam("要读取的构件 ID 列表（字符串形式，来自 revit_query_elements）", Required = true)]
        public List<string> ElementIds { get; set; }

        [McpParam("只返回名称包含该文本的参数（不区分大小写）。模型参数很多，建议过滤")]
        public string NameContains { get; set; }

        [McpParam("true 时同时返回该构件所属类型（族类型）上的参数，默认 false")]
        public bool? IncludeTypeParameters { get; set; }
    }

    public sealed class ElementParameters
    {
        [McpParam("构件 ID")]
        public string Id { get; set; }

        [McpParam("构件名称")]
        public string Name { get; set; }

        [McpParam("所属类别")]
        public string Category { get; set; }

        [McpParam("实例参数")]
        public List<ParameterValue> Parameters { get; set; } = new List<ParameterValue>();

        [McpParam("类型参数，未请求时为 null")]
        public List<ParameterValue> TypeParameters { get; set; }
    }

    public sealed class GetParametersOutput
    {
        [McpParam("成功读取的构件")]
        public List<ElementParameters> Elements { get; set; } = new List<ElementParameters>();

        [McpParam("未找到或 ID 非法的构件，及原因")]
        public List<string> NotFound { get; set; } = new List<string>();
    }

    [McpTool("revit_get_element_parameters",
        Title = "读取构件参数",
        Description = "读取一批构件的参数值。同时给出原始值（Double 为 Revit 内部单位，长度为英尺）" +
                      "和带单位的显示值。参数数量通常很多，建议用 nameContains 过滤。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class GetParametersTool : RevitTool<GetParametersInput, GetParametersOutput>
    {
        private const int MaxElements = 200;

        public override GetParametersOutput Execute(GetParametersInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            if (input.ElementIds == null || input.ElementIds.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter, "elementIds 不能为空。");

            if (input.ElementIds.Count > MaxElements)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "一次最多读取 " + MaxElements + " 个构件，本次传入 " + input.ElementIds.Count + " 个。请分批调用。");

            var output = new GetParametersOutput();
            var total = input.ElementIds.Count;
            var done = 0;

            foreach (var raw in input.ElementIds)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                done++;
                ProgressTicker.Tick(context.Progress, done, total, "已读取");

                if (!ElementIdCompat.TryParse(raw, out var elementId))
                {
                    output.NotFound.Add(raw + "（ID 格式非法）");
                    continue;
                }

                var element = document.GetElement(elementId);
                if (element == null)
                {
                    output.NotFound.Add(raw + "（模型中不存在）");
                    continue;
                }

                var entry = new ElementParameters
                {
                    Id = elementId.ToProtocolString(),
                    Name = TryName(element),
                    Category = element.Category?.Name,
                    Parameters = ReadParameters(element, input.NameContains)
                };

                if (input.IncludeTypeParameters == true)
                {
                    var typeId = element.GetTypeId();
                    if (typeId != null && typeId != ElementId.InvalidElementId)
                    {
                        var type = document.GetElement(typeId);
                        if (type != null) entry.TypeParameters = ReadParameters(type, input.NameContains);
                    }
                }

                output.Elements.Add(entry);
            }

            return output;
        }

        private static List<ParameterValue> ReadParameters(Element element, string needle)
        {
            var values = new List<ParameterValue>();

            foreach (Parameter parameter in element.Parameters)
            {
                var name = parameter.Definition?.Name;
                if (name == null) continue;
                if (!string.IsNullOrWhiteSpace(needle) &&
                    name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;

                try { values.Add(ReadParameter(parameter)); }
                catch { /* 个别参数读取失败不应让整个构件失败 */ }
            }

            return values.OrderBy(v => v.Name, StringComparer.CurrentCulture).ToList();
        }

        private static string TryName(Element element)
        {
            try { return element.Name; }
            catch { return null; }
        }
    }
}
