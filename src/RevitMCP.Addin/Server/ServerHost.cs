using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using RevitMCP.Addin.Compat;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.Diagnostics;
using RevitMCP.Protocol.Json;

namespace RevitMCP.Addin.Server
{
    public enum ServerState { Stopped, Starting, Running, Faulted }

    /// <summary>
    /// 服务生命周期与实例发现文件的管理者。
    ///
    /// M0 现状：状态机、实例发现文件、Ribbon 联动均已可用，但**尚未监听端口**——
    /// 真正的 TcpListener / HTTP / JSON-RPC 在 M1 接入（见下方 TODO(M1)）。
    /// 状态文案刻意写明这一点，避免界面显示 "运行中" 而实际不接受连接。
    /// </summary>
    public sealed class ServerHost
    {
        private readonly McpConfig _config;
        private readonly object _gate = new object();

        public ServerHost(McpConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public ServerState State { get; private set; } = ServerState.Stopped;

        /// <summary>实际监听端口。未启动时为 0。</summary>
        public int Port { get; private set; }

        public event EventHandler StateChanged;

        public bool IsRunning => State == ServerState.Running;

        public string StatusText
        {
            get
            {
                switch (State)
                {
                    case ServerState.Running:
                        // TODO(M1): 传输层接入后改为 "运行中 · 端口 {Port}"
                        return "已启用 · 端口 " + Port + "（传输层待 M1）";
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
                    Port = _config.Port;

                    // TODO(M1): 在此启动 MiniHttpServer，并把实际绑定到的端口写回 Port
                    //           （端口被占用时从 config.Port 起向上探测）。

                    WriteInstanceFile();
                    SetState(ServerState.Running);
                    Log.Info("服务已启用，端口 " + Port);
                }
                catch (Exception ex)
                {
                    Log.Error("服务启动失败。", ex);
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
                    // TODO(M1): 停止监听、断开所有 SSE 会话
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

        public void SetWriteEnabled(bool enabled)
        {
            _config.WriteEnabled = enabled;
            try { _config.Save(); } catch (Exception ex) { Log.Error("保存配置失败。", ex); }
            if (IsRunning) WriteInstanceFile();   // 让实例发现文件反映最新状态
            RaiseStateChanged();
            Log.Info("写入模式：" + (enabled ? "已开启" : "已关闭"));
        }

        /// <summary>供 "复制接入命令" 按钮使用。</summary>
        public string BuildConnectCommand() =>
            "claude mcp add --transport http revit http://127.0.0.1:" + Port + "/mcp" +
            " --header \"Authorization: Bearer " + _config.Token + "\"";

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
                    .Set("revitVersion", RevitVersionInfo.Year)
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
