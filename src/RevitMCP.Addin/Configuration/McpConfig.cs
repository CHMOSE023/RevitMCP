using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RevitMCP.Addin.Diagnostics;
using RevitMCP.Protocol.Json;

namespace RevitMCP.Addin.Configuration
{
    /// <summary>
    /// %APPDATA%\RevitMCP\config.json
    ///
    /// 读取容错：任何单项缺失或类型不对都回退到默认值，不让一个坏字段导致插件起不来。
    /// 文件损坏时整体回退到默认配置，并把坏文件改名保留，方便排查。
    /// </summary>
    public sealed class McpConfig
    {
        public int Port { get; set; } = 7801;
        public bool AutoStart { get; set; } = true;
        public string Token { get; set; }
        public bool WriteEnabled { get; set; } = false;

        /// <summary>
        /// 逃生舱（revit_invoke_api / revit_execute_script）的总闸。
        ///
        /// 它们等于允许调用方在 Revit 进程里执行任意代码——
        /// 能读写任何文件、发任何网络请求，远超出「改模型」的范围。
        /// 所以它不跟着 <see cref="WriteEnabled"/> 走，而是单独一道闸，
        /// 且只能改配置文件——Ribbon 上不提供一键开启，
        /// 开启它应当是一个需要停下来想一想的动作。
        /// </summary>
        public bool EscapeHatchEnabled { get; set; } = false;
        /// <summary>
        /// 认不出来的 Revit 模态对话框，要不要也自动点「确定」。
        ///
        /// 默认 false：不认识的对话框一律取消，让这一步失败、事务回滚，
        /// 并在回执里说清楚需要人来决定。数字上的"第一个按钮"在不同对话框上
        /// 分别意味着确定、是、删除、覆盖——替用户点下去，等于把未知后果写进他的模型。
        ///
        /// 如果某个建模流程因此走不通（某个必经的对话框其实是安全的），
        /// 可以临时打开它，同时把那个对话框的 DialogId 反馈进白名单，
        /// 而不是长期让所有对话框都被自动确认。
        /// </summary>
        public bool AutoConfirmUnknownDialogs { get; set; } = false;

        public int MaxElementsPerWrite { get; set; } = 500;
        public int DefaultToolTimeoutSeconds { get; set; } = 60;
        public List<string> DisabledTools { get; set; } = new List<string>();
        public List<string> AllowedOrigins { get; set; } = new List<string>
        {
            "http://localhost", "http://127.0.0.1", "https://claude.ai"
        };
        public LogLevel LogLevel { get; set; } = LogLevel.Information;

        /// <summary>
        /// 导出文件的落脚点。留空则用 <see cref="DefaultExportDirectory"/>。
        /// 工具只接受文件名，一律落在这个目录下——理由见 <c>ExportPaths</c>。
        /// </summary>
        public string ExportDirectory { get; set; }

        /// <summary>导出目录的实际取值。</summary>
        public string ResolvedExportDirectory =>
            string.IsNullOrWhiteSpace(ExportDirectory) ? DefaultExportDirectory : ExportDirectory;

        public static string DefaultExportDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitMCP", "exports");

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitMCP", "config.json");

