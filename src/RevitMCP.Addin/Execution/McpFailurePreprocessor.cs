using System.Collections.Generic;
using Autodesk.Revit.DB;

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

            foreach (var message in messages)
            {
                var severity = message.GetSeverity();
                var text = Describe(message);

                if (severity == FailureSeverity.Warning)
                {
                    _warnings.Add("Revit 警告已自动忽略：" + text);
                    failuresAccessor.DeleteWarning(message);
                    continue;
                }

                // Error / DocumentCorruption：不尝试任何自动解决方案
                hasError = true;
                if (ErrorText == null) ErrorText = text;
                _warnings.Add("Revit 错误（已回滚）：" + text);
            }

            if (!hasError) return FailureProcessingResult.Continue;

            RolledBackDueToError = true;
            return FailureProcessingResult.ProceedWithRollBack;
        }

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
