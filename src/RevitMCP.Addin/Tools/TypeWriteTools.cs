using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 换类型 ====================

    public sealed class ChangeTypeInput
    {
        [McpParam("要换类型的构件 ID 列表。与 fromTypeId 二选一。ElementId 与 uniqueId 两种写法都接受")]
        public List<string> ElementIds { get; set; }

        [McpParam("把使用这个类型的所有实例统统换掉。与 elementIds 二选一——" +
                  "「把项目里所有 200 厚的墙换成 300 厚」用它，不必先查一遍 ID")]
        public string FromTypeId { get; set; }

        [McpParam("目标类型 ID，来自 revit_list_types 或 revit_duplicate_type 的回执", Required = true)]
        public string ToTypeId { get; set; }

        [McpParam("影响构件数超过上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class TypeChange
    {
        [McpParam("构件 ID")]
        public string Id { get; set; }

        [McpParam("构件名称")]
        public string Name { get; set; }

        [McpParam("原类型名")]
        public string OldType { get; set; }

        [McpParam("新类型名")]
        public string NewType { get; set; }
    }

    public sealed class ChangeTypeOutput : IReportsAffectedElements
    {
        [McpParam("目标类型名")]
        public string ToType { get; set; }

        [McpParam("成功换型的构件数")]
        public int Changed { get; set; }

        [McpParam("本来就是目标类型、这次没动的构件数")]
        public int AlreadySet { get; set; }

        [McpParam("逐个构件的新旧类型")]
        public List<TypeChange> Elements { get; set; } = new List<TypeChange>();

        int IReportsAffectedElements.AffectedElements => Changed;
    }

    /// <summary>
    /// 换构件类型。
    ///
    /// 两种寻址方式合成一个工具：按构件 ID 换，或按"原类型"整批换。
    /// 后者是实际工作里更常见的形态——「把所有 200 厚的墙换成 300 厚」——
    /// 而让模型先查一遍 ID 再传回来，中间任何一次遗漏都会变成漏改。
    /// </summary>
    [McpTool("revit_change_element_types",
        Title = "换构件类型",
        Description = "把一批构件换成另一个类型。可以按构件 ID 指定，也可以给 fromTypeId " +
                      "把用了某个类型的实例统统换掉。新旧类型必须属于同一类别。" +
                      "换型会重算几何——墙变厚会挤到相邻构件，改完值得用 revit_get_warnings 看一眼。" +
                      "整批要么全成、要么全不动，且在撤销栈里只占一步。",
        TimeoutSeconds = 120)]
    public sealed class ChangeTypeTool : RevitTool<ChangeTypeInput, ChangeTypeOutput>
    {
        public override ChangeTypeOutput Execute(
            ChangeTypeInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            var hasIds = input.ElementIds != null && input.ElementIds.Count > 0;
            var hasFrom = !string.IsNullOrWhiteSpace(input.FromTypeId);

            if (hasIds == hasFrom)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    hasIds
                        ? "elementIds 与 fromTypeId 只能给一个——同时给会产生两套互相矛盾的范围。"
                        : "必须给 elementIds（按构件换）或 fromTypeId（按原类型整批换）其中之一。");

            if (string.IsNullOrWhiteSpace(input.ToTypeId))
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "toTypeId 不能为空。用 revit_list_types 取目标类型的 ID。");

            var newType = RequireType(document, input.ToTypeId, "toTypeId");
            var elements = hasIds
                ? ResolveByIds(document, input.ElementIds)
                : ResolveByType(document, input.FromTypeId);

            if (elements.Count == 0)
                throw new ToolFailureException(McpDomainError.ElementNotFound,
                    "没有任何构件使用类型 " + input.FromTypeId + "，无需换型。");

            GuardScale(elements.Count, input.Confirm, context, "换类型");

            var output = new ChangeTypeOutput { ToType = SafeName(newType) };

            foreach (var element in elements)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var currentTypeId = element.GetTypeId();
                var change = new TypeChange
                {
                    Id = element.Id.ToProtocolString(),
                    Name = SafeName(element),
                    OldType = TypeNameOf(document, currentTypeId),
                    NewType = output.ToType
                };

                if (currentTypeId != null && currentTypeId.GetValue() == newType.Id.GetValue())
                {
                    output.AlreadySet++;
                    output.Elements.Add(change);
                    continue;
                }

                try
                {
                    element.ChangeTypeId(newType.Id);
                }
                catch (Exception ex)
                {
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "Revit 拒绝把构件 " + change.Id + "（" + (change.Name ?? "无名") +
                        "）换成类型「" + output.ToType + "」：" + ex.Message + "（整批未改动）。" +
                        "最常见的原因是新旧类型不属于同一类别——" +
                        "墙类型换不到楼板上去，门族也换不成窗族。");
                }

                output.Changed++;
                output.Elements.Add(change);
            }

            return output;
        }

        private static List<Element> ResolveByIds(Document document, IList<string> rawIds)
        {
            var elements = new List<Element>();
            var seen = new HashSet<long>();

            foreach (var rawId in rawIds)
            {
                var element = RequireElement(document, rawId);

                if (element is ElementType)
                    throw new ToolFailureException(McpDomainError.InvalidParameter,
                        "ID " + rawId + " 是一个类型，不是构件实例。" +
                        "要改类型自身的参数用 revit_set_type_parameters；" +
                        "要把用了它的实例换掉，把这个 ID 填到 fromTypeId。");

                if (seen.Add(element.Id.GetValue())) elements.Add(element);
            }

            return elements;
        }

        private static List<Element> ResolveByType(Document document, string rawTypeId)
        {
            var type = RequireType(document, rawTypeId, "fromTypeId");

            // ElementClassFilter 按不了"用了某类型"，只能过一遍同类别的实例再比 TypeId。
            // 限定类别是为了不去遍历整个模型
            var categoryId = type.Category?.Id;

            var collector = new FilteredElementCollector(document).WhereElementIsNotElementType();
            if (categoryId != null)
                collector = collector.OfCategoryId(categoryId);

            return collector
                .Where(e =>
                {
                    var typeId = e.GetTypeId();
                    return typeId != null && typeId.GetValue() == type.Id.GetValue();
                })
                .ToList();
        }

        internal static ElementType RequireType(Document document, string rawId, string fieldName)
        {
            // 类型也走同一套寻址：ElementId 或 UniqueId 都收。
            // 类型的 UniqueId 同样跨会话稳定，把"用哪个墙类型"写进企业标准时正需要它
            string problem;
            var element = ElementRef.Resolve(document, rawId, out problem);

            if (element == null)
                throw new ToolFailureException(
                    problem.Contains("格式") ? McpDomainError.InvalidParameter : McpDomainError.ElementNotFound,
                    fieldName + "：" + problem + "（用 revit_list_types 确认类型 ID）");

            var type = element as ElementType;
            if (type == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    fieldName + " " + rawId + " 不是族类型，而是「" +
                    (element.Category?.Name ?? element.GetType().Name) +
                    "」的一个实例。用 revit_list_types 取类型 ID。");

            return type;
        }

        private static string TypeNameOf(Document document, ElementId typeId)
        {
            if (typeId == null || typeId == ElementId.InvalidElementId) return null;
            return SafeName(document.GetElement(typeId));
        }

        private static string SafeName(Element element)
        {
            try { return element?.Name; }
            catch { return null; }
        }
    }

    // ==================== 改类型参数 ====================

    public sealed class SetTypeParametersInput
    {
        [McpParam("要修改的类型 ID 列表，来自 revit_list_types。ElementId 与 uniqueId 两种写法都接受", Required = true)]
        public List<string> TypeIds { get; set; }

        [McpParam("参数名，须与 revit_get_element_parameters（includeTypeParameters: true）" +
                  "返回的名称完全一致", Required = true)]
        public string ParameterName { get; set; }

        [McpParam("要写入的值，一律用字符串传递。数值按项目显示单位解释（如长度 \"3000\" 即 3000 毫米）；" +
                  "是/否类参数可用 true/false 或 是/否；ElementId 类参数（材质、标高这类）传目标构件的 ID，传 -1 或空字符串表示清空", Required = true)]
        public string Value { get; set; }

        [McpParam("数值按哪种单位解释：projectUnits（默认，项目显示单位）、internal（Revit 内部单位：长度英尺、角度弧度）。" +
                  "写不进去会报错，不会在两种单位之间回退猜测",
                  AllowedValues = new[] { "projectUnits", "internal" })]
        public string ValueMode { get; set; }

        [McpParam("影响类型数超过上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class TypeParameterChange
    {
        [McpParam("类型 ID")]
        public string Id { get; set; }

        [McpParam("类型名")]
        public string Name { get; set; }

        [McpParam("修改前的显示值")]
        public string OldValue { get; set; }

        [McpParam("修改后的显示值")]
        public string NewValue { get; set; }

        [McpParam("模型中使用该类型的构件数——这次改动会波及多少个构件")]
        public int InstanceCount { get; set; }
    }

    public sealed class SetTypeParametersOutput : IReportsAffectedElements
    {
        [McpParam("被修改的参数名")]
        public string ParameterName { get; set; }

        [McpParam("成功修改的类型数")]
        public int Changed { get; set; }

        [McpParam("这些类型在模型中的实例总数——改动实际波及的构件数")]
        public int AffectedInstances { get; set; }

        [McpParam("每个类型的新旧值")]
        public List<TypeParameterChange> Types { get; set; } = new List<TypeParameterChange>();

        // 审计关心的是"模型被动了多少"。改一个类型参数可能波及几百个实例，
        // 记成 1 会让审计日志严重低估这次操作的影响面
        int IReportsAffectedElements.AffectedElements => AffectedInstances;
    }

    /// <summary>
    /// 改类型参数。
    ///
    /// 与改实例参数是两件事，刻意不合并：改类型会同时改变用了它的每一个构件，
    /// 而调用方往往意识不到这一点。分成两个工具，名字本身就是一次提醒，
    /// 回执里的 affectedInstances 则把实际影响面摊开说。
    /// </summary>
    [McpTool("revit_set_type_parameters",
        Title = "批量改类型参数",
        Description = "把一批族类型的同一个参数设成同一个值。" +
                      "注意：改类型参数会同时改变模型中用了这个类型的每一个构件——" +
                      "回执里的 affectedInstances 就是实际波及的构件数。" +
                      "只想改其中几个构件的话，先用 revit_duplicate_type 复制出一个新类型，" +
                      "再用 revit_change_element_types 把那几个换过去。",
        TimeoutSeconds = 120)]
    public sealed class SetTypeParametersTool : RevitTool<SetTypeParametersInput, SetTypeParametersOutput>
    {
        public override SetTypeParametersOutput Execute(
            SetTypeParametersInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            if (input.TypeIds == null || input.TypeIds.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "typeIds 不能为空，至少要给一个类型。");

            if (string.IsNullOrWhiteSpace(input.ParameterName))
                throw new ToolFailureException(McpDomainError.InvalidParameter, "parameterName 不能为空。");

            if (input.Value == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "value 不能为 null。要清空一个文本参数，传空字符串。");

            var types = new List<ElementType>();
            var seen = new HashSet<long>();

            foreach (var rawId in input.TypeIds)
            {
                var type = ChangeTypeTool.RequireType(document, rawId, "typeIds");
                if (seen.Add(type.Id.GetValue())) types.Add(type);
            }

            // 先把影响面算清楚，再决定要不要拦。
            // 规模闸看的应当是"波及多少构件"，而不是"改了几个类型"——
            // 改 1 个类型波及 800 个构件，按类型数去数会直接放行
            var instanceCounts = CountInstances(document, types);
            var totalInstances = instanceCounts.Values.Sum();

            GuardScale(totalInstances, input.Confirm, context, "通过类型参数影响",
                "否则请减少 typeIds，或改用 revit_duplicate_type + revit_change_element_types " +
                "把改动限制在少数构件上。");

            var output = new SetTypeParametersOutput { ParameterName = input.ParameterName.Trim() };

            foreach (var type in types)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var parameter = FindParameter(type, output.ParameterName);
                var change = new TypeParameterChange
                {
                    Id = type.Id.ToProtocolString(),
                    Name = SafeName(type),
                    OldValue = ParameterWriter.DisplayOf(parameter)
                };

                instanceCounts.TryGetValue(type.Id.GetValue(), out var instances);
                change.InstanceCount = instances;

                ParameterWriter.Write(parameter, input.Value, type, ParameterWriter.ParseInternalMode(input.ValueMode));
                change.NewValue = ParameterWriter.DisplayOf(parameter);

                output.Types.Add(change);
                output.Changed++;
                output.AffectedInstances += instances;
            }

            return output;
        }

        private static Parameter FindParameter(ElementType type, string name)
        {
            var parameter = type.LookupParameter(name);

            if (parameter == null)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "类型「" + SafeName(type) + "」（ID " + type.Id.ToProtocolString() +
                    "）上没有名为 \"" + name + "\" 的参数。" +
                    "用 revit_get_element_parameters 带 includeTypeParameters: true 查看可用的类型参数名——" +
                    "参数名随项目语言变化，不要猜。");

            if (parameter.IsReadOnly)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "类型「" + SafeName(type) + "」的参数 \"" + name + "\" 是只读的，改不了。" +
                    "Revit 里由其他设置推算出来的参数（如墙的整体厚度）都是只读的——" +
                    "墙厚要改层结构，用 revit_duplicate_type 的 thicknessMm。");

            return parameter;
        }

        /// <summary>
        /// 统计每个类型在模型中有多少实例。
        /// 一次性过完所有相关类别，避免逐个类型去 collect 造成 O(类型数 × 模型大小)。
        /// </summary>
        private static Dictionary<long, int> CountInstances(Document document, IList<ElementType> types)
        {
            var wanted = new HashSet<long>(types.Select(t => t.Id.GetValue()));
            var counts = wanted.ToDictionary(id => id, id => 0);

            var categoryIds = types
                .Select(t => t.Category?.Id)
                .Where(id => id != null)
                .GroupBy(id => id.GetValue())
                .Select(g => g.First())
                .ToList();

            // 类型取不到类别时无法限定范围，只能整个模型过一遍。
            // 这种情况少见，但不能因此漏算影响面
            var collectors = categoryIds.Count > 0
                ? categoryIds.Select(id =>
                    new FilteredElementCollector(document).OfCategoryId(id).WhereElementIsNotElementType())
                : new[] { new FilteredElementCollector(document).WhereElementIsNotElementType() }.AsEnumerable();

            foreach (var collector in collectors)
            {
                foreach (var element in collector)
                {
                    var typeId = element.GetTypeId();
                    if (typeId == null) continue;

                    var key = typeId.GetValue();
                    if (!wanted.Contains(key)) continue;

                    counts[key] = counts[key] + 1;
                }
            }

            return counts;
        }

        private static string SafeName(Element element)
        {
            try { return element?.Name; }
            catch { return null; }
        }
    }

    // ==================== 族清单 ====================

    public sealed class ListFamiliesInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档")]
        public string DocumentId { get; set; }

        [McpParam("按类别过滤，如 OST_Doors。可省略 OST_ 前缀。省略则列出所有族")]
        public string Category { get; set; }

        [McpParam("按族名过滤（不区分大小写的子串匹配）")]
        public string NameContains { get; set; }

        [McpParam("是否一并列出每个族下的类型名，默认 false。" +
                  "要拿类型 ID 建模的话用 revit_list_types，那里给的信息更全")]
        public bool? IncludeTypes { get; set; }

        [McpParam("最多返回多少条，默认 200，上限 1000")]
        public int? Limit { get; set; }
    }

    public sealed class FamilyInfo
    {
        [McpParam("族 ID")]
        public string Id { get; set; }

        [McpParam("族名")]
        public string Name { get; set; }

        [McpParam("所属类别")]
        public string Category { get; set; }

        [McpParam("是否为可载入族（false 表示系统族，如基本墙——系统族不能编辑族文件）")]
        public bool IsInPlace { get; set; }

        [McpParam("该族下的类型数")]
        public int TypeCount { get; set; }

        [McpParam("类型名列表。仅 includeTypes 为 true 时有值")]
        public List<string> Types { get; set; }
    }

    public sealed class ListFamiliesOutput
    {
        [McpParam("匹配到的族总数（不受 limit 影响）")]
        public int Total { get; set; }

        [McpParam("本次实际返回的条数")]
        public int Returned { get; set; }

        [McpParam("是否因 limit 而被截断")]
        public bool Truncated { get; set; }

        [McpParam("族列表")]
        public List<FamilyInfo> Families { get; set; } = new List<FamilyInfo>();
    }

    [McpTool("revit_list_families",
        Title = "列出已载入的族",
        Description = "列出项目里已载入的族及其类型数。" +
                      "建模前用它确认目标族在不在项目里——没载入的族无法创建实例，" +
                      "而 Revit 对此的报错相当难懂。" +
                      "要拿具体的类型 ID 去建模，用 revit_list_types。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class ListFamiliesTool : RevitTool<ListFamiliesInput, ListFamiliesOutput>
    {
        private const int DefaultLimit = 200;
        private const int MaxLimit = 1000;

        public override ListFamiliesOutput Execute(
            ListFamiliesInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);
            var limit = Math.Min(Math.Max(input.Limit ?? DefaultLimit, 1), MaxLimit);
            var includeTypes = input.IncludeTypes ?? false;

            long? categoryId = null;
            if (!string.IsNullOrWhiteSpace(input.Category))
                categoryId = (long)ParseCategory(input.Category);

            var families = new FilteredElementCollector(document)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .ToList();

            var results = new List<FamilyInfo>();

            foreach (var family in families)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var name = SafeName(family);

                if (categoryId != null)
                {
                    var familyCategoryId = family.FamilyCategory?.Id;
                    if (familyCategoryId == null || familyCategoryId.GetValue() != categoryId.Value) continue;
                }

                if (!string.IsNullOrWhiteSpace(input.NameContains) &&
                    (name == null ||
                     name.IndexOf(input.NameContains, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;

                var info = new FamilyInfo
                {
                    Id = family.Id.ToProtocolString(),
                    Name = name,
                    Category = family.FamilyCategory?.Name
                };

                try { info.IsInPlace = family.IsInPlace; }
                catch { /* 少数族读不到这个标志，留默认值 */ }

                try
                {
                    var symbolIds = family.GetFamilySymbolIds();
                    info.TypeCount = symbolIds.Count;

                    if (includeTypes)
                        info.Types = symbolIds
                            .Select(id => SafeName(document.GetElement(id)))
                            .Where(n => n != null)
                            .OrderBy(n => n, StringComparer.CurrentCulture)
                            .ToList();
                }
                catch { /* 取不到类型不影响这一条的其他信息 */ }

                results.Add(info);
            }

            results = results
                .OrderBy(f => f.Category, StringComparer.CurrentCulture)
                .ThenBy(f => f.Name, StringComparer.CurrentCulture)
                .ToList();

            var output = new ListFamiliesOutput { Total = results.Count };

            if (results.Count > limit)
            {
                output.Truncated = true;
                results = results.Take(limit).ToList();
            }

            output.Families = results;
            output.Returned = results.Count;
            return output;
        }

        private static string SafeName(Element element)
        {
            try { return element?.Name; }
            catch { return null; }
        }
    }
}
