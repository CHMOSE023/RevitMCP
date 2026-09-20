using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Diagnostics;
using RevitMCP.Tooling;

namespace RevitMCP.Addin.Execution
{
    /// <summary>
    /// 写工具的执行作用域：一个工具调用 = 一个事务 = 撤销栈里的一步。
    ///
    /// 事务名统一为 <c>MCP: 工具名</c>，用户按 Ctrl+Z 时能看懂撤销的是什么、
    /// 也能只撤销这一步——这是"让模型改模型"这件事可被接受的前提。
    ///
    /// 两道防模态框的防线都装在这里（<see cref="McpFailurePreprocessor"/> 与
    /// <see cref="DialogSuppressor"/>），工具作者不需要知道它们存在。
    /// </summary>
    public sealed class RevitWriteScope : IWriteScope<UIApplication>
    {
        private readonly Func<bool> _autoConfirmUnknownDialogs;

        /// <param name="autoConfirmUnknownDialogs">
        /// 认不出来的模态对话框是否也自动确认。用委托而非快照，
        /// 这样用户改了配置立刻生效，不必重启 Revit。省略即"不自动确认"。
        /// </param>
        public RevitWriteScope(Func<bool> autoConfirmUnknownDialogs = null)
        {
            _autoConfirmUnknownDialogs = autoConfirmUnknownDialogs ?? (() => false);
        }

        public TResult Run<TResult>(UIApplication host, WriteScopeInfo info, Func<TResult> work)
        {
            var document = host?.ActiveUIDocument?.Document;

            // 没有文档不在这里报错：工具里的 RequireDocument 会给出 NO_ACTIVE_DOC，
            // 那条信息比"事务开不起来"对模型有用得多
            if (document == null) return work();

            if (document.IsReadOnly)
                throw new ToolFailureException(McpDomainError.TransactionFailed,
                    "当前文档是只读的（可能是链接模型或以只读方式打开），无法修改。");

            using (var dialogs = new DialogSuppressor(host, info.Warnings, SafeAutoConfirm()))
            using (var transaction = new Transaction(document, "MCP: " + info.ToolName))
            {
                if (transaction.Start() != TransactionStatus.Started)
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "无法开启事务，Revit 可能正处于另一个事务或特殊模式中。");

                var preprocessor = new McpFailurePreprocessor(info.Warnings);
                ApplyFailureHandling(transaction, preprocessor);

                TResult result;
                try
                {
                    result = work();
                }
                catch
                {
                    // 工具失败就当它没发生过。异常继续上抛，由管线映射成领域错误码
                    SafeRollBack(transaction, info.ToolName);
                    throw;
                }

                var status = transaction.Commit();

                if (preprocessor.RolledBackDueToError)
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "Revit 报告了错误，改动已全部回滚：" + (preprocessor.ErrorText ?? "(无描述)"));

                if (status != TransactionStatus.Committed)
                    throw new ToolFailureException(McpDomainError.TransactionFailed,
                        "事务未能提交（状态：" + status + "），模型未被修改。");

                if (dialogs.SuppressedCount > 0)
                    Log.Warn(info.ToolName + " 执行期间拦截了 " + dialogs.SuppressedCount + " 个对话框。");

                if (dialogs.NeedsUserAction)
                    info.Warnings.Add(
                        "这次执行期间 Revit 弹出了本服务认不出来的对话框，已按「不替用户做决定」处理。" +
                        "回执里的结果可能不完整，请核对模型，或到 Revit 界面上把这一步走一遍。");

                return result;
            }
        }

        private bool SafeAutoConfirm()
        {
            try { return _autoConfirmUnknownDialogs(); }
            catch { return false; }
        }

        private static void ApplyFailureHandling(Transaction transaction, IFailuresPreprocessor preprocessor)
        {
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(preprocessor);

            // 关掉强制模态：否则 Revit 在某些失败上仍坚持弹窗，预处理器就白装了
            options.SetForcedModalHandling(false);

            // 回滚后清掉失败列表，免得它们跟着下一个事务一起再冒出来
            options.SetClearAfterRollback(true);

            transaction.SetFailureHandlingOptions(options);
        }

        private static void SafeRollBack(Transaction transaction, string toolName)
        {
            try
            {
                if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            }
            catch (Exception ex)
            {
                // 回滚失败时 Transaction.Dispose 还会再兜一次；这里只记录，
                // 绝不能让它盖住工具本身抛出的那个更有信息量的异常
                Log.Error(toolName + " 回滚事务失败。", ex);
            }
        }
    }
}