        public static McpConfig Load(string path = null)
        {
            path = path ?? DefaultPath;
            var config = new McpConfig();

            try
            {
                if (!File.Exists(path))
                {
                    config.Token = GenerateToken();
                    config.Save(path);
                    Log.Info("已生成默认配置：" + path);
                    return config;
                }

                var root = JsonValue.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (!root.IsObject) throw new JsonException("配置根节点必须是对象。");

                config.Port = ReadInt(root, "port", config.Port);
                config.AutoStart = ReadBool(root, "autoStart", config.AutoStart);
                config.Token = ReadString(root, "token", null);
                config.WriteEnabled = ReadBool(root, "writeEnabled", config.WriteEnabled);
                config.EscapeHatchEnabled = ReadBool(root, "escapeHatchEnabled", config.EscapeHatchEnabled);
                config.AutoConfirmUnknownDialogs = ReadBool(root, "autoConfirmUnknownDialogs", config.AutoConfirmUnknownDialogs);
                config.MaxElementsPerWrite = ReadInt(root, "maxElementsPerWrite", config.MaxElementsPerWrite);
                config.DefaultToolTimeoutSeconds = ReadInt(root, "defaultToolTimeoutSeconds", config.DefaultToolTimeoutSeconds);
                config.DisabledTools = ReadStringList(root, "disabledTools", config.DisabledTools);
                config.AllowedOrigins = ReadStringList(root, "allowedOrigins", config.AllowedOrigins);
                config.LogLevel = ReadEnum(root, "logLevel", config.LogLevel);
                config.ExportDirectory = ReadString(root, "exportDirectory", null);

                if (string.IsNullOrEmpty(config.Token))
                {
                    config.Token = GenerateToken();
                    config.Save(path);
                    Log.Info("配置中缺少 token，已重新生成。");
                }

                return config;
            }
            catch (Exception ex)
            {
                Log.Error("读取配置失败，回退到默认配置：" + path, ex);
                QuarantineBadFile(path);
                var fallback = new McpConfig { Token = GenerateToken() };
                try { fallback.Save(path); } catch { }
                return fallback;
            }
        }

        public void Save(string path = null)
        {
            path = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var root = JsonValue.NewObject()
                .Set("port", Port)
                .Set("autoStart", AutoStart)
                .Set("token", Token ?? string.Empty)
                .Set("writeEnabled", WriteEnabled)
                .Set("escapeHatchEnabled", EscapeHatchEnabled)
                .Set("autoConfirmUnknownDialogs", AutoConfirmUnknownDialogs)
                .Set("maxElementsPerWrite", MaxElementsPerWrite)
                .Set("defaultToolTimeoutSeconds", DefaultToolTimeoutSeconds)
                .Set("disabledTools", ToArray(DisabledTools))
                .Set("allowedOrigins", ToArray(AllowedOrigins))
                .Set("logLevel", LogLevel.ToString())
                .Set("exportDirectory", ExportDirectory ?? string.Empty);

            // 先写临时文件再替换，避免写一半崩溃留下半截文件
            var temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJson(indented: true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static JsonValue ToArray(IEnumerable<string> values)
        {
            var arr = JsonValue.NewArray();
            if (values != null)
                foreach (var v in values) arr.Add(v);
            return arr;
        }

        private static string GenerateToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            // URL-safe base64，方便直接放进命令行和 HTTP 头
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static void QuarantineBadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var backup = path + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Move(path, backup);
                Log.Warn("损坏的配置已保留为：" + backup);
            }
            catch { }
        }

        // ---------- 容错读取 ----------
        private static int ReadInt(JsonValue root, string key, int fallback)
        {
            try { return root.TryGet(key, out var v) && v.Kind == JsonKind.Number ? (int)v.AsInt64 : fallback; }
            catch { return fallback; }
        }

        private static bool ReadBool(JsonValue root, string key, bool fallback)
        {
            try { return root.TryGet(key, out var v) && v.Kind == JsonKind.Bool ? v.AsBool : fallback; }
            catch { return fallback; }
        }

        private static string ReadString(JsonValue root, string key, string fallback)
        {
            try { return root.TryGet(key, out var v) && v.Kind == JsonKind.String ? v.AsString : fallback; }
            catch { return fallback; }
        }

        private static List<string> ReadStringList(JsonValue root, string key, List<string> fallback)
        {
            try
            {
                if (!root.TryGet(key, out var v) || !v.IsArray) return fallback;
                var list = new List<string>();
                foreach (var item in v.Items)
                    if (item.Kind == JsonKind.String) list.Add(item.AsString);
                return list;
            }
            catch { return fallback; }
        }

        private static LogLevel ReadEnum(JsonValue root, string key, LogLevel fallback)
        {
            try
            {
                var text = ReadString(root, key, null);
                if (string.IsNullOrEmpty(text)) return fallback;
                return (LogLevel)Enum.Parse(typeof(LogLevel), text, ignoreCase: true);
            }
            catch { return fallback; }
        }
    }
}
