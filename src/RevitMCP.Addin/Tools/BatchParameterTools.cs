using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    /// <summary>
    /// 本文件的失败构造：把下标前缀写成 <c>updates[i]</c>，而不是通用的 elements[i]。
    ///
    /// 报错里出现的数组名必须是调用方真的传过的那个，否则它会去找一个不存在的参数。
    /// </summary>
    internal static class BatchFail
    {
        public static ToolFailureException At(int index, string code, string message)
        {
            return CreateSupport.Failure(index, code, message, "updates", "整批未改动");
        }
    }
    public sealed class ElementFilterSpec
    {
        [McpParam("按类别筛选，如 OST_Walls。可省略 OST_ 前缀")]
        public string Category { get; set; }

        [McpParam("按构件名筛选（不区分大小写的子串匹配）")]
        public string NameContains { get; set; }

        [McpParam("只要用了这个类型的构件，ID 来自 revit_list_types")]
        public string TypeId { get; set; }

        [McpParam("只要这个标高上的构件，ID 来自 revit_list_levels")]
        public string LevelId { get; set; }

        [McpParam("只要这个阶段创建的构件，ID 来自 revit_list_phases")]
        public string PhaseId { get; set; }

        [McpParam("只要这个设计选项里的构件，ID 来自 revit_list_design_options。" +
                  "传 \"main\" 表示只要主模型")]
        public string DesignOptionId { get; set; }

        [McpParam("只要某个参数等于指定值的构件。比较的是**显示值**" +
                  "（就是 revit_get_element_parameters 返回的 displayValue）")]
        public string WhereParameter { get; set; }

        [McpParam("whereParameter 要等于的值。whereParameter 给了它就必须给")]
        public string WhereEquals { get; set; }
    }

    public sealed class ParameterUpdateSpec
    {
        [McpParam("要修改的构件 ID。与 filter 二选一。ElementId 与 uniqueId 两种写法都接受")]
        public List<string> ElementIds { get; set; }

        [McpParam("按条件筛选要修改的构件。与 elementIds 二选一——" +
                  "「把所有外墙的防火等级设为 A」用它，不必先查一遍 ID 再传回来")]
        public ElementFilterSpec Filter { get; set; }

        [McpParam("要写入的参数，可以一次写多个。" +
                  "值一律用字符串：数值按项目显示单位解释，是/否用 true/false，" +
                  "ElementId 类参数（材质、标高）传目标构件的 ID", Required = true)]
        public List<ParameterValueSpec> Parameters { get; set; }
    }

    public sealed class BatchSetParametersInput
    {
        [McpParam("一批修改指令。每条指定「改哪些构件」和「改成什么」，" +
                  "不同指令之间可以改不同的参数", Required = true)]
        public List<ParameterUpdateSpec> Updates { get; set; }

        [McpParam("筛选没匹配到任何构件时是否算成功，默认 false（报错）。" +
                  "true 适合「有就改、没有就算了」的批处理场景")]
        public bool? AllowEmptyMatch { get; set; }

        [McpParam("影响构件数超过上限时，带上 true 表示确认后再执行")]
        public bool? Confirm { get; set; }
    }

    public sealed class BatchUpdateResult
    {
        [McpParam("对应 updates 数组中的下标，从 0 起")]
        public int Index { get; set; }

        [McpParam("这条指令匹配到多少个构件")]
        public int Matched { get; set; }

        [McpParam("实际写入的「构件 × 参数」次数")]
        public int Writes { get; set; }

        [McpParam("被写入的参数名")]
        public List<string> Parameters { get; set; } = new List<string>();

        [McpParam("匹配到的构件 ID（最多列前 100 个）")]
        public List<string> ElementIds { get; set; } = new List<string>();
    }

    public sealed class BatchSetParametersOutput : IReportsAffectedElements
    {
        [McpParam("被修改的构件总数（去重后）")]
        public int Changed { get; set; }

        [McpParam("总写入次数（构件 × 参数）")]
        public int TotalWrites { get; set; }

        [McpParam("逐条指令的结果")]
        public List<BatchUpdateResult> Updates { get; set; } = new List<BatchUpdateResult>();

        int IReportsAffectedElements.AffectedElements => Changed;
    }

    /// <summary>
    /// 批量改参数的完整形态。
    ///
    /// 与 <see cref="SetParametersTool"/>（一批 ID + 一个参数 + 一个值）的分工：
    /// 那个是最常用的简单情形，这个负责两件它做不到的事——
    /// **按条件匹配**（不用先查一遍 ID 再传回来，中间任何一次遗漏都是漏改），
    /// 以及**一次写多个参数**。
    ///
    /// 两者都保留，是因为简单情形占绝大多数，
    /// 而让每次改一个参数都要套三层嵌套结构，只会让调用更容易出错。
    /// </summary>
    [McpTool("revit_batch_set_parameters",
        Title = "批量改参数（多条件多参数）",
        Description = "按条件或按 ID 批量修改构件参数，一次可以写多个参数、下多条不同的指令。" +
                      "**按条件改**是它的主要用途：「把所有外墙的防火等级设为 A」直接给 filter，" +
                      "不用先查一遍 ID 再传回来。" +
                      "只改一个参数的简单情形用 revit_set_element_parameters 更省事。" +
                      "整批要么全成、要么全不动，且在撤销栈里只占一步。",
        TimeoutSeconds = 300)]
    public sealed class BatchSetParametersTool
        : RevitTool<BatchSetParametersInput, BatchSetParametersOutput>
    {
        private const int MaxReportedIds = 100;

        public override BatchSetParametersOutput Execute(
            BatchSetParametersInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = RequireDocument(context);

            if (input.Updates == null || input.Updates.Count == 0)
                throw new ToolFailureException(McpDomainError.InvalidParameter,
                    "updates 不能为空，至少要给一条指令。");

            // 先把每条指令匹配到的构件全部解析出来，再统一过规模闸、再统一写。
            // 边匹配边写的话，第一条指令的改动会影响第二条指令的筛选结果——
            // 那种依赖执行顺序的行为没人能预测
            var resolved = new List<Tuple<int, ParameterUpdateSpec, List<Element>>>();

            for (var index = 0; index < input.Updates.Count; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var spec = input.Updates[index];
                if (spec == null)
                    throw BatchFail.At(index, McpDomainError.InvalidParameter, "该项为 null。");

                ValidateParameters(spec, index);

                var elements = Resolve(document, spec, index, input.AllowEmptyMatch ?? false);
                resolved.Add(Tuple.Create(index, spec, elements));
            }

            var distinct = new HashSet<long>(
                resolved.SelectMany(r => r.Item3).Select(e => e.Id.GetValue()));

            GuardScale(distinct.Count, input.Confirm, context, "修改",
                "否则请收紧 filter（加上 nameContains、levelId 或 typeId），或分批调用。");

            var output = new BatchSetParametersOutput();

            foreach (var entry in resolved)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                output.Updates.Add(Apply(entry.Item1, entry.Item2, entry.Item3, context));
            }

            output.Changed = distinct.Count;
            output.TotalWrites = output.Updates.Sum(u => u.Writes);
            return output;
        }

        private static void ValidateParameters(ParameterUpdateSpec spec, int index)
        {
            if (spec.Parameters == null || spec.Parameters.Count == 0)
                throw BatchFail.At(index, McpDomainError.InvalidParameter,
                    "parameters 不能为空。");

            var duplicate = spec.Parameters
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name))
                .GroupBy(p => p.Name.Trim(), StringComparer.Ordinal)
                .FirstOrDefault(g => g.Count() > 1);

            if (duplicate != null)
                throw BatchFail.At(index, McpDomainError.InvalidParameter,
                    "参数 \"" + duplicate.Key + "\" 在同一条指令里出现了两次，" +
                    "后一个会覆盖前一个。请合并成一条。");
        }

        private static BatchUpdateResult Apply(
            int index, ParameterUpdateSpec spec, IList<Element> elements,
            ToolExecutionContext<UIApplication> context)
        {
            var result = new BatchUpdateResult
            {
                Index = index,
                Matched = elements.Count,
                Parameters = spec.Parameters.Select(p => p.Name.Trim()).ToList(),
                ElementIds = elements.Take(MaxReportedIds).Select(e => e.Id.ToProtocolString()).ToList()
            };

            if (elements.Count > MaxReportedIds)
                CreateSupport.Once(context,
                    "updates[" + index + "]：匹配到 " + elements.Count +
                    " 个构件，回执里只列出前 " + MaxReportedIds + " 个 ID。");

            var total = elements.Count;

            for (var position = 0; position < total; position++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var element = elements[position];

                foreach (var entry in spec.Parameters)
                {
                    var name = entry.Name.Trim();
                    var parameter = element.LookupParameter(name);

                    if (parameter == null)
                        throw BatchFail.At(index, McpDomainError.InvalidParameter,
                            "构件 " + element.Id.ToProtocolString() + "（" +
                            (element.Category?.Name ?? "未知类别") +
                            "）上没有名为 \"" + name + "\" 的参数（整批未改动）。" +
                            "同一条指令里的构件必须都有这个参数——" +
                            "用 filter.category 把范围限定到同一类别，" +
                            "或先用 revit_get_element_parameters 确认参数名。");

                    if (parameter.IsReadOnly)
                        throw BatchFail.At(index, McpDomainError.InvalidParameter,
                            "构件 " + element.Id.ToProtocolString() + " 的参数 \"" + name +
                            "\" 是只读的，改不了（整批未改动）。");

                    ParameterWriter.Write(parameter, entry.Value ?? string.Empty, element,
                        ParameterWriter.ParseInternalMode(entry.ValueMode));
                    result.Writes++;
                }

                ProgressTicker.Tick(context.Progress, position + 1, total, "已修改");
            }

            return result;
        }

        // ==================== 筛选 ====================

        private List<Element> Resolve(
            Document document, ParameterUpdateSpec spec, int index, bool allowEmpty)
        {
            var hasIds = spec.ElementIds != null && spec.ElementIds.Count > 0;
            var hasFilter = spec.Filter != null;

            if (hasIds == hasFilter)
                throw BatchFail.At(index, McpDomainError.InvalidParameter,
                    hasIds
                        ? "elementIds 与 filter 只能给一个——同时给会产生两套互相矛盾的范围。"
                        : "必须给 elementIds 或 filter 其中之一。");

            List<Element> elements;

            if (hasIds)
            {
                var seen = new HashSet<long>();
                elements = new List<Element>();

                foreach (var raw in spec.ElementIds)
                {
                    var element = RequireElement(document, raw);
                    if (seen.Add(element.Id.GetValue())) elements.Add(element);
                }
            }
            else
            {
                elements = Match(document, spec.Filter, index);
            }

            if (elements.Count == 0 && !allowEmpty)
                throw BatchFail.At(index, McpDomainError.ElementNotFound,
                    "这条指令没有匹配到任何构件（整批未改动）。" +
                    "先用 revit_query_elements 用同样的条件确认一下；" +
                    "如果「匹配不到也算正常」，带上 allowEmptyMatch: true。");

            return elements;
        }

        private List<Element> Match(Document document, ElementFilterSpec filter, int index)
        {
            if (string.IsNullOrWhiteSpace(filter.Category) &&
                string.IsNullOrWhiteSpace(filter.TypeId) &&
                string.IsNullOrWhiteSpace(filter.LevelId))
                throw BatchFail.At(index, McpDomainError.InvalidParameter,
                    "filter 至少要给 category、typeId 或 levelId 其中之一。" +
                    "只按名字或参数值筛选会扫描整个模型，而且几乎一定会匹配到意料之外的东西。");

            var collector = new FilteredElementCollector(document).WhereElementIsNotElementType();

            if (!string.IsNullOrWhiteSpace(filter.Category))
                collector = collector.OfCategory(ParseCategory(filter.Category));

            IEnumerable<Element> query = collector;

            if (!string.IsNullOrWhiteSpace(filter.TypeId))
            {
                var typeId = RequireTypeId(document, filter.TypeId, index);
                query = query.Where(e =>
                {
                    var id = e.GetTypeId();
                    return id != null && id.GetValue() == typeId;
                });
            }

            if (!string.IsNullOrWhiteSpace(filter.LevelId))
            {
                var levelId = RequireLevelId(document, filter.LevelId, index);
                query = query.Where(e =>
                {
                    var id = e.LevelId;
                    return id != null && id.GetValue() == levelId;
                });
            }

            if (!string.IsNullOrWhiteSpace(filter.NameContains))
                query = query.Where(e => NameMatches(e, filter.NameContains));

            if (!string.IsNullOrWhiteSpace(filter.PhaseId))
            {
                var phaseId = RequirePhaseId(document, filter.PhaseId, index);
                query = query.Where(e => IdParameterEquals(e, BuiltInParameter.PHASE_CREATED, phaseId));
            }

            if (!string.IsNullOrWhiteSpace(filter.DesignOptionId))
            {
                var optionId = RequireDesignOptionId(document, filter.DesignOptionId, index);
                query = query.Where(e => DesignOptionEquals(e, optionId));
            }

            if (!string.IsNullOrWhiteSpace(filter.WhereParameter))
            {
                if (filter.WhereEquals == null)
                    throw BatchFail.At(index, McpDomainError.InvalidParameter,
                        "给了 whereParameter 就必须给 whereEquals。" +
                        "要匹配空值，传空字符串。");

                var name = filter.WhereParameter.Trim();
                query = query.Where(e => ParameterEquals(e, name, filter.WhereEquals));
            }

            return query.ToList();
        }

        private static bool NameMatches(Element element, string needle)
        {
            try
            {
                var name = element.Name;
                return name != null && name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 按显示值比参数。
        ///
        /// 比显示值而不是原始值，是因为调用方拿到的就是显示值——
        /// revit_get_element_parameters 给的 displayValue 是什么，这里就能拿什么来筛。
        /// 让调用方去换算内部单位再来筛，等于把一类必然出错的转换推给它。
        /// </summary>
        private static bool ParameterEquals(Element element, string name, string expected)
        {
            try
            {
                var parameter = element.LookupParameter(name);
                if (parameter == null) return false;

                var display = ParameterWriter.DisplayOf(parameter) ?? string.Empty;
                return string.Equals(display.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool IdParameterEquals(Element element, BuiltInParameter which, long expected)
        {
            try
            {
                var parameter = element.get_Parameter(which);
                if (parameter == null || !parameter.HasValue) return false;

                var id = parameter.AsElementId();
                return id != null && id.GetValue() == expected;
            }
            catch { return false; }
        }

        private static bool DesignOptionEquals(Element element, long expected)
        {
            try
            {
                var id = element.DesignOption?.Id;
                var actual = id == null ? ElementId.InvalidElementId.GetValue() : id.GetValue();

                return actual == expected;
            }
            catch { return false; }
        }

        private long RequireTypeId(Document document, string raw, int index)
        {
            var element = CreateSupport.RequireElement(document, raw, index);

            if (!(element is ElementType))
                throw BatchFail.At(index, McpDomainError.InvalidParameter,
                    "filter.typeId " + raw + " 不是族类型。用 revit_list_types 取 ID。");

            return element.Id.GetValue();
        }

        private long RequireLevelId(Document document, string raw, int index)
        {
            var element = CreateSupport.RequireElement(document, raw, index);

            if (!(element is Level))
                throw BatchFail.At(index, McpDomainError.InvalidParameter,
                    "filter.levelId " + raw + " 不是标高。用 revit_list_levels 取 ID。");

            return element.Id.GetValue();
        }

        private long RequirePhaseId(Document document, string raw, int index)
        {
            var element = CreateSupport.RequireElement(document, raw, index);

            if (!(element is Phase))
                throw BatchFail.At(index, McpDomainError.InvalidParameter,
                    "filter.phaseId " + raw + " 不是阶段。用 revit_list_phases 取 ID。");

            return element.Id.GetValue();
        }

        private long RequireDesignOptionId(Document document, string raw, int index)
        {
            if (string.Equals(raw.Trim(), "main", StringComparison.OrdinalIgnoreCase))
                return ElementId.InvalidElementId.GetValue();

            var element = CreateSupport.RequireElement(document, raw, index);

            if (!(element is DesignOption))
                throw BatchFail.At(index, McpDomainError.InvalidParameter,
                    "filter.designOptionId " + raw + " 不是设计选项。" +
                    "用 revit_list_design_options 取 ID，或传 \"main\" 表示主模型。");

            return element.Id.GetValue();
        }
    }
}
