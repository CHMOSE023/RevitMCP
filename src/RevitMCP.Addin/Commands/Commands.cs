using System;
using System.Diagnostics;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Diagnostics;

namespace RevitMCP.Addin.Commands
{
    /// <summary>
    /// Ribbon 按钮的命令实现。
    /// Revit 要求每个命令是 public、带无参构造函数的类，且实现 IExternalCommand。
    /// 这些命令只操作服务状态，不碰文档，因此都是 TransactionMode.Manual 且不开事务。
    /// </summary>
    internal static class CommandHelper
    {
        /// <summary>统一的异常出口：命令里抛异常会让 Revit 弹通用错误框，信息量太低。</summary>
        public static Result Guard(string title, Func<Result> action, ref string message)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                Log.Error(title + " 执行失败。", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class ToggleServerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CommandHelper.Guard("启动/停止服务", () =>
            {
                var app = App.Current;
                if (app == null)
                {
                    TaskDialog.Show("RevitMCP", "插件未正确初始化，请查看日志。");
                    return Result.Failed;
                }

                if (app.Server.IsRunning) app.Server.Stop();
                else app.Server.Start();

                app.RefreshRibbon();
                return Result.Succeeded;
            }, ref message);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class ToggleWriteModeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CommandHelper.Guard("切换写入模式", () =>
            {
                var app = App.Current;
                if (app == null) return Result.Failed;

                var enabling = !app.Config.WriteEnabled;
                if (enabling)
                {
                    // 开启写入意味着模型可被 AI 修改，这一步必须是用户的明确决定
                    var dialog = new TaskDialog("RevitMCP · 开启写入模式")
                    {
                        MainInstruction = "允许 MCP 工具修改当前模型？",
                        MainContent =
                            "开启后，连接到本服务的客户端可以创建、修改、删除模型中的构件。\n\n" +
                            "每次修改都在独立事务中执行，可在 Revit 中单步撤销；" +
                            "单次修改超过 " + app.Config.MaxElementsPerWrite + " 个构件时会被拒绝。\n\n" +
                            "建议仅在需要时开启，用完及时关闭。",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.No
                    };
                    if (dialog.Show() != TaskDialogResult.Yes) return Result.Cancelled;
                }

                app.Server.SetWriteEnabled(enabling);
                app.RefreshRibbon();
                return Result.Succeeded;
            }, ref message);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class CopyConnectCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CommandHelper.Guard("复制接入命令", () =>
            {
                var app = App.Current;
                if (app == null) return Result.Failed;

                if (!app.Server.IsRunning)
                {
                    TaskDialog.Show("RevitMCP", "服务尚未启动，请先点击「启动服务」。");
                    return Result.Cancelled;
                }

                var command = app.Server.BuildConnectCommand();
                System.Windows.Clipboard.SetText(command);

                TaskDialog.Show("RevitMCP",
                    "接入命令已复制到剪贴板：\n\n" + command +
                    "\n\n在终端中执行即可把本 Revit 实例接入 Claude Code。");
                return Result.Succeeded;
            }, ref message);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class OpenLogCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return CommandHelper.Guard("打开日志", () =>
            {
                var path = Log.FilePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    TaskDialog.Show("RevitMCP", "日志文件尚未生成。");
                    return Result.Cancelled;
                }

                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return Result.Succeeded;
            }, ref message);
        }
    }
}
