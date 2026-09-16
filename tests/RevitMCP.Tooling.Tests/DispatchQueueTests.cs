using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Tooling.Dispatch;
using Xunit;

namespace RevitMCP.Tooling.Tests
{
    /// <summary>测试用的"主线程"上下文，对应插件里的 UIApplication。</summary>
    internal sealed class FakeContext
    {
        public int PumpThreadId { get; set; }
    }

    /// <summary>手动驱动：记录 Raise 次数，由测试自己决定何时 Pump。</summary>
    internal sealed class ManualSignal : IDispatchSignal
    {
        private int _raiseCount;

        public int RaiseCount => Volatile.Read(ref _raiseCount);

        public void Raise() => Interlocked.Increment(ref _raiseCount);
    }

    /// <summary>信号直接抛异常，模拟调度器未初始化。</summary>
    internal sealed class BrokenSignal : IDispatchSignal
    {
        public void Raise() => throw new InvalidOperationException("调度器尚未初始化。");
    }

    /// <summary>
    /// 后台泵：收到信号就在一个固定的"主线程"上排空队列，
    /// 模拟 Revit 空闲时回调 ExternalEvent 的行为。
    /// </summary>
    internal sealed class BackgroundPump : IDispatchSignal, IDisposable
    {
        private readonly DispatchQueue<FakeContext> _queue;
        private readonly FakeContext _context = new FakeContext();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _thread;
        private volatile bool _stop;

        /// <summary>设为 true 可模拟"模态对话框打开"——主线程收到信号也不干活。</summary>
        public volatile bool Blocked;

        public BackgroundPump(Func<IDispatchSignal, DispatchQueue<FakeContext>> factory)
        {
            _queue = factory(this);
            _thread = new Thread(Loop) { IsBackground = true, Name = "FakeRevitMainThread" };
            _thread.Start();
        }

        public DispatchQueue<FakeContext> Queue => _queue;

        public int MainThreadId => _thread.ManagedThreadId;

        public void Raise() => _wake.Set();

        private void Loop()
        {
            _context.PumpThreadId = Thread.CurrentThread.ManagedThreadId;
            while (!_stop)
            {
                if (!_wake.WaitOne(20)) continue;
                if (Blocked) continue;   // 模态框打开：信号收到了，但主线程进不了 API context
                _queue.Pump(_context, TimeSpan.FromMilliseconds(200));
            }
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            _thread.Join(TimeSpan.FromSeconds(2));
            _wake.Dispose();
        }
    }

    public class DispatchQueueBasicTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

        [Fact]
        public async Task WorkRunsOnPumpThreadAndReturnsValue()
        {
            using (var pump = new BackgroundPump(signal => new DispatchQueue<FakeContext>(signal)))
            {
                var threadId = await pump.Queue.EnqueueAsync(
                    ctx => Thread.CurrentThread.ManagedThreadId, LongTimeout, CancellationToken.None);

                // 工作必须在"主线程"上执行，而不是调用方的线程池线程
                Assert.Equal(pump.MainThreadId, threadId);
                Assert.NotEqual(Thread.CurrentThread.ManagedThreadId, threadId);
            }
        }

        [Fact]
        public async Task ExceptionFromWorkPropagatesToCaller()
        {
            using (var pump = new BackgroundPump(signal => new DispatchQueue<FakeContext>(signal)))
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    pump.Queue.EnqueueAsync<int>(
                        ctx => throw new InvalidOperationException("模型里没有这个构件"),
                        LongTimeout, CancellationToken.None));

