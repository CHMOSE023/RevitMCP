using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;

namespace RevitMCP.Protocol.Mcp
{
    /// <summary>
    /// 工具用它上报进度。
    ///
    /// 只有客户端在请求里给了 <c>params._meta.progressToken</c> 时才会有真实的接收方——
    /// 规范如此规定，也正好省掉"要不要发进度"这个判断：没人要就不发。
    /// 没给 token 时工具拿到的是 <see cref="NullProgressSink"/>，照常调用即可。
    /// </summary>
    public interface IProgressSink
    {
        /// <summary>
        /// 有没有人在听。拼进度文案本身有成本时先问一句——
        /// 没人听的时候，遍历十万个构件去算百分比纯属浪费主线程。
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// 报一次进度。可从任意线程调用，不阻塞——工具通常在 Revit 主线程上跑，
        /// 让它为了发一条通知去等 socket 写完是不可接受的。
        /// </summary>
        /// <param name="progress">已完成量，必须单调递增。</param>
        /// <param name="total">总量；不知道就传 null，客户端会显示成不确定进度。</param>
        /// <param name="message">给人看的一句话，如"已处理 120 / 500 个构件"。</param>
        void Report(double progress, double? total, string message);
    }

    /// <summary>没有接收方时的空实现。工具不必判断有没有人听。</summary>
    public sealed class NullProgressSink : IProgressSink
    {
        public static readonly NullProgressSink Instance = new NullProgressSink();

        private NullProgressSink() { }

        public bool IsActive => false;

        public void Report(double progress, double? total, string message) { }
    }

    /// <summary>
    /// 把工具线程上的进度上报，转交给 HTTP 线程写成 SSE 事件。
    ///
    /// 两侧线程完全不同：工具在 Revit 主线程上跑，SSE 在处理该请求的 HTTP 线程上写。
    /// 中间这条队列就是这道线程边界——上报永远不阻塞，写不出去也拖不住 Revit。
    /// </summary>
    public sealed class ProgressQueue : IProgressSink, IDisposable
    {
        private readonly ConcurrentQueue<JsonValue> _pending = new ConcurrentQueue<JsonValue>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly JsonValue _token;

        private volatile bool _completed;
        private int _disposed;

        public ProgressQueue(JsonValue progressToken)
        {
            _token = progressToken ?? throw new ArgumentNullException(nameof(progressToken));
        }

        public bool IsActive => !_completed;

        public void Report(double progress, double? total, string message)
        {
            if (_completed) return;

            var parameters = JsonValue.NewObject()
                .Set("progressToken", _token)
                .Set("progress", progress);

            if (total.HasValue) parameters.Set("total", total.Value);
            if (!string.IsNullOrEmpty(message)) parameters.Set("message", message);

            _pending.Enqueue(JsonValue.NewObject()
                .Set("jsonrpc", "2.0")
                .Set("method", "notifications/progress")
                .Set("params", parameters));

            try { _signal.Release(); }
            catch (ObjectDisposedException) { /* 请求已经结束，这条进度没人要了 */ }
            catch (SemaphoreFullException) { }
        }

        /// <summary>
        /// 工具已经返回，不会再有新进度了。
        /// 唤醒等待中的读取方，让它把队列里剩下的写完后收工。
        /// </summary>
        public void Complete()
        {
            if (_completed) return;
            _completed = true;

            try { _signal.Release(); }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }

        /// <summary>
        /// 取下一条待发通知；没有了且已 <see cref="Complete"/> 则返回 null。
        /// 先取队列再看完成标志——否则最后几条进度会在收工时被丢掉。
        /// </summary>
        public async Task<JsonValue> TakeAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                JsonValue item;
                if (_pending.TryDequeue(out item)) return item;
                if (_completed) return null;

                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _completed = true;
            _signal.Dispose();
        }
    }

    /// <summary>从请求里取 <c>params._meta.progressToken</c>。</summary>
    public static class ProgressToken
    {
        /// <summary>
        /// 取出 progressToken；没有则返回 null。
        /// 规范允许它是字符串或数字，所以原样保留 JsonValue——
        /// 回传时必须和客户端发来的完全一致，它靠这个把进度对上是哪次调用。
        /// </summary>
        public static JsonValue From(JsonValue parameters)
        {
            var meta = parameters == null ? null : parameters["_meta"];
            if (meta == null || !meta.IsObject) return null;

            var token = meta["progressToken"];
            if (token == null) return null;

            return token.Kind == JsonKind.String || token.Kind == JsonKind.Number ? token : null;
        }
    }
}
