using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Tools
{
    // ==================== 项目单位 ====================

    public sealed class ProjectUnitsInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档。" +
                  "一个 Revit 可以同时开着多个项目，批量检查靠它逐个指定")]
        public string DocumentId { get; set; }
    }

    public sealed class UnitFormat
    {
        [McpParam("度量类型：Length / Area / Volume / Angle / MassDensity")]
        public string Spec { get; set; }

        [McpParam("跨 Revit 版本稳定的单位标识，如 millimeters、meters、feetFractionalInches")]
        public string Unit { get; set; }

        [McpParam("Revit 界面上显示的单位名，随 Revit 语言变化，仅供人读")]
        public string Label { get; set; }

        [McpParam("舍入精度，以该单位计。项目用默认设置时为 null")]
        public double? Accuracy { get; set; }
    }

    public sealed class ProjectUnitsOutput
    {
        [McpParam("项目的显示单位设置")]
        public List<UnitFormat> Units { get; set; } = new List<UnitFormat>();

        [McpParam("项目长度的显示单位标识，等同 Units 中 Spec 为 Length 的那一项")]
        public string LengthUnit { get; set; }

        [McpParam("本服务所有工具的长度入参与出参使用的单位，恒为 millimeters")]
        public string ToolLengthUnit { get; set; }

        [McpParam("项目显示单位与工具单位是否一致。为 false 时，用户口中的长度数字需要换算后再传给工具")]
        public bool MatchesToolUnit { get; set; }
    }

    [McpTool("revit_get_project_units",
        Title = "项目单位设置",
        Description = "返回当前项目的长度、面积、体积、角度显示单位。" +
                      "本服务所有工具的长度一律用毫米，与项目设置无关；" +
                      "当项目用英制时，用户说的「20 英尺」必须先换算成毫米再传给建模工具。" +
                      "涉及长度数值的对话开始前先调用它，可以避免整段对话的数字含义全错。",
        ReadOnly = true,
        TimeoutSeconds = 15)]
    public sealed class ProjectUnitsTool : RevitTool<ProjectUnitsInput, ProjectUnitsOutput>
    {
        private const string ToolUnit = "millimeters";

        public override ProjectUnitsOutput Execute(ProjectUnitsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);

            var output = new ProjectUnitsOutput { ToolLengthUnit = ToolUnit };

            foreach (var reading in UnitsCompat.ReadProjectUnits(document))
            {
                output.Units.Add(new UnitFormat
                {
                    Spec = reading.Spec,
                    Unit = reading.Unit,
                    Label = reading.Label,
                    Accuracy = reading.Accuracy
                });

                if (reading.Spec == "Length") output.LengthUnit = reading.Unit;
            }

            output.MatchesToolUnit = output.LengthUnit == ToolUnit;

            if (!output.MatchesToolUnit && output.LengthUnit != null)
                context.Warnings.Add(
                    "项目长度单位是「" + output.LengthUnit + "」，而本服务的工具一律收发毫米。" +
                    "把用户给的长度数字直接当毫米传会得到错误的尺寸。");

            return output;
        }
    }

    // ==================== 模型警告 ====================

    public sealed class GetWarningsInput
    {
        [McpParam("要查询的文档 ID，来自 revit_list_documents。省略则用当前活动文档。" +
                  "一个 Revit 可以同时开着多个项目，批量检查靠它逐个指定")]
        public string DocumentId { get; set; }

        [McpParam("只返回描述中包含该文本的警告（不区分大小写）")]
        public string DescriptionContains { get; set; }

        [McpParam("只返回与该构件相关的警告。用来回答「这个构件现在有什么问题」。ElementId 与 uniqueId 两种写法都接受")]
        public string ElementId { get; set; }

        [McpParam("最多返回多少组，默认 50。同一种警告归为一组")]
        public int? Limit { get; set; }

        [McpParam("每组最多列出多少个相关构件 ID，默认 20")]
        public int? MaxElementsPerGroup { get; set; }
    }

    public sealed class WarningGroup
    {
        [McpParam("警告描述，Revit 原文")]
        public string Description { get; set; }

        [McpParam("严重程度：Warning / Error / DocumentCorruption")]
        public string Severity { get; set; }

        [McpParam("该种警告的条数")]
        public int Count { get; set; }

        [McpParam("涉及的构件 ID（可能被 maxElementsPerGroup 截断）")]
        public List<string> ElementIds { get; set; } = new List<string>();

        [McpParam("构件 ID 是否被截断")]
        public bool ElementsTruncated { get; set; }
    }

    public sealed class GetWarningsOutput
    {
        [McpParam("匹配到的警告总条数")]
        public int Total { get; set; }

        [McpParam("归类后的组数")]
        public int GroupCount { get; set; }

        [McpParam("是否因 limit 而被截断")]
        public bool Truncated { get; set; }

        // 刻意不叫 warnings：那个字段名归管线所有（它往每个工具输出里挂服务端提示），
        // 工具再占用就会被悄悄盖掉——这个坑实测踩过一次
        [McpParam("警告分组，按条数从多到少排列")]
        public List<WarningGroup> Groups { get; set; } = new List<WarningGroup>();
    }

    [McpTool("revit_get_warnings",
        Title = "模型警告",
        Description = "返回 Revit 自己记录的模型警告（「警告」面板里的那些），按种类归组。" +
                      "建模之后调用它复查：图元重叠、房间未闭合、标识数据重复这类问题，" +
                      "Revit 已经替你发现了，不必自己做几何比对。",
        ReadOnly = true,
        TimeoutSeconds = 60)]
    public sealed class GetWarningsTool : RevitTool<GetWarningsInput, GetWarningsOutput>
    {
        private const int DefaultLimit = 50;
        private const int DefaultElementsPerGroup = 20;

        public override GetWarningsOutput Execute(GetWarningsInput input, ToolExecutionContext<UIApplication> context)
        {
            var document = ResolveDocument(context, input.DocumentId);

            var limit = Math.Max(input.Limit ?? DefaultLimit, 1);
            var perGroup = Math.Max(input.MaxElementsPerGroup ?? DefaultElementsPerGroup, 1);

            ElementId focus = null;
            if (!string.IsNullOrWhiteSpace(input.ElementId))
            {
                // 存在性由 RequireElement 校验：ID 打错时回一句「没有相关警告」是误导
                var element = RequireElement(document, input.ElementId);
                focus = element.Id;
            }

            IList<FailureMessage> messages;
            try
            {
                messages = document.GetWarnings();
            }
            catch (Exception ex)
            {
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "读取模型警告失败：" + ex.Message);
            }

            var groups = new Dictionary<GroupKey, WarningGroup>();
            var order = new List<WarningGroup>();
            var total = 0;

            foreach (var message in messages)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var description = SafeDescription(message);
                if (!Matches(input.DescriptionContains, description)) continue;

                var elementIds = RelatedIds(message);
                if (focus != null && !elementIds.Any(id => id == focus)) continue;

                total++;

                var key = new GroupKey(SafeSeverity(message), description);

                WarningGroup group;
                if (!groups.TryGetValue(key, out group))
                {
                    group = new WarningGroup { Description = description, Severity = key.Severity };
                    groups[key] = group;
                    order.Add(group);
                }

                group.Count++;

                foreach (var id in elementIds)
                {
                    if (group.ElementIds.Count >= perGroup) { group.ElementsTruncated = true; break; }

                    var text = id.ToProtocolString();
                    if (!group.ElementIds.Contains(text)) group.ElementIds.Add(text);
                }
            }

            var sorted = order.OrderByDescending(g => g.Count)
                              .ThenBy(g => g.Description, StringComparer.Ordinal)
                              .ToList();

            return new GetWarningsOutput
            {
                Total = total,
                GroupCount = sorted.Count,
                Truncated = sorted.Count > limit,
                Groups = sorted.Take(limit).ToList()
            };
        }

        /// <summary>
        /// 归组的键：严重程度 + 描述原文。
        /// 不把两者拼成一个字符串——描述是 Revit 的自由文本，
        /// 任何分隔符都可能恰好出现在里面，拼接就等着误合并。
        /// </summary>
        private struct GroupKey : IEquatable<GroupKey>
        {
            public readonly string Severity;
            public readonly string Description;

            public GroupKey(string severity, string description)
            {
                Severity = severity;
                Description = description;
            }

            public bool Equals(GroupKey other)
            {
                return string.Equals(Severity, other.Severity, StringComparison.Ordinal)
                       && string.Equals(Description, other.Description, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is GroupKey && Equals((GroupKey)obj);
            }

            public override int GetHashCode()
            {
                var severity = Severity == null ? 0 : Severity.GetHashCode();
                var description = Description == null ? 0 : Description.GetHashCode();
                return unchecked((severity * 397) ^ description);
            }
        }

        private static List<ElementId> RelatedIds(FailureMessage message)
        {
            var ids = new List<ElementId>();

            try { ids.AddRange(message.GetFailingElements()); }
            catch { /* 个别警告取不到构件，不影响它本身值得被看见 */ }

            try { ids.AddRange(message.GetAdditionalElements()); }
            catch { }

            return ids;
        }

        private static string SafeDescription(FailureMessage message)
        {
            try { return message.GetDescriptionText(); }
            catch { return "(无法读取描述)"; }
        }

        private static string SafeSeverity(FailureMessage message)
        {
            try { return message.GetSeverity().ToString(); }
            catch { return "Unknown"; }
        }

        private static bool Matches(string needle, string text)
        {
            if (string.IsNullOrWhiteSpace(needle)) return true;
            return text != null && text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