                Assert.Equal("模型里没有这个构件", ex.Message);
            }
        }

        [Fact]
        public async Task ContinuationDoesNotRunOnThePumpThread()
        {
            // 若 TaskCompletionSource 没有用 RunContinuationsAsynchronously，
            // 等待方的后续代码会内联跑在 Revit 主线程上，直接冻界面
            using (var pump = new BackgroundPump(signal => new DispatchQueue<FakeContext>(signal)))
            {
                await pump.Queue.EnqueueAsync(ctx => 1, LongTimeout, CancellationToken.None);
                Assert.NotEqual(pump.MainThreadId, Thread.CurrentThread.ManagedThreadId);
            }
        }

        [Fact]
        public void EnqueueRaisesTheSignal()
        {
            var signal = new ManualSignal();
            var queue = new DispatchQueue<FakeContext>(signal);

            var _ = queue.EnqueueAsync(ctx => 1, LongTimeout, CancellationToken.None);

            Assert.Equal(1, signal.RaiseCount);
            Assert.Equal(1, queue.PendingCount);
        }

        [Fact]
        public async Task ItemsRunInFifoOrder()
        {
            var signal = new ManualSignal();
            var queue = new DispatchQueue<FakeContext>(signal);
            var order = new ConcurrentQueue<int>();

            var tasks = Enumerable.Range(0, 10)
                .Select(i => queue.EnqueueAsync(ctx => { order.Enqueue(i); return i; }, LongTimeout, CancellationToken.None))
                .ToArray();

            queue.Pump(new FakeContext(), TimeSpan.FromSeconds(5));
            await Task.WhenAll(tasks);

            Assert.Equal(Enumerable.Range(0, 10), order);
        }

        [Fact]
        public void PumpStopsAtBudgetAndReRaisesForTheRest()
        {
            var signal = new ManualSignal();
            var queue = new DispatchQueue<FakeContext>(signal);

            for (var i = 0; i < 5; i++)
                _ = queue.EnqueueAsync(ctx => { Thread.Sleep(60); return 0; }, LongTimeout, CancellationToken.None);

            var raisesBefore = signal.RaiseCount;
            var executed = queue.Pump(new FakeContext(), TimeSpan.FromMilliseconds(100));

            // 预算 100ms、每条 60ms：执行两条后超预算收工，绝不能一口气跑完把界面冻住
            Assert.InRange(executed, 1, 3);
            Assert.True(queue.PendingCount > 0);

            // 队列没空就必须重新发信号，否则剩下的工作永远等不到下一次 Pump
            Assert.True(signal.RaiseCount > raisesBefore);
        }

        [Fact]
        public void PumpExecutesAtLeastOneItemEvenWithZeroBudget()
        {
            var signal = new ManualSignal();
            var queue = new DispatchQueue<FakeContext>(signal);
            _ = queue.EnqueueAsync(ctx => 1, LongTimeout, CancellationToken.None);

            // 预算再紧也要保证进度，否则队列会饿死
            Assert.Equal(1, queue.Pump(new FakeContext(), TimeSpan.FromTicks(1)));
        }

        [Fact]
        public async Task ConcurrentEnqueuesAllComplete()
        {
            using (var pump = new BackgroundPump(signal => new DispatchQueue<FakeContext>(signal)))
            {
                var tasks = Enumerable.Range(0, 200)
                    .Select(i => Task.Run(() => pump.Queue.EnqueueAsync(ctx => i * 2, LongTimeout, CancellationToken.None)))
                    .ToArray();

                var results = await Task.WhenAll(tasks);
                Assert.Equal(Enumerable.Range(0, 200).Select(i => i * 2).OrderBy(x => x), results.OrderBy(x => x));
            }
        }
    }

    /// <summary>超时语义——设计文档点名的"最隐蔽的正确性问题"。</summary>
    public class DispatchQueueTimeoutTests
    {
        private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

        [Fact]
        public async Task BlockedMainThreadYieldsRevitBusy()
        {
            // 对应冒烟项 6：Revit 里开着模态对话框时调用工具
            using (var pump = new BackgroundPump(signal => new DispatchQueue<FakeContext>(signal)))
            {
                pump.Blocked = true;

                var ex = await Assert.ThrowsAsync<RevitBusyException>(() =>
                    pump.Queue.EnqueueAsync(ctx => 1, TimeSpan.FromMilliseconds(150), CancellationToken.None));

                Assert.Contains("尚未开始执行", ex.Message);
            }
        }

        [Fact]
        public async Task AbandonedWorkIsNeverExecutedEvenIfPumpedLater()
        {
            // 本测试是整个 M2 的核心：ExternalEvent 没有取消机制，
            // 超时返回给客户端之后，那条工作仍会留在队列里等下一次 Pump。
            // 若不做放弃标记，它会在"客户端已经放弃"之后静默修改用户模型。
            var signal = new ManualSignal();
            var queue = new DispatchQueue<FakeContext>(signal);
            var executed = 0;

            var task = queue.EnqueueAsync(
                ctx => Interlocked.Increment(ref executed),
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);

            await Assert.ThrowsAsync<RevitBusyException>(() => task);

            // Revit 事后空闲下来，Pump 被调用——工作必须被跳过
            var ran = queue.Pump(new FakeContext(), TimeSpan.FromSeconds(1));

            Assert.Equal(0, ran);
            Assert.Equal(0, Volatile.Read(ref executed));
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public async Task WorkAlreadyRunningYieldsTimeoutNotRevitBusy()
        {
            // 这个区分很重要：REVIT_BUSY 意味着"模型没被碰过"，
            // 对一条已经跑起来的写操作说 REVIT_BUSY，会让调用方基于错误前提去重试。
            var signal = new ManualSignal();
            var queue = new DispatchQueue<FakeContext>(signal);
            var started = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);

            var task = queue.EnqueueAsync(ctx =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return 42;
            }, TimeSpan.FromMilliseconds(200), CancellationToken.None);

            var pumping = Task.Run(() => queue.Pump(new FakeContext(), TimeSpan.FromSeconds(5)));
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "工作应已开始执行");

            // 工作正在跑，此时等待超时：宽限期内放行，拿到真实结果
            release.Set();
            Assert.Equal(42, await task);
            await pumping;
        }

        [Fact]
        public async Task CancellationBeforeStartAbandonsTheWork()
        {
            var signal = new ManualSignal();
            var queue = new DispatchQueue<FakeContext>(signal);
            var executed = 0;

            using (var cts = new CancellationTokenSource())
            {
                var task = queue.EnqueueAsync(
                    ctx => Interlocked.Increment(ref executed), LongTimeout, cts.Token);

                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

                // 客户端断开连接后，工作同样不能再落到模型上
                Assert.Equal(0, queue.Pump(new FakeContext(), TimeSpan.FromSeconds(1)));
                Assert.Equal(0, Volatile.Read(ref executed));
            }
        }

        [Fact]
        public async Task AlreadyCancelledTokenFailsImmediately()
        {
            var queue = new DispatchQueue<FakeContext>(new ManualSignal());
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    queue.EnqueueAsync(ctx => 1, LongTimeout, cts.Token));
            }
        }

        [Fact]
        public async Task TimeoutIsRoughlyHonoured()
        {
            var queue = new DispatchQueue<FakeContext>(new ManualSignal());
            var clock = Stopwatch.StartNew();

            await Assert.ThrowsAsync<RevitBusyException>(() =>
                queue.EnqueueAsync(ctx => 1, TimeSpan.FromMilliseconds(300), CancellationToken.None));

            Assert.InRange(clock.ElapsedMilliseconds, 250, 3000);
        }

        [Fact]
        public async Task SignalFailureFailsFastInsteadOfWaitingForTimeout()
        {
            // 调度器没初始化时，让调用方干等 60 秒毫无意义
            var queue = new DispatchQueue<FakeContext>(new BrokenSignal());
            var clock = Stopwatch.StartNew();

            await Assert.ThrowsAsync<DispatchStoppedException>(() =>
                queue.EnqueueAsync(ctx => 1, TimeSpan.FromSeconds(30), CancellationToken.None));

            Assert.True(clock.ElapsedMilliseconds < 2000, "应立即失败而不是等到超时");
        }
    }

    public class DispatchQueueShutdownTests
    {
        [Fact]
        public async Task ShutdownFailsPendingWorkImmediately()
        {
            // Revit 关闭时还挂着的 HTTP 请求不应一直等到自己的超时
            var queue = new DispatchQueue<FakeContext>(new ManualSignal());
            var task = queue.EnqueueAsync(ctx => 1, TimeSpan.FromMinutes(5), CancellationToken.None);

            queue.Shutdown();

            await Assert.ThrowsAsync<DispatchStoppedException>(() => task);
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public async Task EnqueueAfterShutdownIsRejected()
        {
            var queue = new DispatchQueue<FakeContext>(new ManualSignal());
            queue.Shutdown();

            await Assert.ThrowsAsync<DispatchStoppedException>(() =>
                queue.EnqueueAsync(ctx => 1, TimeSpan.FromSeconds(5), CancellationToken.None));
        }

        [Fact]
        public void ShutdownIsIdempotent()
        {
            var queue = new DispatchQueue<FakeContext>(new ManualSignal());
            queue.Shutdown();
            queue.Shutdown();
            Assert.True(queue.IsShutDown);
        }

        [Fact]
        public async Task ShutdownDoesNotDisturbWorkThatAlreadyCompleted()
        {
            var signal = new ManualSignal();
            var queue = new DispatchQueue<FakeContext>(signal);

            var task = queue.EnqueueAsync(ctx => 7, TimeSpan.FromSeconds(30), CancellationToken.None);
            queue.Pump(new FakeContext(), TimeSpan.FromSeconds(1));
            Assert.Equal(7, await task);

            queue.Shutdown();
            Assert.Equal(7, await task);
        }
    }
}
