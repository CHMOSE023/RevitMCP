using System.Globalization;
using RevitMCP.Protocol.Mcp;

namespace RevitMCP.Tooling
{
    /// <summary>
    /// 批量循环里报进度的惯用法。
    ///
    /// 节流是必须的：进度是给人看的，一秒钟推几百条通知既没用，
    /// 又把 Revit 主线程的时间花在了拼字符串和入队上——
    /// 而主线程正是这类批量操作最稀缺的资源。
    /// </summary>
    public static class ProgressTicker
    {
        /// <summary>每多少个报一次。最后一个总是报，好让进度条走满。</summary>
        public const int Interval = 25;

        public static void Tick(IProgressSink sink, int done, int total, string verb)
        {
            if (sink == null || !sink.IsActive) return;
            if (done % Interval != 0 && done != total) return;

            sink.Report(done, total,
                verb + " " +
                done.ToString(CultureInfo.InvariantCulture) + " / " +
                total.ToString(CultureInfo.InvariantCulture) + " 个构件");
        }
    }
}
