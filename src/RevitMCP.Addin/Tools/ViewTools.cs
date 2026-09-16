using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 视图清单 ====================

    public sealed class ListViewsInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档。" +
                  "一个 Revit 可以同时开着多个项目，批量检查靠它逐个指定")]
        public string DocumentId { get; set; }

        [McpParam("按视图类型过滤，如 FloorPlan、ThreeD、Section、Elevation、DrawingSheet、Schedule")]
        public string ViewType { get; set; }

        [McpParam("按视图名过滤（不区分大小写的子串匹配）")]
        public string NameContains { get; set; }

        [McpParam("true 时只返回能放到图纸上的视图（排除视图样板与图纸本身），默认 false")]
        public bool? OnlyPlaceable { get; set; }

        [McpParam("true 时把视图样板也列出来，默认 false")]
        public bool? IncludeTemplates { get; set; }

        [McpParam("最多返回多少条，默认 200，上限 1000")]
        public int? Limit { get; set; }
    }

    public sealed class ViewInfo
    {
        [McpParam("视图 ID")]
        public string Id { get; set; }

        [McpParam("视图名")]
        public string Name { get; set; }

        [McpParam("视图类型，如 FloorPlan、ThreeD、DrawingSheet")]
        public string ViewType { get; set; }

        [McpParam("是否为视图样板。样板不能激活，也不能放到图纸上")]
        public bool IsTemplate { get; set; }

        [McpParam("是否可打印/可导出为图片")]
        public bool CanBePrinted { get; set; }

        [McpParam("比例分母，如 100 表示 1:100。无比例概念的视图为 null")]
        public int? Scale { get; set; }

        [McpParam("关联标高 ID，平面视图才有")]
        public string LevelId { get; set; }

        [McpParam("关联标高名")]
        public string Level { get; set; }

        [McpParam("已放置在哪张图纸上的图纸 ID；未放置为 null。图纸本身没有这一项")]
        public string SheetId { get; set; }

        [McpParam("图纸编号。视图放在图纸上时是**所在图纸**的编号；" +
                  "这一行本身就是图纸时，是**它自己的**编号")]
        public string SheetNumber { get; set; }

        [McpParam("是否为当前活动视图")]
        public bool IsActive { get; set; }
    }

    public sealed class ListViewsOutput
    {
        [McpParam("匹配到的视图总数（不受 limit 影响）")]
        public int Total { get; set; }

        [McpParam("本次实际返回的条数")]
        public int Returned { get; set; }

        [McpParam("是否因 limit 而被截断")]
        public bool Truncated { get; set; }

        [McpParam("视图列表")]
        public List<ViewInfo> Views { get; set; } = new List<ViewInfo>();
    }

    [McpTool("revit_list_views",
        Title = "列出视图与图纸",
        Description = "列出模型中的视图、图纸及其类型、比例、图纸编号。" +
                      "导出图片前用它挑视图；往图纸上摆视图前用它确认哪些还没被放置" +
                      "（sheetId 为 null 的才可以放——一个视图只能放在一张图纸上）。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListViewsTool : RevitTool<ListViewsInput, ListViewsOutput>
    {
        private const int DefaultLimit = 200;
        private const int MaxLimit = 1000;

        public override ListViewsOutput Execute(ListViewsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);
            var limit = Math.Min(Math.Max(input.Limit ?? DefaultLimit, 1), MaxLimit);

            ViewType? typeFilter = null;
            if (!string.IsNullOrWhiteSpace(input.ViewType)) typeFilter = ParseViewType(input.ViewType);

            var placement = MapViewsToSheets(document);

            // 活动视图属于活动文档，查别的文档时这一列一律是 false
            ElementId activeId = null;
            if (string.IsNullOrWhiteSpace(input.DocumentId))
                activeId = SafeActiveViewId(RequireUiDocument(context));

            var views = new List<ViewInfo>();

            foreach (var element in new FilteredElementCollector(document).OfClass(typeof(View)))
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var view = element as View;
                if (view == null) continue;

                var isTemplate = SafeIsTemplate(view);
                if (isTemplate && input.IncludeTemplates != true) continue;

                var viewType = SafeViewType(view);
                if (typeFilter.HasValue && viewType != typeFilter.Value) continue;

                var name = SafeName(view);
                if (!Matches(input.NameContains, name)) continue;

                // 能放到图纸上的：不是样板，本身也不是图纸
                if (input.OnlyPlaceable == true && (isTemplate || viewType == Autodesk.Revit.DB.ViewType.DrawingSheet))
                    continue;

                ElementId sheetId;
                placement.TryGetValue(view.Id.GetValue(), out sheetId);

                // 图纸自己的编号也要给出来。
                // 图纸是它自己的身份标识（"把视图放到 A-101 上"靠的就是它），
                // 而这一列原先只在"视图放在某张图纸上"时才有值——
                // 于是一张图纸的编号反倒读不到，检查图纸编号规范只能绕道读参数
                var isSheet = viewType == Autodesk.Revit.DB.ViewType.DrawingSheet;

                views.Add(new ViewInfo
                {
                    Id = view.Id.ToProtocolString(),
                    Name = name,
                    ViewType = viewType.ToString(),
                    IsTemplate = isTemplate,
                    CanBePrinted = SafeCanPrint(view),
                    Scale = SafeScale(view),
                    LevelId = SafeLevelId(view)?.ToProtocolString(),
                    Level = SafeLevelName(view),
                    SheetId = sheetId?.ToProtocolString(),
                    SheetNumber = isSheet
                        ? SafeSheetNumber(view as ViewSheet)
                        : (sheetId == null ? null : SheetNumberOf(document, sheetId)),
                    IsActive = activeId != null && view.Id == activeId
                });
            }

            views = views.OrderBy(v => v.ViewType, StringComparer.Ordinal)
                         .ThenBy(v => v.Name, StringComparer.CurrentCulture)
                         .ToList();

            var output = new ListViewsOutput
            {
                Total = views.Count,
                Truncated = views.Count > limit,
                Views = views.Take(limit).ToList()
            };

            output.Returned = output.Views.Count;
            return output;
        }

        /// <summary>
        /// 一次遍历所有视口，建起"视图 → 图纸"的映射。
        /// 逐个视图去问"你在哪张图纸上"要么没有直接 API，要么是 O(n²)。
        /// </summary>
        internal static Dictionary<long, ElementId> MapViewsToSheets(Document document)
        {
            var map = new Dictionary<long, ElementId>();

            foreach (var element in new FilteredElementCollector(document)
                         .OfClass(typeof(Viewport))
                         .WhereElementIsNotElementType())
            {
                var viewport = element as Viewport;
                if (viewport == null) continue;

                try { map[viewport.ViewId.GetValue()] = viewport.SheetId; }
                catch { /* 个别损坏的视口读不出来，不影响其余 */ }
            }

            return map;
        }

        internal static ViewType ParseViewType(string value)
        {
            ViewType parsed;
            if (Enum.TryParse(value.Trim(), ignoreCase: true, out parsed) &&
                Enum.IsDefined(typeof(ViewType), parsed))
                return parsed;

            var suggestions = Enum.GetNames(typeof(ViewType))
                .Where(n => n.IndexOf(value.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(8)
                .ToArray();

            var hint = suggestions.Length > 0
                ? "。是否想找：" + string.Join("、", suggestions)
                : "。常用值：FloorPlan、CeilingPlan、ThreeD、Section、Elevation、DrawingSheet、Schedule";

            throw new ToolFailureException(McpDomainError.InvalidParameter,
                "无法识别的视图类型 \"" + value + "\"" + hint);
        }

        private static string SafeSheetNumber(ViewSheet sheet)
        {
            try { return sheet?.SheetNumber; }
            catch { return null; }
        }

        internal static string SheetNumberOf(Document document, ElementId sheetId)
        {
            try { return (document.GetElement(sheetId) as ViewSheet)?.SheetNumber; }
            catch { return null; }
        }

        internal static ElementId SafeActiveViewId(UIDocument uiDocument)
        {
            try { return uiDocument.ActiveView?.Id; }
            catch { return null; }
        }

        private static bool SafeIsTemplate(View view)
        {
            try { return view.IsTemplate; }
            catch { return false; }
        }

        private static ViewType SafeViewType(View view)
        {
            try { return view.ViewType; }
            catch { return Autodesk.Revit.DB.ViewType.Undefined; }
        }

        private static bool SafeCanPrint(View view)
        {
            try { return view.CanBePrinted; }
            catch { return false; }
        }

        private static int? SafeScale(View view)
        {
            try { return view.Scale; }
            catch { return null; }
        }

        private static ElementId SafeLevelId(View view)
        {
            try
            {
                var level = view.GenLevel;
                return level?.Id;
            }
            catch { return null; }
        }

        private static string SafeLevelName(View view)
        {
            try { return view.GenLevel?.Name; }
            catch { return null; }
        }

        private static string SafeName(View view)
        {
            try { return view.Name; }
            catch { return null; }
        }

        private static bool Matches(string needle, string name)
        {
            if (string.IsNullOrWhiteSpace(needle)) return true;
            return name != null && name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    // ==================== 切换活动视图 ====================

    public sealed class ActivateViewInput
    {
        [McpParam("要切换到的视图或图纸 ID，来自 revit_list_views", Required = true)]
        public string ViewId { get; set; }
    }

    public sealed class ActivateViewOutput
    {
        [McpParam("切换后的活动视图 ID")]
        public string Id { get; set; }

        [McpParam("切换后的活动视图名")]
        public string Name { get; set; }

        [McpParam("视图类型")]
        public string ViewType { get; set; }

        [McpParam("切换前的活动视图名")]
        public string PreviousName { get; set; }
    }

    [McpTool("revit_activate_view",
        Title = "切换活动视图",
        Description = "把 Revit 的活动视图切到指定视图或图纸，用户屏幕会跟着变。" +
                      "**它会改变其他工具的答案**：activeViewOnly 查询、导出当前视图都以活动视图为准，" +
                      "所以别在用户没要求时随手切换。" +
                      "只是想导出某个视图的图片，不必切——revit_export_image 可以直接指定 viewId。",
        WithoutTransaction = true,
        TimeoutSeconds = 30)]
    public sealed class ActivateViewTool : RevitTool<ActivateViewInput, ActivateViewOutput>
    {
        public override ActivateViewOutput Execute(
            ActivateViewInput input, ToolExecutionContext<UIApplication> context)
        {
            var uiDocument = RequireUiDocument(context);
            var document = uiDocument.Document;

            var element = RequireElement(document, input.ViewId);
            var view = element as View;

            if (view == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "ID " + input.ViewId + " 不是视图，而是「" +
                    (element.Category?.Name ?? element.GetType().Name) + "」。用 revit_list_views 取视图 ID。");

            if (view.IsTemplate)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "「" + view.Name + "」是视图样板，不能作为活动视图。样板只是一组设置，不是能打开的视图。");

            var previous = SafeActiveName(uiDocument);

            try
            {
                uiDocument.ActiveView = view;
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "Revit 拒绝切换到「" + view.Name + "」：" + ex.Message +
                    "。有些视图（如未放置的明细表、系统浏览器视图）不能作为活动视图。");
            }

            return new ActivateViewOutput
            {
                Id = view.Id.ToProtocolString(),
                Name = view.Name,
                ViewType = view.ViewType.ToString(),
                PreviousName = previous
            };
        }

        private static string SafeActiveName(UIDocument uiDocument)
        {
            try { return uiDocument.ActiveView?.Name; }
            catch { return null; }
        }
    }
}
