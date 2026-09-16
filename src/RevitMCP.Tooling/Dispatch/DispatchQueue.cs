using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RevitMCP.Tooling.Dispatch
{
    /// <summary>唤醒主线程的信号。Revit 侧实现为 ExternalEvent.Raise()。</summary>
    public interface IDispatchSignal
    {
        void Raise();
    }

    /// <summary>
    /// 把工作编组到主线程执行。执行管线只依赖这个接口，
    /// 因此测试里可以换成同步实现，插件里则是 ExternalEvent 那套。
    /// </summary>
    public interface IWorkDispatcher<TContext>
    {
        Task<TResult> InvokeAsync<TResult>(
            Func<TContext, TResult> work, TimeSpan timeout, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 把后台线程提交的工作编组到单一"泵"线程上执行。
    ///
    /// 刻意不认识 Revit：<typeparamref name="TContext"/> 在插件里是 UIApplication，
    /// 在测试里是任意假对象。整个超时/放弃/竞态语义因此能脱离 Revit 验证——
    /// 而这正是全框架最容易出隐蔽错误的地方。
    ///
    /// 线程模型：<see cref="EnqueueAsync"/> 跑在 HTTP 线程池线程，
    /// <see cref="Pump"/> 只在主线程（Revit 的 API context 内）调用。
    /// </summary>
    public sealed class DispatchQueue<TContext> : IWorkDispatcher<TContext>
    {
        /// <summary>
        /// 工作已在主线程跑起来、但等待超时后，额外再等多久。
        /// 这段等待不是为了拿结果，而是为了分清"没执行"和"已执行但没等到"——
        /// 两者对调用方的含义完全不同。
        /// </summary>
        public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(10);

        private readonly ConcurrentQueue<IWorkItem<TContext>> _queue = new ConcurrentQueue<IWorkItem<TContext>>();
        private readonly IDispatchSignal _signal;
        private int _shutdown;

        public DispatchQueue(IDispatchSignal signal)
        {
            _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        }

        public int PendingCount => _queue.Count;

        public bool IsShutDown => Volatile.Read(ref _shutdown) != 0;

        /// <summary>
        /// 提交一份工作到主线程执行并等待结果。
        ///
        /// 超时分两种，绝不能混：
        /// · 工作还没开始就超时 → <see cref="RevitBusyException"/>，可以确定模型未被触碰；
        /// · 工作已经开始但没等到 → <see cref="DispatchTimeoutException"/>，模型**可能已被修改**。
        /// </summary>
        public async Task<TResult> EnqueueAsync<TResult>(
            Func<TContext, TResult> work, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            if (IsShutDown) throw new DispatchStoppedException();

            var item = new WorkItem<TContext, TResult>(work);
            _queue.Enqueue(item);

            try
            {
                _signal.Raise();
            }
            catch (Exception ex)
            {
                // 连信号都发不出去（调度器未初始化等），立刻放弃，不要让调用方干等到超时
                item.TryAbandon();
                throw new DispatchStoppedException("无法向主线程投递工作。", ex);
            }

            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var delay = Task.Delay(timeout, timer.Token);
                var finished = await Task.WhenAny(item.Completion, delay).ConfigureAwait(false);

                if (finished == item.Completion)
                {
                    timer.Cancel();   // 取消后的 Task.Delay 是 Canceled 而非 Faulted，无需再观察
                    return await item.Completion.ConfigureAwait(false);
                }
            }

            // 到这里说明等待被超时或取消打断。
            // 能抢到"放弃"就说明它还没开始跑——这是唯一可以断言"什么都没发生"的分支。
            if (item.TryAbandon())
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new RevitBusyException(timeout);
            }

            // 抢不到 = 主线程已经在执行它了。此时谎称 REVIT_BUSY 会让调用方以为模型没变，
            // 那是比超时本身严重得多的错误。宽限一小段，仍拿不到就如实报"已执行但未等到"。
            var graced = await Task.WhenAny(item.Completion, Task.Delay(GracePeriod)).ConfigureAwait(false);
            if (graced == item.Completion) return await item.Completion.ConfigureAwait(false);

            throw new DispatchTimeoutException(timeout);
        }

        Task<TResult> IWorkDispatcher<TContext>.InvokeAsync<TResult>(
            Func<TContext, TResult> work, TimeSpan timeout, CancellationToken cancellationToken) =>
            EnqueueAsync(work, timeout, cancellationToken);

        /// <summary>
        /// 在主线程上排空队列，受时间预算约束。返回实际执行的条数。
        ///
        /// 预算的意义：一批长任务会把 Revit 界面冻住。每次至少执行一条以保证进度，
        /// 超预算就收工并重新发信号，把主线程还给用户。
        /// </summary>
        public int Pump(TContext context, TimeSpan budget)
        {
            var clock = Stopwatch.StartNew();
            var executed = 0;

            while (_queue.TryDequeue(out var item))
            {
                // 抢不到的都是已被超时方放弃的工作——跳过它们正是本类存在的理由
                if (!item.TryClaimForExecution()) continue;

                item.Execute(context);
                executed++;

                if (clock.Elapsed >= budget) break;
            }

            if (!_queue.IsEmpty && !IsShutDown)
            {
                try { _signal.Raise(); }
                catch { /* 停止过程中信号源可能已失效，下次 Enqueue 会重新唤醒 */ }
            }

            return executed;
        }

        /// <summary>
        /// 停止调度并让所有排队中的工作立即失败。
        /// 不这么做的话，Revit 关闭时还挂着的 HTTP 请求会一直等到自己的超时。
        /// </summary>
        public void Shutdown()
        {
            Interlocked.Exchange(ref _shutdown, 1);

            while (_queue.TryDequeue(out var item))
                item.FailIfPending(new DispatchStoppedException());
        }

        // ---------- 工作项 ----------

        private interface IWorkItem<in T>
        {
            bool TryClaimForExecution();
            bool TryAbandon();
            void Execute(T context);
            void FailIfPending(Exception reason);
        }

        private sealed class WorkItem<T, TResult> : IWorkItem<T>
        {
            private const int Pending = 0;
            private const int Running = 1;
            private const int Abandoned = 2;

            private readonly Func<T, TResult> _work;
            private readonly TaskCompletionSource<TResult> _completion;
            private int _state = Pending;

            public WorkItem(Func<T, TResult> work)
            {
                _work = work;
                // RunContinuationsAsynchronously 是必须的：否则等待方（HTTP 线程）的后续代码
                // 会在 TrySetResult 处内联执行，也就是跑在 Revit 主线程上，直接冻界面。
                _completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public Task<TResult> Completion => _completion.Task;

            public bool TryClaimForExecution() =>
                Interlocked.CompareExchange(ref _state, Running, Pending) == Pending;

            public bool TryAbandon() =>
                Interlocked.CompareExchange(ref _state, Abandoned, Pending) == Pending;

            public void Execute(T context)
            {
                try { _completion.TrySetResult(_work(context)); }
                catch (OperationCanceledException ex) { _completion.TrySetCanceled(ex.CancellationToken); }
                catch (Exception ex) { _completion.TrySetException(ex); }
            }

            public void FailIfPending(Exception reason)
            {
                if (TryAbandon()) _completion.TrySetException(reason);
            }
        }
    }

    /// <summary>主线程在超时内始终没空——通常是有模态对话框打开，或正在长时间运算。</summary>
    public sealed class RevitBusyException : Exception
    {
        public RevitBusyException(TimeSpan timeout)
            : base("Revit 未在 " + (int)timeout.TotalSeconds + " 秒内进入空闲状态，工作尚未开始执行。" +
                   "通常是有模态对话框打开，或正在进行长时间运算。")
        {
            Timeout = timeout;
        }

        public TimeSpan Timeout { get; }
    }

    /// <summary>工作已经在主线程上执行，但没能在超时内拿到结果。模型可能已被修改。</summary>
    public sealed class DispatchTimeoutException : Exception
    {
        public DispatchTimeoutException(TimeSpan timeout)
            : base("操作已在 Revit 中开始执行，但超过 " + (int)timeout.TotalSeconds + " 秒仍未完成。" +
                   "模型可能已被部分修改，请在 Revit 中确认后再重试。")
        {
            Timeout = timeout;
        }

        public TimeSpan Timeout { get; }
    }

    /// <summary>调度器已停止或不可用。</summary>
    public sealed class DispatchStoppedException : Exception
    {
        public DispatchStoppedException() : base("RevitMCP 调度器已停止。") { }

        public DispatchStoppedException(string message, Exception inner) : base(message, inner) { }
    }
}
