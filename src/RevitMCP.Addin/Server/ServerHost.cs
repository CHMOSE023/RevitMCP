using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using RevitMCP.Addin.Compat;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.Diagnostics;
using RevitMCP.Addin.Dispatcher;
using RevitMCP.Addin.Execution;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Tools;
using RevitMCP.Protocol.Json;
using RevitMCP.Protocol.Mcp;
using RevitMCP.Tooling;
using RevitMCP.Transport;
using RevitMCP.Transport.Http;

namespace RevitMCP.Addin.Server
{
    public enum ServerState { Stopped, Starting, Running, Faulted }

    /// <summary>
    /// 服务生命周期：装配 MCP 服务 + HTTP 传输，并维护实例发现文件。
    ///
    /// M4 现状：工具由 [McpTool] 反射注册；非只读工具经 <see cref="RevitWriteScope"/>
    /// 跑在独立事务里，失败预处理与对话框拦截都由它装配。
    /// </summary>
    public sealed class ServerHost
    {
        private readonly McpConfig _config;
        private readonly RevitDispatcher _dispatcher;
        private readonly object _gate = new object();

        private MiniHttpServer _http;

        public ServerHost(McpConfig config, RevitDispatcher dispatcher)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public ServerState State { get; private set; } = ServerState.Stopped;

        /// <summary>
        /// 当前活动文档的标题，写进实例发现文件。
        /// 由 App 在 ViewActivated 时喂进来——ServerHost 够不到 UIApplication，
        /// 而客户端面对多个 Revit 实例时，"哪个开着我要的模型"正是它要问的问题。
        /// </summary>
        public string ActiveDocumentTitle { get; private set; }

        /// <summary>活动文档变了就刷新实例文件；没变则什么都不做（ViewActivated 触发得很频繁）。</summary>
        public void SetActiveDocument(string title)
        {
            if (string.Equals(ActiveDocumentTitle, title, StringComparison.Ordinal)) return;

            ActiveDocumentTitle = title;
            if (IsRunning) WriteInstanceFile();
        }

        /// <summary>实际监听端口（可能因端口占用而不等于配置值）。未启动时为 0。</summary>
        public int Port { get; private set; }

        public event EventHandler StateChanged;

        public bool IsRunning => State == ServerState.Running;

        public string StatusText
        {
            get
            {
                switch (State)
                {
                    case ServerState.Running: return "运行中 · 端口 " + Port;
                    case ServerState.Starting: return "启动中…";
                    case ServerState.Faulted: return "启动失败，见日志";
                    default: return "已停止";
                }
            }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (State == ServerState.Running || State == ServerState.Starting) return;

                SetState(ServerState.Starting);
                try
                {
                    var mcp = new McpServer(
                        new McpServerOptions
                        {
                            ServerName = "RevitMCP",
                            ServerVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(3),
                            Instructions = BuildInstructions()
                        },
                        BuildToolPipeline());

                    var handler = new McpHttpHandler(
                        mcp,
                        new McpHttpOptions
                        {
                            Token = _config.Token,
                            AllowedOrigins = _config.AllowedOrigins
                        },
                        (message, ex) => LogFrom(message, ex));

                    _http = new MiniHttpServer(handler.HandleAsync, (message, ex) => LogFrom(message, ex));
                    Port = _http.Start(_config.Port);

                    WriteInstanceFile();
                    SetState(ServerState.Running);
                    Log.Info("服务已启动：http://127.0.0.1:" + Port + "/mcp");
                }
                catch (Exception ex)
                {
                    Log.Error("服务启动失败。", ex);
                    SafeStopHttp();
                    Port = 0;
                    SetState(ServerState.Faulted);
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (State == ServerState.Stopped) return;

                try
                {
                    SafeStopHttp();
                    DeleteInstanceFile();
                }
                catch (Exception ex)
                {
                    Log.Error("服务停止过程中出错。", ex);
                }
                finally
                {
                    Port = 0;
                    SetState(ServerState.Stopped);
                    Log.Info("服务已停止。");
                }
            }
        }

        private void SafeStopHttp()
        {
            try { _http?.Stop(); }
            catch (Exception ex) { Log.Error("停止 HTTP 监听失败。", ex); }
            finally { _http = null; }
        }

