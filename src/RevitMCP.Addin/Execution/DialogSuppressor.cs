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
        private readonly UIApplication _application;
        private readonly IList<string> _warnings;
        private bool _disposed;

        public DialogSuppressor(UIApplication application, IList<string> warnings)
        {
            _application = application;
            _warnings = warnings ?? new List<string>();

            if (_application != null)
                _application.DialogBoxShowing += OnDialogBoxShowing;
        }

        /// <summary>被拦下的对话框数量。用于判断这次执行是否值得让模型多看一眼。</summary>
        public int SuppressedCount { get; private set; }

        private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            SuppressedCount++;

            var description = Describe(e);
            _warnings.Add("已自动关闭 Revit 对话框：" + description + "（按默认选项处理）");
            Log.Warn("拦截到模态对话框：" + description);

            try
            {
                // 1 = IDOK / TaskDialogResult.Ok。对话框种类太多，没有一个能覆盖全部的
                // "正确"答案；选默认项至少让操作走完，而真正发生了什么已经记进 warnings。
                e.OverrideResult(1);
            }
            catch (Exception ex)
            {
                // 少数对话框不接受 OverrideResult。取消掉总好过让它显示出来卡死主线程。
                Log.Warn("OverrideResult 失败，改为取消对话框：" + ex.Message);
                try { if (e.Cancellable) e.Cancel(); }
                catch { /* 连取消都不行，只能让它弹——这时只剩调用超时兜底 */ }
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
