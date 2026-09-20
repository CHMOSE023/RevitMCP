using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 视图族类型清单 ====================

    public sealed class ListViewFamilyTypesInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }

        [McpParam("只看这一种视图类型的族类型：floorPlan、ceilingPlan、section、elevation、threeD、" +
                  "areaPlan、drafting、schedule、sheet、walkthrough、legend。省略则全给",
                  AllowedValues = new[] { "floorPlan", "ceilingPlan", "section", "elevation", "threeD",
                                          "areaPlan", "drafting", "schedule", "sheet", "walkthrough", "legend" })]
        public string ViewType { get; set; }

        [McpParam("按类型名过滤（不区分大小写的子串匹配）")]
        public string NameContains { get; set; }
    }

    public sealed class ViewFamilyTypeInfo
    {
        [McpParam("视图族类型 ID。revit_create_views 的 viewFamilyTypeId 用它")]
        public string Id { get; set; }

        [McpParam("类型名，如「建筑平面」「结构平面」")]
        public string Name { get; set; }

        [McpParam("它能建出哪一类视图（Revit 的 ViewFamily 名）")]
        public string ViewFamily { get; set; }

        [McpParam("revit_create_views 的 viewType 该填什么。为 null 表示本服务还不支持建这类视图")]
        public string ViewType { get; set; }

        [McpParam("省略 viewFamilyTypeId 时会不会选中它——同一 ViewFamily 下的第一个")]
        public bool IsDefaultPick { get; set; }

        [McpParam("当前有多少个视图用着这个族类型")]
        public int UsedCount { get; set; }
    }

    public sealed class ListViewFamilyTypesOutput
    {
        [McpParam("返回的视图族类型数")]
        public int Total { get; set; }

        [McpParam("视图族类型列表")]
        public List<ViewFamilyTypeInfo> Types { get; set; } = new List<ViewFamilyTypeInfo>();
    }

    /// <summary>
    /// 视图族类型清单。
    ///
    /// 补这个工具，是因为它本来就被别的工具的报错指着：创建视图失败时提示
    /// "请显式指定 viewFamilyTypeId"，而此前没有任何工具能列出这些 ID
    /// （<c>revit_list_types</c> 查 OST_Views 返回 0 条——视图族类型不是构件类别）。
    /// 一句指向死路的提示，比没有提示更费时间。
    /// </summary>
    [McpTool("revit_list_view_family_types",
        Title = "列出视图族类型",
        Description = "列出项目里的视图族类型及其 ID——revit_create_views 的 viewFamilyTypeId 从这里来。" +
                      "项目里常有多套同类视图族类型（「建筑平面」「结构平面」「详图剖面」等），" +
                      "省略 viewFamilyTypeId 时工具只挑第一个，挑得对不对看 isDefaultPick。" +
                      "**视图族类型不是构件类别**，用 revit_list_types 查 OST_Views 是查不到的。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListViewFamilyTypesTool : RevitTool<ListViewFamilyTypesInput, ListViewFamilyTypesOutput>
    {
        public override ListViewFamilyTypesOutput Execute(
            ListViewFamilyTypesInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);
            var wanted = ParseViewFamily(input.ViewType);

            var usedCounts = CountUsage(document);

            var all = new FilteredElementCollector(document)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .ToList();

            // "省略时会选谁"必须按与 revit_create_views 完全一致的顺序算出来，
            // 否则这份清单会告诉调用方一个它实际不会选中的默认值
            var defaultPicks = new HashSet<long>();
            foreach (var family in all.Select(type => type.ViewFamily).Distinct())
            {
                var first = all.FirstOrDefault(type => type.ViewFamily == family);
                if (first != null) defaultPicks.Add(first.Id.GetValue());
            }

            var results = new List<ViewFamilyTypeInfo>();
            foreach (var type in all)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                if (wanted.HasValue && type.ViewFamily != wanted.Value) continue;

                var name = SafeName(type);
                if (!string.IsNullOrWhiteSpace(input.NameContains) &&
                    (name == null ||
                     name.IndexOf(input.NameContains, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;

                int used;
                usedCounts.TryGetValue(type.Id.GetValue(), out used);

                results.Add(new ViewFamilyTypeInfo
                {
                    Id = type.Id.ToProtocolString(),
                    Name = name,
                    ViewFamily = type.ViewFamily.ToString(),
                    ViewType = ToolViewType(type.ViewFamily),
                    IsDefaultPick = defaultPicks.Contains(type.Id.GetValue()),
                    UsedCount = used
                });
            }

            results = results
                .OrderBy(type => type.ViewFamily, StringComparer.Ordinal)
                .ThenBy(type => type.Name, StringComparer.CurrentCulture)
                .ToList();

            return new ListViewFamilyTypesOutput { Total = results.Count, Types = results };
        }

        private static Dictionary<long, int> CountUsage(Document document)
        {
            var counts = new Dictionary<long, int>();

            foreach (var view in new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>())
            {
                ElementId typeId;
                try { typeId = view.GetTypeId(); }
                catch { continue; }

                if (typeId == null || typeId == ElementId.InvalidElementId) continue;

                var key = typeId.GetValue();
                int count;
                counts.TryGetValue(key, out count);
                counts[key] = count + 1;
            }

            return counts;
        }

        /// <summary>对外的 viewType 名 → Revit 的 ViewFamily。</summary>
        private static ViewFamily? ParseViewFamily(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            switch (value.Trim().ToLowerInvariant())
            {
                case "floorplan": return Autodesk.Revit.DB.ViewFamily.FloorPlan;
                case "ceilingplan": return Autodesk.Revit.DB.ViewFamily.CeilingPlan;
                case "section": return Autodesk.Revit.DB.ViewFamily.Section;
                case "elevation": return Autodesk.Revit.DB.ViewFamily.Elevation;
                case "threed":
                case "3d": return Autodesk.Revit.DB.ViewFamily.ThreeDimensional;
                case "areaplan": return Autodesk.Revit.DB.ViewFamily.AreaPlan;
                case "drafting": return Autodesk.Revit.DB.ViewFamily.Drafting;
                case "schedule": return Autodesk.Revit.DB.ViewFamily.Schedule;
                case "sheet": return Autodesk.Revit.DB.ViewFamily.Sheet;
                case "walkthrough": return Autodesk.Revit.DB.ViewFamily.Walkthrough;
                case "legend": return Autodesk.Revit.DB.ViewFamily.Legend;

                default:
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "无法识别的 viewType \"" + value +
                        "\"。可用值：floorPlan、ceilingPlan、section、elevation、threeD、" +
                        "areaPlan、drafting、schedule、sheet、walkthrough、legend。");
            }
        }

        /// <summary>Revit 的 ViewFamily → revit_create_views 收的 viewType。建不了的给 null。</summary>
        private static string ToolViewType(ViewFamily family)
        {
            switch (family)
            {
                case Autodesk.Revit.DB.ViewFamily.FloorPlan: return "floorPlan";
                case Autodesk.Revit.DB.ViewFamily.CeilingPlan: return "ceilingPlan";
                case Autodesk.Revit.DB.ViewFamily.Section: return "section";
                case Autodesk.Revit.DB.ViewFamily.Elevation: return "elevation";
                case Autodesk.Revit.DB.ViewFamily.ThreeDimensional: return "threeD";
                default: return null;
            }
        }

        private static string SafeName(Element element)
        {
            try { return element == null ? null : element.Name; }
            catch { return null; }
        }
    }
}
