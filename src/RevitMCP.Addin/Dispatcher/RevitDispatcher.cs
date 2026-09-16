using System;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Diagnostics;
using RevitMCP.Tooling.Dispatch;

namespace RevitMCP.Addin.Dispatcher
{
    /// <summary>
    /// 全框架唯一的线程边界：把 HTTP 线程上的工作编组回 Revit 主线程。
    ///
    /// 这一层刻意做得极薄——排队、超时、放弃语义全在 <see cref="DispatchQueue{TContext}"/> 里，
    /// 那是不依赖 Revit、能在 CI 上跑的部分。这里只负责 ExternalEvent 的接线。
    /// </summary>
    public sealed class RevitDispatcher : IExternalEventHandler, IDispatchSignal, IWorkDispatcher<UIApplication>
    {
        /// <summary>单次 Execute 的时间预算。超出就收工并重新发信号，把主线程还给用户。</summary>
        private static readonly TimeSpan PumpBudget = TimeSpan.FromMilliseconds(200);

        private readonly DispatchQueue<UIApplication> _queue;
        private ExternalEvent _event;

        public RevitDispatcher()
        {
            _queue = new DispatchQueue<UIApplication>(this);
        }

        public bool IsInitialized => _event != null;

        public int PendingCount => _queue.PendingCount;

        /// <summary>
        /// 必须在 <c>IExternalApplication.OnStartup</c> 中调用。
        /// ExternalEvent.Create 只能在 Revit 主线程执行，且只能在插件启动期间创建——
        /// 放到第一次请求时懒加载会直接失败。
        /// </summary>
        public void Initialize()
        {
            if (_event != null) throw new InvalidOperationException("调度器已初始化。");
            _event = ExternalEvent.Create(this);
            Log.Debug("调度器已初始化。");
        }

        /// <summary>提交工作到主线程。可从任意线程调用。</summary>
        public Task<TResult> InvokeAsync<TResult>(
            Func<UIApplication, TResult> work, TimeSpan timeout, CancellationToken cancellationToken) =>
            _queue.EnqueueAsync(work, timeout, cancellationToken);

        void IDispatchSignal.Raise()
        {
            var handle = _event;
            if (handle == null)
                throw new InvalidOperationException("调度器尚未初始化，无法向 Revit 主线程投递工作。");

            // ExternalEvent.Raise 是 Revit 明确保证可从任意线程调用的少数 API 之一。
            // 返回 Pending 表示上一次尚未执行完，属正常情况，不是错误。
            handle.Raise();
        }

        /// <summary>由 Revit 在主线程调用，此时具备有效的 API context。</summary>
        public void Execute(UIApplication app)
        {
            try
            {
                _queue.Pump(app, PumpBudget);
            }
            catch (Exception ex)
            {
                // 从 Execute 里抛异常会被 Revit 当成插件故障，可能直接禁用插件
                Log.Error("调度器执行出错。", ex);
            }
        }

        public string GetName() => "RevitMCP Dispatcher";

        /// <summary>让所有排队中的工作立即失败，避免 Revit 关闭时还有 HTTP 请求在干等。</summary>
        public void Shutdown() => _queue.Shutdown();
    }
}
