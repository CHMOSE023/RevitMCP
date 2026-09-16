using System;
using System.Reflection;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Diagnostics;
using RevitMCP.Addin.Server;

namespace RevitMCP.Addin.Ribbon
{
    /// <summary>构建 Ribbon 面板，并随服务状态刷新按钮文案与图标。</summary>
    internal sealed class RibbonController
    {
        private const string TabName = "RevitMCP";
        private const string PanelName = "服务";

        private PushButton _toggleButton;
        private PushButton _writeModeButton;

        public static RibbonController Build(UIControlledApplication application)
        {
            var controller = new RibbonController();
            controller.CreateUi(application);
            return controller;
        }

        private void CreateUi(UIControlledApplication application)
        {
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // 选项卡已存在（同一 Revit 会话中重复加载，或另一插件占用了同名选项卡）
            }

            var panel = application.CreateRibbonPanel(TabName, PanelName);
            var assemblyPath = Assembly.GetExecutingAssembly().Location;

            _toggleButton = AddButton(panel, assemblyPath,
                name: "RevitMcpToggleServer",
                text: "启动服务",
                className: "RevitMCP.Addin.Commands.ToggleServerCommand",
                tooltip: "启动或停止 RevitMCP 服务。",
                glyph: "M", color: IconFactory.Idle);

            _writeModeButton = AddButton(panel, assemblyPath,
                name: "RevitMcpWriteMode",
                text: "写入：关",
                className: "RevitMCP.Addin.Commands.ToggleWriteModeCommand",
                tooltip: "允许 MCP 工具修改模型。默认关闭。",
                longDescription: "关闭时，所有写操作工具会返回 WRITE_DISABLED，只有只读工具可用。" +
                                 "开启后，每个写工具都在独立事务中执行，可在 Revit 中单步撤销。",
                glyph: "W", color: IconFactory.Idle);

            panel.AddSeparator();

            AddButton(panel, assemblyPath,
                name: "RevitMcpCopyConnect",
                text: "复制接入命令",
                className: "RevitMCP.Addin.Commands.CopyConnectCommand",
                tooltip: "把 claude mcp add 命令（含访问令牌）复制到剪贴板。",
                glyph: "C", color: IconFactory.Neutral);

            AddButton(panel, assemblyPath,
                name: "RevitMcpOpenLog",
                text: "打开日志",
                className: "RevitMCP.Addin.Commands.OpenLogCommand",
                tooltip: "打开当前 Revit 进程的 RevitMCP 日志文件。",
                glyph: "L", color: IconFactory.Neutral);

            Log.Info("Ribbon 面板已创建。");
        }

        private static PushButton AddButton(
            RibbonPanel panel, string assemblyPath, string name, string text, string className,
            string tooltip, string glyph, System.Windows.Media.Color color, string longDescription = null)
        {
            var data = new PushButtonData(name, text, assemblyPath, className)
            {
                ToolTip = tooltip,
                LargeImage = IconFactory.Create(glyph, color, 32),
                Image = IconFactory.Create(glyph, color, 16)
            };
            if (longDescription != null) data.LongDescription = longDescription;

            return (PushButton)panel.AddItem(data);
        }

        /// <summary>服务状态变化后刷新界面。必须在 Revit UI 线程调用。</summary>
        public void Refresh(ServerHost host, bool writeEnabled)
        {
            try
            {
                if (_toggleButton != null)
                {
                    _toggleButton.ItemText = host.IsRunning ? "停止服务" : "启动服务";
                    _toggleButton.ToolTip = "状态：" + host.StatusText;

                    var color = host.State == ServerState.Faulted ? IconFactory.Danger
                              : host.IsRunning ? IconFactory.Active
                              : IconFactory.Idle;
                    _toggleButton.LargeImage = IconFactory.Create("M", color, 32);
                    _toggleButton.Image = IconFactory.Create("M", color, 16);
                }

                if (_writeModeButton != null)
                {
                    _writeModeButton.ItemText = writeEnabled ? "写入：开" : "写入：关";
                    var color = writeEnabled ? IconFactory.Warning : IconFactory.Idle;
                    _writeModeButton.LargeImage = IconFactory.Create("W", color, 32);
                    _writeModeButton.Image = IconFactory.Create("W", color, 16);
                }
            }
            catch (Exception ex)
            {
                Log.Error("刷新 Ribbon 状态失败。", ex);   // 界面问题不应影响服务
            }
        }
    }
}
