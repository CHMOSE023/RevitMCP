using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 类型清单 ====================

    public sealed class ListTypesInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档。" +
                  "一个 Revit 可以同时开着多个项目，批量检查靠它逐个指定")]
        public string DocumentId { get; set; }

        [McpParam("BuiltInCategory 名，如 OST_Walls、OST_Doors。可省略 OST_ 前缀", Required = true)]
        public string Category { get; set; }

        [McpParam("按族名或类型名过滤（不区分大小写的子串匹配）")]
        public string NameContains { get; set; }

        [McpParam("true 时只返回模型中已有实例的类型，默认 false")]
        public bool? OnlyInUse { get; set; }

        [McpParam("最多返回多少条，默认 100，上限 500")]
        public int? Limit { get; set; }
    }

    public sealed class TypeInfo
    {
        [McpParam("类型 ID。建模工具的 typeId 参数用的就是它")]
        public string Id { get; set; }

        [McpParam("类型名，如「常规 - 200mm」")]
        public string Name { get; set; }

        [McpParam("族名，如「基本墙」")]
        public string Family { get; set; }

        [McpParam("整体厚度，毫米。仅墙、楼板、天花、屋顶这类有层结构的类型才有，其余为 null")]
        public double? ThicknessMm { get; set; }

        [McpParam("模型中使用该类型的构件数")]
        public int InstanceCount { get; set; }

        [McpParam("是否为该类别的默认类型（建模时省略 typeId 就用它）")]
        public bool IsDefault { get; set; }
    }

    public sealed class ListTypesOutput
    {
        [McpParam("匹配到的类型总数（不受 limit 影响）")]
        public int Total { get; set; }

        [McpParam("本次实际返回的条数")]
        public int Returned { get; set; }

        [McpParam("是否因 limit 而被截断")]
        public bool Truncated { get; set; }

        [McpParam("类型列表，已使用的排在前面")]
        public List<TypeInfo> Types { get; set; } = new List<TypeInfo>();
    }

    [McpTool("revit_list_types",
        Title = "列出类别下的族类型",
        Description = "列出某个类别下可用的族类型及其 ID、厚度、使用数量。" +
                      "建模前先用它挑类型——「外墙 200 厚、内隔墙 100 厚」这类要求" +
                      "只能通过 typeId 表达，凭类型名猜是猜不中的。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListTypesTool : RevitTool<ListTypesInput, ListTypesOutput>
    {
        private const int DefaultLimit = 100;
        private const int MaxLimit = 500;

        public override ListTypesOutput Execute(ListTypesInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);
            var category = ParseCategory(input.Category);
            var limit = Math.Min(Math.Max(input.Limit ?? DefaultLimit, 1), MaxLimit);

            var counts = CountInstances(document, category);
            var defaultTypeId = DefaultTypeId(document, category);

            var results = new List<TypeInfo>();

            foreach (var element in new FilteredElementCollector(document)
                         .OfCategory(category)
                         .WhereElementIsElementType())
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var type = element as ElementType;
                if (type == null) continue;

                var name = SafeName(type);
                var family = SafeFamilyName(type);
                if (!Matches(input.NameContains, name, family)) continue;

                int count;
                counts.TryGetValue(type.Id.GetValue(), out count);
                if (input.OnlyInUse == true && count == 0) continue;

                results.Add(new TypeInfo
                {
                    Id = type.Id.ToProtocolString(),
                    Name = name,
                    Family = family,
                    ThicknessMm = ThicknessOf(type),
                    InstanceCount = count,
                    IsDefault = defaultTypeId != null && type.Id == defaultTypeId
                });
            }

            // 用过的排前面：模型挑类型时，"这个项目实际在用什么"比字母序有用得多
            results = results.OrderByDescending(t => t.IsDefault)
                             .ThenByDescending(t => t.InstanceCount)
                             .ThenBy(t => t.Family, StringComparer.CurrentCulture)
                             .ThenBy(t => t.Name, StringComparer.CurrentCulture)
                             .ToList();

            var output = new ListTypesOutput
            {
                Total = results.Count,
                Truncated = results.Count > limit,
                Types = results.Take(limit).ToList()
            };

            output.Returned = output.Types.Count;

            if (output.Total == 0)
                context.Warnings.Add(
                    "类别 " + input.Category + " 下没有可用类型。该类别的族可能尚未载入本项目，" +
                    "门窗家具尤其常见——需要用户先在 Revit 里载入族。");

            return output;
        }

        /// <summary>一次遍历统计各类型的实例数，比逐类型建 collector 快一个数量级。</summary>
        private static Dictionary<long, int> CountInstances(Document document, BuiltInCategory category)
        {
            var counts = new Dictionary<long, int>();

            foreach (var element in new FilteredElementCollector(document)
                         .OfCategory(category)
                         .WhereElementIsNotElementType())
            {
                var typeId = element.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId) continue;

                var key = typeId.GetValue();
                int current;
                counts.TryGetValue(key, out current);
                counts[key] = current + 1;
            }

            return counts;
        }

        private static ElementId DefaultTypeId(Document document, BuiltInCategory category)
        {
            try
            {
                var categoryId = Category.GetCategory(document, category)?.Id;
                if (categoryId == null) return null;

                var typeId = document.GetDefaultFamilyTypeId(categoryId);
                return typeId == ElementId.InvalidElementId ? null : typeId;
            }
            catch
            {
                // 部分类别没有"默认类型"这个概念，标不出来不影响其余信息
                return null;
            }
        }

        /// <summary>
        /// 取整体厚度。墙、楼板、天花、屋顶都是 <c>HostObjAttributes</c>，
        /// 层结构一问即得——一句话覆盖四个类别，不必给每种类型写一个分支。
        /// </summary>
        private static double? ThicknessOf(ElementType type)
        {
            var hostType = type as HostObjAttributes;
            if (hostType == null) return null;

            try
            {
                var structure = hostType.GetCompoundStructure();
                if (structure == null) return null;   // 幕墙、叠层墙没有层结构

                return Units.Round(Units.FromFeet(structure.GetWidth()));
            }
            catch
            {
                return null;
            }
        }

        private static bool Matches(string needle, string name, string family)
        {
            if (string.IsNullOrWhiteSpace(needle)) return true;

            return (name != null && name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                   || (family != null && family.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string SafeName(ElementType type)
        {
            try { return type.Name; }
            catch { return null; }
        }

        private static string SafeFamilyName(ElementType type)
        {
            try { return type.FamilyName; }
            catch { return null; }
        }
    }

    // ==================== 标高清单 ====================

    public sealed class ListLevelsInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档。" +
                  "一个 Revit 可以同时开着多个项目，批量检查靠它逐个指定")]
        public string DocumentId { get; set; }
    }

    public sealed class LevelInfo
    {
        [McpParam("标高 ID。建模工具的 levelId 参数用的就是它")]
        public string Id { get; set; }

        [McpParam("标高名")]
        public string Name { get; set; }

        [McpParam("高程，毫米（项目基点为零）")]
        public double ElevationMm { get; set; }

        [McpParam("是否为建筑楼层")]
        public bool IsBuildingStory { get; set; }

        [McpParam("是否为活动视图所在的标高")]
        public bool IsActiveViewLevel { get; set; }
    }

    public sealed class ListLevelsOutput
    {
        [McpParam("标高数")]
        public int Total { get; set; }

        [McpParam("标高列表，按高程从低到高排列")]
        public List<LevelInfo> Levels { get; set; } = new List<LevelInfo>();
    }

    [McpTool("revit_list_levels",
        Title = "列出标高",
        Description = "列出模型中所有标高的 ID、名称与高程（毫米）。" +
                      "所有建模工具都用 levelId 指定标高而不是高度数值，先用它拿 ID。",
        ReadOnly = true,
        TimeoutSeconds = 30)]
    public sealed class ListLevelsTool : RevitTool<ListLevelsInput, ListLevelsOutput>
    {
        public override ListLevelsOutput Execute(ListLevelsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);

            ElementId activeLevelId = null;
            // 只有活动文档才谈得上"活动视图所在的标高"
            if (string.IsNullOrWhiteSpace(input.DocumentId))
            {
                try
                {
                    var genLevel = document.ActiveView == null ? null : document.ActiveView.GenLevel;
                    if (genLevel != null) activeLevelId = genLevel.Id;
                }
                catch { /* 某些视图类型取不到关联标高，不影响列表本身 */ }
            }

            var levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .Select(level => new LevelInfo
                {
                    Id = level.Id.ToProtocolString(),
                    Name = SafeName(level),
                    ElevationMm = Units.Round(Units.FromFeet(level.Elevation)),
                    IsBuildingStory = IsBuildingStory(level),
                    IsActiveViewLevel = activeLevelId != null && level.Id == activeLevelId
                })
                .ToList();

            if (levels.Count == 0)
                context.Warnings.Add("模型里没有任何标高，建模工具无法工作。");

            return new ListLevelsOutput { Total = levels.Count, Levels = levels };
        }

        private static bool IsBuildingStory(Level level)
        {
            try
            {
                var parameter = level.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY);
                return parameter != null && parameter.AsInteger() != 0;
            }
            catch
            {
                return false;
            }
        }

        private static string SafeName(Level level)
        {
            try { return level.Name; }
            catch { return null; }
        }
    }
}
