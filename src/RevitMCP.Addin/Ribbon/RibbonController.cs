using System;
using System.Reflection;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Diagnostics;
using RevitMCP.Addin.Server;

namespace RevitMCP.Addin.Ribbon
{
    /// <summary>
    /// 构建 Ribbon 面板，并随服务状态刷新按钮文案与图标。
    ///
    /// 分两个面板：写入开关单独占一个。它是整个插件里唯一决定"模型能不能被改"的闸门，
    /// 和「打开日志」并排会让两者看起来同样重要。面板标题「写保护」本身也是一次提醒。
    ///
    /// 两个开关一律显示**状态**而不是动作。「停止服务」这种动作式文案要用户反推当前状态，
    /// 而反推错的代价是把正在用的服务关掉；「服务：运行中」扫一眼就够了。
    /// 点下去会发生什么，交给 tooltip 说。
    /// </summary>
    internal sealed class RibbonController
    {
        private const string TabName = "RevitMCP";
        private const string ServicePanelName = "MCP 服务";
        private const string ProtectionPanelName = "操作模式";

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

            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var servicePanel = application.CreateRibbonPanel(TabName, ServicePanelName);

            _toggleButton = AddButton(servicePanel, assemblyPath,
                name: "RevitMcpToggleServer",
                text: "服务已停止",
                className: "RevitMCP.Addin.Commands.ToggleServerCommand",
                tooltip: "点击启动 RevitMCP 服务。",
                glyph: Glyph.Stopped, color: IconFactory.Idle);

            AddConnectMenu(servicePanel, assemblyPath);

            AddButton(servicePanel, assemblyPath,
                name: "RevitMcpOpenLog",
                text: "打开日志",
                className: "RevitMCP.Addin.Commands.OpenLogCommand",
                tooltip: "打开当前 Revit 进程的 RevitMCP 日志文件。",
                glyph: Glyph.Document, color: IconFactory.Neutral);

            var protectionPanel = application.CreateRibbonPanel(TabName, ProtectionPanelName);

            _writeModeButton = AddButton(protectionPanel, assemblyPath,
                name: "RevitMcpWriteMode",
                text: "浏览模型",
                className: "RevitMCP.Addin.Commands.ToggleWriteModeCommand",
                tooltip: "点击切换到修改模型。默认为浏览模型。",
                longDescription: "处于「浏览模型」时，所有写工具返回 WRITE_DISABLED，只有只读工具可用。" +
                                 "切换到「修改模型」后，每个写工具都在独立事务中执行，可在 Revit 中单步撤销。",
                glyph: Glyph.LockClosed, color: IconFactory.Idle);

            Log.Info("Ribbon 面板已创建。");
        }

        /// <summary>
        /// 接入信息做成下拉：两种形式，各自粘贴即可用。
        ///
        /// 本服务是标准 MCP over HTTP，不是某一家客户端的专属插件。
        /// Claude Code 走 CLI 命令，Claude Desktop / Cline / Continue 那一大类读 JSON 配置——
        /// 把两者揉成一段文本，结果是哪边都要用户自己再拼一遍。
        /// </summary>
        private static void AddConnectMenu(RibbonPanel panel, string assemblyPath)
        {
            var data = new PulldownButtonData("RevitMcpConnect", "接入信息")
            {
                ToolTip = "把本 Revit 实例的接入信息复制到剪贴板，供 MCP 客户端使用。",
                LargeImage = IconFactory.Create(Glyph.Copy, IconFactory.Neutral, 32),
                Image = IconFactory.Create(Glyph.Copy, IconFactory.Neutral, 16)
            };

            var pulldown = (PulldownButton)panel.AddItem(data);

            pulldown.AddPushButton(new PushButtonData(
                "RevitMcpCopyConnect", "Claude Code 命令",
                assemblyPath, "RevitMCP.Addin.Commands.CopyConnectCommand")
            {
                ToolTip = "复制 claude mcp add 接入命令（含访问令牌），可直接粘贴到终端执行。",
                Image = IconFactory.Create(Glyph.Copy, IconFactory.Neutral, 16)
            });

            pulldown.AddPushButton(new PushButtonData(
                "RevitMcpCopyJson", "JSON 配置",
                assemblyPath, "RevitMCP.Addin.Commands.CopyJsonConfigCommand")
            {
                ToolTip = "复制通用 MCP 客户端的 mcpServers 配置块（含端点与访问令牌）。",
                Image = IconFactory.Create(Glyph.Document, IconFactory.Neutral, 16)
            });
        }

        private static PushButton AddButton(
            RibbonPanel panel, string assemblyPath, string name, string text, string className,
            string tooltip, Glyph glyph, System.Windows.Media.Color color, string longDescription = null)
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
                    var faulted = host.State == ServerState.Faulted;

                    // 显示状态，不是动作。写「启动服务」而实际正在运行，
                    // 用户读到的是"还没开"，点一下就把正用着的服务关了
                    _toggleButton.ItemText = faulted ? "服务出错"
                                           : host.IsRunning ? "服务运行中"
                                           : "服务已停止";

                    // 状态写在按钮上，点下去会发生什么写在提示里——两者不能混为一谈
                    _toggleButton.ToolTip = (host.IsRunning ? "点击将停止服务。" : "点击将启动服务。") + "\n当前状态：" + host.StatusText;

                    var glyph = faulted ? Glyph.Alert : host.IsRunning ? Glyph.Online : Glyph.Stopped;

                    var color = faulted ? IconFactory.Danger : host.IsRunning ? IconFactory.Active : IconFactory.Idle;

                    _toggleButton.LargeImage = IconFactory.Create(glyph, color, 32);
                    _toggleButton.Image = IconFactory.Create(glyph, color, 16);
                }

                if (_writeModeButton != null)
                {
                    _writeModeButton.ItemText = writeEnabled ? "修改模型" : "浏览模型";
                    _writeModeButton.ToolTip = writeEnabled
                        ? "点击切回浏览模型。切回后所有写工具返回 WRITE_DISABLED。"
                        : "点击切换到修改模型。默认为浏览模型。";

                    var glyph = writeEnabled ? Glyph.LockOpen : Glyph.LockClosed;
                    var color = writeEnabled ? IconFactory.Warning : IconFactory.Idle;

                    _writeModeButton.LargeImage = IconFactory.Create(glyph, color, 32);
                    _writeModeButton.Image = IconFactory.Create(glyph, color, 16);
                }
            }
            catch (Exception ex)
            {
                Log.Error("刷新 Ribbon 状态失败。", ex);   // 界面问题不应影响服务
            }
        }
    }
}