        public void SetWriteEnabled(bool enabled)
        {
            _config.WriteEnabled = enabled;
            try { _config.Save(); } catch (Exception ex) { Log.Error("保存配置失败。", ex); }
            if (IsRunning) WriteInstanceFile();   // 让实例发现文件反映最新状态
            RaiseStateChanged();
            Log.Info("写入模式：" + (enabled ? "已开启" : "已关闭"));
        }

        /// <summary>供「复制接入命令」按钮使用。</summary>
        public string BuildConnectCommand() =>
            "claude mcp add --transport http revit http://127.0.0.1:" + Port + "/mcp" +
            " --header \"Authorization: Bearer " + _config.Token + "\"";

        /// <summary>
        /// 扫描本程序集中所有 [McpTool] 并组装执行管线。
        /// 配置用委托而非快照传入：用户在 Ribbon 上切换写入开关后应立即生效，
        /// 不需要重启服务。
        /// </summary>
        private ToolPipeline<UIApplication> BuildToolPipeline()
        {
            var registry = new ToolRegistry<UIApplication>();
            var count = registry.RegisterAssembly(typeof(ServerHost).Assembly, _config.DisabledTools);
            Log.Info("已注册 " + count + " 个工具：" + string.Join("、", registry.Tools.Select(t => t.Name).ToArray()));

            return new ToolPipeline<UIApplication>(registry, _dispatcher, new ToolPipelineOptions
            {
                WriteEnabled = () => _config.WriteEnabled,
                MaxElementsPerWrite = () => _config.MaxElementsPerWrite,
                DefaultTimeoutSeconds = () => _config.DefaultToolTimeoutSeconds,
                Log = LogFrom,
                Audit = entry => Log.Audit(entry.ToString())
            },
            // 写作用域只作用于非只读工具：开事务、装失败预处理、拦模态框（M4）
            new RevitWriteScope());
        }

        private string BuildInstructions() =>
            "操作当前在 Revit " + RevitVersionInfo.Year + " 中打开的模型。" +
            "写操作默认被禁用，需用户在 Revit 的 RevitMCP 面板上手动开启。";

        private static void LogFrom(string message, Exception ex)
        {
            if (ex != null) Log.Error(message, ex);
            else Log.Debug(message);
        }

        // ---------- 实例发现 ----------
        // 一台机器可能同时开多个 Revit，客户端需要知道该连哪个端口。

        public static string InstanceDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitMCP", "instances");

        private static string InstanceFilePath =>
            Path.Combine(InstanceDirectory, "revit-" + Process.GetCurrentProcess().Id + ".json");

        private void WriteInstanceFile()
        {
            try
            {
                Directory.CreateDirectory(InstanceDirectory);
                var json = JsonValue.NewObject()
                    .Set("pid", Process.GetCurrentProcess().Id)
                    .Set("port", Port)
                    .Set("endpoint", "http://127.0.0.1:" + Port + "/mcp")
                    .Set("revitVersion", RevitVersionInfo.Year)
                    .Set("activeDocument", ActiveDocumentTitle)
                    .Set("writeEnabled", _config.WriteEnabled)
                    .Set("startedAt", DateTime.Now.ToString("o"))
                    .ToJson(indented: true);
                File.WriteAllText(InstanceFilePath, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Error("写入实例发现文件失败。", ex);   // 非致命
            }
        }

        private void DeleteInstanceFile()
        {
            try { if (File.Exists(InstanceFilePath)) File.Delete(InstanceFilePath); }
            catch (Exception ex) { Log.Error("删除实例发现文件失败。", ex); }
        }

        /// <summary>清理进程已不存在的残留实例文件（Revit 崩溃时会留下）。</summary>
        public static void PruneStaleInstanceFiles()
        {
            try
            {
                if (!Directory.Exists(InstanceDirectory)) return;
                foreach (var file in Directory.GetFiles(InstanceDirectory, "revit-*.json"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var pidText = name.Substring("revit-".Length);
                    if (!int.TryParse(pidText, out var pid)) continue;

                    try { Process.GetProcessById(pid); }
                    catch (ArgumentException)
                    {
                        // 进程已退出
                        try { File.Delete(file); Log.Debug("清理残留实例文件：" + file); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("清理残留实例文件失败。", ex);
            }
        }

        private void SetState(ServerState state)
        {
            State = state;
            RaiseStateChanged();
        }

        private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
