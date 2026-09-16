using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace RevitMCP.Addin.Diagnostics
{
    public enum LogLevel { Debug = 0, Information = 1, Warning = 2, Error = 3 }

    /// <summary>
    /// 文件日志。每个 Revit 进程一个文件，避免多实例互相覆盖。
    /// 日志写入绝不能抛异常——它经常在 catch 块里被调用，再抛就把原始错误吃掉了。
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static bool _initialized;

        public static LogLevel MinimumLevel { get; set; } = LogLevel.Information;

        public static string FilePath => _path;

        public static void Initialize()
        {
            lock (Gate)
            {
                if (_initialized) return;
                try
                {
                    var dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "RevitMCP", "logs");
                    Directory.CreateDirectory(dir);
                    _path = Path.Combine(dir, "revit-" + Process.GetCurrentProcess().Id + ".log");

                    PruneOldLogs(dir);
                }
                catch
                {
                    _path = null;   // 日志不可用不影响插件功能
                }
                _initialized = true;
            }
        }

        public static void Debug(string message) => Write(LogLevel.Debug, message, null);
        public static void Info(string message) => Write(LogLevel.Information, message, null);
        public static void Warn(string message) => Write(LogLevel.Warning, message, null);
        public static void Error(string message, Exception ex = null) => Write(LogLevel.Error, message, ex);

        /// <summary>
        /// 一条工具调用审计。
        ///
        /// 刻意绕过 <see cref="MinimumLevel"/>：审计是安全措施，
        /// 不该因为用户把日志级别调高就悄悄消失——那恰恰是最需要它的时候。
        /// </summary>
        public static void Audit(string message) => Emit("AUDIT", message, null);

        private static void Write(LogLevel level, string message, Exception ex)
        {
            if (level < MinimumLevel) return;
            Emit(level.ToString().ToUpperInvariant(), message, ex);
        }

        private static void Emit(string tag, string message, Exception ex)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                  .Append(" [").Append(tag).Append("] ")
                  .Append(message);
                if (ex != null) sb.AppendLine().Append(ex);

                var line = sb.ToString();
                System.Diagnostics.Debug.WriteLine("[RevitMCP] " + line);

                lock (Gate)
                {
                    if (_path == null) return;
                    File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // 刻意吞掉：日志失败不能掩盖调用方正在处理的真实异常
            }
        }

        /// <summary>保留最近 20 个日志文件，防止长期使用后目录膨胀。</summary>
        private static void PruneOldLogs(string dir)
        {
            try
            {
                var files = new DirectoryInfo(dir).GetFiles("revit-*.log");
                if (files.Length <= 20) return;
                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (var i = 20; i < files.Length; i++)
                {
                    try { files[i].Delete(); } catch { /* 文件可能被其他 Revit 实例占用 */ }
                }
            }
            catch { }
        }
    }
}
