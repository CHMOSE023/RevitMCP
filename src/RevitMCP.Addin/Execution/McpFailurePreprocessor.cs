using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Compat;

namespace RevitMCP.Addin.Execution
{
    /// <summary>
    /// 防线一：事务级的失败预处理。
    ///
    /// 没有它，Revit 会在事务提交时弹出模态的「警告」对话框等人点确定——
    /// 而此刻主线程正被我们的工具占着，没有人能去点那个按钮。
    /// 结果是 Revit 和 MCP 服务一起死锁，只能杀进程。
    ///
    /// 策略：
    ///   - 警告（Warning）→ 删除警告、继续提交。这正是用户在 UI 上点「确定」的效果。
    ///   - 错误及以上 → 回滚整个事务。能"解决"错误的通常是删构件，
    ///     替用户默默删掉模型里的东西，比失败严重得多。
    ///
    /// 两种情况都把原文记进 <see cref="WriteScopeInfo.Warnings"/>：
    /// 静默吞掉警告比弹框更危险——模型和用户都不会知道刚才发生过什么。
    /// </summary>
    internal sealed class McpFailurePreprocessor : IFailuresPreprocessor
    {
        private readonly IList<string> _warnings;

        public McpFailurePreprocessor(IList<string> warnings)
        {
            _warnings = warnings ?? new List<string>();
        }

        /// <summary>是否因为错误而要求回滚。提交后据此判断该不该报 TRANSACTION_FAILED。</summary>
        public bool RolledBackDueToError { get; private set; }

        /// <summary>导致回滚的错误描述，用于拼错误信息。</summary>
        public string ErrorText { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var messages = failuresAccessor.GetFailureMessages();
            if (messages == null || messages.Count == 0) return FailureProcessingResult.Continue;

            var hasError = false;

            // 同一种警告在一次批量创建里会出现几十条，逐条 Add 会把回执淹掉，
            // 而真正有用的是"哪一种、几条、涉及哪些构件"
            var grouped = new Dictionary<string, WarningGroup>(StringComparer.Ordinal);

            foreach (var message in messages)
            {
                var severity = message.GetSeverity();
                var text = Describe(message);

                if (severity == FailureSeverity.Warning)
                {
                    WarningGroup group;
                    if (!grouped.TryGetValue(text, out group))
                    {
                        group = new WarningGroup();
                        grouped.Add(text, group);
                    }

                    group.Count++;
                    CollectIds(message, group.ElementIds);

                    failuresAccessor.DeleteWarning(message);
                    continue;
                }

                // Error / DocumentCorruption：不尝试任何自动解决方案
                hasError = true;
                if (ErrorText == null) ErrorText = text;
                _warnings.Add("Revit 错误（已回滚）：" + text);
            }

            foreach (var pair in grouped) _warnings.Add(FormatWarning(pair.Key, pair.Value));

            if (!hasError) return FailureProcessingResult.Continue;

            RolledBackDueToError = true;
            return FailureProcessingResult.ProceedWithRollBack;
        }

        private sealed class WarningGroup
        {
            public int Count;
            public readonly List<string> ElementIds = new List<string>();
        }

        /// <summary>
        /// 一条警告该怎么说给模型听。
        ///
        /// **必须带上构件 ID。** 原先只有一句"高亮显示的楼板重叠"，
        /// 既不知道是哪两块、也无从复查——而那次它恰恰是唯一的求救信号：
        /// 三块楼板因为标高偏移的 bug 全落在了零高程上，回执里只留下三条一模一样的话。
        /// 几何冲突类的警告还要额外点一句"这通常意味着位置不对"，
        /// 让它不至于和"图元被略微移动了"这种噪音混在一起。
        /// </summary>
        private static string FormatWarning(string text, WarningGroup group)
        {
            var line = "Revit 警告已自动忽略：" + text;

            if (group.Count > 1) line += "（共 " + group.Count + " 条）";

            if (group.ElementIds.Count > 0)
            {
                var shown = group.ElementIds.Count <= MaxIdsPerWarning
                    ? group.ElementIds
                    : group.ElementIds.GetRange(0, MaxIdsPerWarning);

                line += " 涉及构件：" + string.Join("、", shown.ToArray());
                if (group.ElementIds.Count > shown.Count)
                    line += " 等 " + group.ElementIds.Count + " 个";
            }

            if (LooksLikeGeometryConflict(text))
                line += "。**重叠/重复这类警告通常说明构件放错了位置**——" +
                        "建完请用 revit_get_element_geometry 量一下它们的实际高程与范围，别只看这次调用没报错。";

            return line;
        }

        private static bool LooksLikeGeometryConflict(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            return text.IndexOf("重叠", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("重复", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("相同", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("overlap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("duplicate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("identical", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void CollectIds(FailureMessageAccessor message, List<string> into)
        {
            try
            {
                var ids = message.GetFailingElementIds();
                if (ids == null) return;

                foreach (var id in ids)
                {
                    if (id == null || id == ElementId.InvalidElementId) continue;

                    var text = id.GetValue().ToString(CultureInfo.InvariantCulture);
                    if (!into.Contains(text)) into.Add(text);
                }
            }
            catch { /* 拿不到 ID 不影响把警告本身说出来 */ }
        }

        /// <summary>一条警告里最多列几个构件 ID。再多就只报总数——回执不该被 ID 淹没。</summary>
        private const int MaxIdsPerWarning = 12;

        private static string Describe(FailureMessageAccessor message)
        {
            string text;
            try { text = message.GetDescriptionText(); }
            catch { text = null; }

            if (string.IsNullOrWhiteSpace(text))
            {
                try { text = message.GetFailureDefinitionId().Guid.ToString(); }
                catch { text = "(无描述)"; }
            }

            return text;
        }
    }
}
