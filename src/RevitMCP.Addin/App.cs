using System;
using System.Reflection;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Compat;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.Diagnostics;
using RevitMCP.Addin.Dispatcher;
using RevitMCP.Addin.Ribbon;
using RevitMCP.Addin.Server;

namespace RevitMCP.Addin
{
    /// <summary>
    /// 插件入口。
    ///
    /// OnStartup 里任何未捕获的异常都会让 Revit 弹出"插件加载失败"并禁用本插件，
    /// 因此整个方法体包在 try/catch 中：宁可降级运行，也不要被 Revit 拉黑。
    /// </summary>
    public sealed class App : IExternalApplication
    {
        public static App Current { get; private set; }

        public McpConfig Config { get; private set; }
        public ServerHost Server { get; private set; }
        public RevitDispatcher Dispatcher { get; private set; }

        private RibbonController _ribbon;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                Current = this;

                Log.Initialize();
                Log.Info(string.Format(
                    "RevitMCP {0} 启动中 · 目标 Revit {1} ({2}) · 宿主 Revit {3}",
                    Assembly.GetExecutingAssembly().GetName().Version,
                    RevitVersionInfo.Year,
                    RevitVersionInfo.TargetFramework,
                    application.ControlledApplication.VersionNumber));

                WarnOnVersionMismatch(application);

                Config = McpConfig.Load();
                Log.MinimumLevel = Config.LogLevel;

                ServerHost.PruneStaleInstanceFiles();

                // ExternalEvent.Create 只能在 Revit 主线程、且仅限 OnStartup 期间执行，
                // 放到第一次请求时懒加载会失败——这行的位置是有约束的，不要移动。
                Dispatcher = new RevitDispatcher();
                Dispatcher.Initialize();

                Server = new ServerHost(Config, Dispatcher);
                Server.StateChanged += (s, e) => _ribbon?.Refresh(Server, Config.WriteEnabled);

                _ribbon = RibbonController.Build(application);
                _ribbon.Refresh(Server, Config.WriteEnabled);

                if (Config.AutoStart)
                {
                    try
                    {
                        Server.Start();
                    }
                    catch (Exception ex)
                    {
                        // 自动启动失败不应阻断插件加载，用户还能从 Ribbon 手动重试
                        Log.Error("自动启动失败，请从 Ribbon 手动启动。", ex);
                    }
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error("OnStartup 失败。", ex);
                try
                {
                    TaskDialog.Show("RevitMCP",
                        "插件启动失败：" + ex.Message + "\n\n详见日志：" + (Log.FilePath ?? "(日志不可用)"));
                }
                catch { }
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                Server?.Stop();
                // 先停服务再停调度器：反过来的话，正在处理中的请求会拿到 SERVER_STOPPED 而不是正常结果
                Dispatcher?.Shutdown();
                Log.Info("RevitMCP 已关闭。");
            }
            catch (Exception ex)
            {
                Log.Error("OnShutdown 出错。", ex);
            }
            finally
            {
                Current = null;
            }
            return Result.Succeeded;
        }

        public void RefreshRibbon() => _ribbon?.Refresh(Server, Config.WriteEnabled);

        /// <summary>
        /// 构建目标版本与实际宿主不一致时告警。
        /// 典型场景：把 R21 的产物装进了 Revit 2024 的 Addins 目录——
        /// 多数 API 仍能跑，但 ElementId 之类的差异会以诡异的方式炸掉，提前记一笔能省很多排查时间。
        /// </summary>
        private static void WarnOnVersionMismatch(UIControlledApplication application)
        {
            try
            {
                var host = application.ControlledApplication.VersionNumber;
                if (int.TryParse(host, out var hostYear) && hostYear != RevitVersionInfo.Year)
                {
                    Log.Warn(string.Format(
                        "版本不匹配：本插件针对 Revit {0} 构建，当前宿主为 Revit {1}。" +
                        "请安装对应版本的产物（artifacts\\{1}\\）。",
                        RevitVersionInfo.Year, hostYear));
                }
            }
            catch (Exception ex)
            {
                Log.Error("检查宿主版本失败。", ex);
            }
        }
    }
}
