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
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档。" +
                  "一个 Revit 可以同时开着多个项目，批量检查靠它逐个指定")]
        public string DocumentId { get; set; }

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
            var document = ResolveDocument(context, input.DocumentId);
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

    public sealed class BoxFilter
    {
        [McpParam("最小角点，毫米", Required = true)]
        public Point3D Min { get; set; }

        [McpParam("最大角点，毫米", Required = true)]
        public Point3D Max { get; set; }
    }

    public sealed class NearFilter
    {
        [McpParam("中心点，毫米", Required = true)]
        public Point3D Point { get; set; }

        [McpParam("半径，毫米。按构件包围盒到该点的最近距离算", Required = true)]
        public double RadiusMm { get; set; }
    }

    public sealed class QueryElementsInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档。" +
                  "一个 Revit 可以同时开着多个项目，批量检查靠它逐个指定")]
        public string DocumentId { get; set; }

        [McpParam("BuiltInCategory 名，如 OST_Walls、OST_Doors。可省略 OST_ 前缀。" +
                  "不确定时先调用 revit_list_categories。" +
                  "给了空间过滤条件时可以省略它，此时会跨类别查")]
        public string Category { get; set; }

        [McpParam("true 时只查当前活动视图中可见的构件，默认 false（查整个模型）")]
        public bool? ActiveViewOnly { get; set; }

        [McpParam("按构件名过滤（不区分大小写的子串匹配）")]
        public string NameContains { get; set; }

        [McpParam("只返回包围盒与该长方体相交的构件。与 near 只能给一个")]
        public BoxFilter WithinBox { get; set; }

        [McpParam("只返回该点附近的构件，结果按距离从近到远排列。与 withinBox 只能给一个")]
        public NearFilter Near { get; set; }

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
            var document = ResolveDocument(context, input.DocumentId);
            var crossDocument = !string.IsNullOrWhiteSpace(input.DocumentId);

            if (input.WithinBox != null && input.Near != null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "withinBox 与 near 只能给一个。要找某个范围内的构件用 withinBox，" +
                    "要找某个位置附近的用 near。");

            var hasSpatial = input.WithinBox != null || input.Near != null;

            // 既不限类别又不限范围，等于把整个模型倒出来。有 limit 兜着也依然是浪费——
            // 模型拿到一份被截断的全模型清单，什么问题都回答不了
            if (string.IsNullOrWhiteSpace(input.Category) && !hasSpatial)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "至少要给一个筛选条件：category（按类别查），或 withinBox / near（按位置查）。");

            var limit = Math.Min(Math.Max(input.Limit ?? DefaultLimit, 1), MaxLimit);

            FilteredElementCollector collector;
            if (input.ActiveViewOnly == true)
            {
                // "活动视图"是相对整个 Revit 而言的，只存在于活动文档里。
                // 对别的文档谈这个概念没有意义，硬要支持只会给出一个看似合理的错答案
                if (crossDocument)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "activeViewOnly 只能用于当前活动文档。查别的文档时请去掉它，或者改用 withinBox / near 限定范围。");

                var view = RequireUiDocument(context).ActiveView;
                if (view == null)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "当前没有活动视图，无法使用 activeViewOnly。");
                collector = new FilteredElementCollector(document, view.Id);
            }
            else
            {
                collector = new FilteredElementCollector(document);
            }

            if (!string.IsNullOrWhiteSpace(input.Category))
                collector = collector.OfCategory(ParseCategory(input.Category));

            collector = collector.WhereElementIsNotElementType();

            // 先让 Revit 用包围盒过滤器粗筛——它走的是空间索引，
            // 比把整个模型拉进托管代码再逐个算距离快得多
            var outline = BuildOutline(input);
            if (outline != null) collector = collector.WherePasses(new BoundingBoxIntersectsFilter(outline));

            var named = collector.Where(e => MatchesName(e, input.NameContains));

            var matched = input.Near != null
                ? RefineByDistance(named, input.Near)
                : named.Select(e => new Match { Element = e }).ToList();

            var output = new QueryElementsOutput
            {
                Total = matched.Count,
                Truncated = matched.Count > limit
            };

            foreach (var match in matched.Take(limit))
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var summary = Summarize(match.Element);
                summary.DistanceMm = match.DistanceMm;
                output.Elements.Add(summary);
            }

            output.Returned = output.Elements.Count;
            return output;
        }

        private sealed class Match
        {
            public Element Element;
            public double? DistanceMm;
        }

        private static Outline BuildOutline(QueryElementsInput input)
        {
            if (input.WithinBox != null)
            {
                if (input.WithinBox.Min == null || input.WithinBox.Max == null)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "withinBox 需要 min 和 max 两个角点。");

                var min = input.WithinBox.Min.ToXyz();
                var max = input.WithinBox.Max.ToXyz();

                // Outline 要求 min 确实在 max 的各分量之下，否则它会拒绝或静默给出空结果
                return new Outline(
                    new XYZ(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z)),
                    new XYZ(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z)));
            }

            if (input.Near == null) return null;

            if (input.Near.Point == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter, "near 需要 point。");

            if (input.Near.RadiusMm <= 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "near.radiusMm 必须为正，收到 " + input.Near.RadiusMm + "。");

            var center = input.Near.Point.ToXyz();
            var radius = Units.ToFeet(input.Near.RadiusMm);
            var offset = new XYZ(radius, radius, radius);

            // 立方体只是粗筛，真正的球形距离在 RefineByDistance 里算
            return new Outline(center - offset, center + offset);
        }

        /// <summary>
        /// 按"包围盒到查询点的最近距离"精筛并排序。
        ///
        /// 不用包围盒中心算距离：一面 10 米长的墙，端点就在你脚边、中心却在 5 米开外，
        /// 按中心算会把它判成"不在附近"。
        /// </summary>
        private static List<Match> RefineByDistance(IEnumerable<Element> elements, NearFilter near)
        {
            var center = near.Point.ToXyz();
            var radius = Units.ToFeet(near.RadiusMm);
            var matches = new List<Match>();

            foreach (var element in elements)
            {
                var box = GetGeometryTool.RawBox(element);
                if (box == null) continue;   // 没有几何的构件谈不上远近

                XYZ min, max;
                try { GetGeometryTool.Extremes(box, out min, out max); }
                catch { continue; }

                var distance = DistanceToBox(center, min, max);
                if (distance > radius) continue;

                matches.Add(new Match
                {
                    Element = element,
                    DistanceMm = Units.Round(Units.FromFeet(distance))
                });
            }

            return matches.OrderBy(m => m.DistanceMm ?? double.MaxValue).ToList();
        }

        /// <summary>点到轴对齐包围盒的最近距离。点在盒内时为 0。</summary>
        private static double DistanceToBox(XYZ point, XYZ min, XYZ max)
        {
            var dx = Math.Max(0, Math.Max(min.X - point.X, point.X - max.X));
            var dy = Math.Max(0, Math.Max(min.Y - point.Y, point.Y - max.Y));
            var dz = Math.Max(0, Math.Max(min.Z - point.Z, point.Z - max.Z));

            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
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
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档。" +
                  "一个 Revit 可以同时开着多个项目，批量检查靠它逐个指定")]
        public string DocumentId { get; set; }

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
            var document = ResolveDocument(context, input.DocumentId);

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
