using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitMCP.Addin.Diagnostics;

namespace RevitMCP.Addin.Execution
{
    /// <summary>
    /// 防线二：兜底拦截任何仍然冒出来的模态对话框。
    ///
    /// 失败预处理器管不到的对话框有的是——族载入冲突、"要把图元移到最近的标高吗"、
    /// 各种 TaskDialog。任何一个弹出来都会把主线程连同整个服务一起冻住。
    ///
    /// <b>必须用 using / try-finally 解绑。</b>漏解绑的后果不是这次调用出错，
    /// 而是此后用户自己操作 Revit 时的正常对话框也被悄悄吃掉——
    /// 那是会被当成"Revit 坏了"的严重回归，而且极难归因到这里。
    /// </summary>
    internal sealed class DialogSuppressor : IDisposable
    {
        /// <summary>
        /// 已知可以安全自动确认的对话框：内容是"告诉你一声"，点确定不会改模型。
        ///
        /// 这张表是本类唯一允许"替用户点是"的地方。往里加之前请确认：
        /// 这个对话框的默认按钮到底会做什么——有些对话框的"确定"意味着
        /// 删掉放不下的构件、或接受一次几何修复。
        /// </summary>
        private static readonly HashSet<string> AutoConfirm = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 导出/打印时提示"当前视图处于临时视图模式"——纯提示
            "TaskDialog_Really_Print_Or_Export_Temp_View_Modes",
            // "未保存的修改将丢失"之类的信息框，出现在我们自己先确认过的路径上
            "TaskDialog_Local_Changes_Not_Synchronized"
        };

        private const int ResultOk = 1;

        private readonly UIApplication _application;
        private readonly IList<string> _warnings;
        private readonly bool _autoConfirmUnknown;
        private bool _disposed;

        /// <param name="autoConfirmUnknown">
        /// 认不出来的对话框是不是也自动点确定。默认 false——
        /// 一个不认识的模态框，"确定"可能意味着任何事情。
        /// </param>
        public DialogSuppressor(UIApplication application, IList<string> warnings, bool autoConfirmUnknown = false)
        {
            _application = application;
            _warnings = warnings ?? new List<string>();
            _autoConfirmUnknown = autoConfirmUnknown;

            if (_application != null)
                _application.DialogBoxShowing += OnDialogBoxShowing;
        }

        /// <summary>被拦下的对话框数量。用于判断这次执行是否值得让模型多看一眼。</summary>
        public int SuppressedCount { get; private set; }

        /// <summary>有没有出现"需要人来决定"的对话框。有的话，这次操作的结果值得让用户亲自看一眼。</summary>
        public bool NeedsUserAction { get; private set; }

        /// <summary>
        /// 拦下一个模态对话框。
        ///
        /// **不再一律 OverrideResult(1)。** 数字 1 只是"第一个按钮"，
        /// 它在不同对话框上分别意味着确定、是、删除、覆盖——
        /// 替用户在一个认不出来的对话框上点下去，等于把未知后果写进他的模型，
        /// 而回执里只会留下一句"已按默认选项处理"。
        ///
        /// 现在的规则：白名单里的照常确认；其余的优先**取消**——
        /// 取消会让这一步失败、事务回滚，模型原样不动，调用方看到的是一句
        /// 说得清楚的"需要人来决定"。只有连取消都不支持时才退回点确定。
        /// </summary>
        private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            SuppressedCount++;

            var description = Describe(e);
            var id = e.DialogId ?? string.Empty;

            if (_autoConfirmUnknown || AutoConfirm.Contains(id))
            {
                if (TryOverride(e, description, "已知可自动确认")) return;
                if (TryCancel(e, description, "自动确认失败")) return;
                return;
            }

            NeedsUserAction = true;

            if (TryCancel(e, description,
                    "这个对话框不在可自动确认的白名单里，替你点「确定」可能意味着任何事情")) return;

            // 不可取消：让它一直弹着会连同整个服务一起冻住，只能点确定并把话说重一点
            if (TryOverride(e, description, "该对话框不可取消，只能自动确认")) return;

            _warnings.Add("Revit 弹出了对话框「" + description +
                          "」，既取消不了也确认不了。这次调用可能会卡到超时，" +
                          "请到 Revit 界面上看看。");
        }

        private bool TryCancel(DialogBoxShowingEventArgs e, string description, string why)
        {
            try
            {
                if (!e.Cancellable) return false;

                e.Cancel();
                _warnings.Add("已取消 Revit 对话框「" + description + "」：" + why +
                              "。**这一步因此没有完成，模型保持原样**——" +
                              "请在 Revit 界面上确认该怎么选，或换一种不触发它的做法。");
                Log.Warn("取消模态对话框：" + description + "（" + why + "）");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("取消对话框失败：" + ex.Message);
                return false;
            }
        }

        private bool TryOverride(DialogBoxShowingEventArgs e, string description, string why)
        {
            try
            {
                e.OverrideResult(ResultOk);
                _warnings.Add("已自动确认 Revit 对话框「" + description + "」：" + why + "。");
                Log.Warn("自动确认模态对话框：" + description + "（" + why + "）");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("OverrideResult 失败：" + ex.Message);
                return false;
            }
        }

        private static string Describe(DialogBoxShowingEventArgs e)
        {
            var id = string.IsNullOrWhiteSpace(e.DialogId) ? "(无 ID)" : e.DialogId;

            var task = e as TaskDialogShowingEventArgs;
            if (task != null && !string.IsNullOrWhiteSpace(task.Message))
                return id + " · " + Shorten(task.Message);

            return id;
        }

        private static string Shorten(string text)
        {
            var single = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return single.Length <= 200 ? single : single.Substring(0, 200) + "…";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_application != null)
                _application.DialogBoxShowing -= OnDialogBoxShowing;
        }
    }
}
