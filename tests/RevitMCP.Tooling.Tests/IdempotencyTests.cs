using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Tooling;
using RevitMCP.Tooling.Dispatch;
using RevitMCP.Tooling.Schema;
using Xunit;

namespace RevitMCP.Tooling.Tests
{
    /// <summary>
    /// 幂等键与操作状态（F09）。
    ///
    /// 要防的是超时之后那两种错误反应：直接重试（模型里多出一整套重复构件，
    /// 不报错、不产生警告、撤销栈里只是一步普通创建），或当作失败继续。
    /// </summary>
    public class IdempotencyTests
    {
        private static ToolPipeline<FakeHost> Build(FakeHost host, OperationJournal journal = null)
        {
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);

            return new ToolPipeline<FakeHost>(
                registry,
                new ImmediateDispatcher(host),
                new ToolPipelineOptions { WriteEnabled = () => true },
                writeScope: null,
                contextIdentity: h => new ContextIdentity("doc-a", "项目A"),
                journal: journal);
        }

        private static JsonValue Mutate(string title, string requestKey = null)
        {
            var arguments = JsonValue.NewObject().Set("title", title);
            if (requestKey != null) arguments.Set(SchemaGenerator.RequestKeyParameter, requestKey);
            return arguments;
        }

        [Fact]
        public async Task SameKeySamePayloadRunsOnlyOnce()
        {
            var host = new FakeHost();
            var pipeline = Build(host);

            var first = await pipeline.CallToolAsync("test_mutate", Mutate("甲", "k1"), CancellationToken.None);
            Assert.False(first.IsError);
            Assert.Equal("甲", host.DocumentTitle);

            // 第二次：一个字节都不该执行。把标题改掉，工具真跑了就会把它盖回"甲"
            host.DocumentTitle = "被改过";
            var second = await pipeline.CallToolAsync("test_mutate", Mutate("甲", "k1"), CancellationToken.None);

            Assert.False(second.IsError);
            Assert.Equal("被改过", host.DocumentTitle);   // 工具没有再跑一遍
            Assert.Contains("这是重放", second.Text);
        }

        [Fact]
        public async Task ReplayReturnsTheOriginalReceipt()
        {
            var pipeline = Build(new FakeHost());

            var first = await pipeline.CallToolAsync("test_mutate", Mutate("甲", "k1"), CancellationToken.None);
            var second = await pipeline.CallToolAsync("test_mutate", Mutate("甲", "k1"), CancellationToken.None);

            Assert.Equal(
                first.StructuredContent["message"].AsString,
                second.StructuredContent["message"].AsString);

            // 重放这件事本身必须说出来，不能让调用方以为又建了一次
            var warnings = second.StructuredContent["warnings"];
            Assert.Contains(warnings.Items, w => w.AsString.Contains("这是重放"));
        }

        [Fact]
        public async Task KeyOrderDoesNotBreakIdempotency()
        {
            // 客户端重试时重新序列化一遍，key 顺序很可能变——
            // 拿普通 JSON 比就会把同一次请求判成不同的请求，幂等直接失效
            var host = new FakeHost();
            var pipeline = Build(host);

            var a = JsonValue.NewObject()
                .Set("title", "甲")
                .Set(SchemaGenerator.RequestKeyParameter, "k1");

            var b = JsonValue.NewObject()
                .Set(SchemaGenerator.RequestKeyParameter, "k1")
                .Set("title", "甲");

            await pipeline.CallToolAsync("test_mutate", a, CancellationToken.None);
            host.DocumentTitle = "原样";
            await pipeline.CallToolAsync("test_mutate", b, CancellationToken.None);

            Assert.Equal("原样", host.DocumentTitle);
        }

        [Fact]
        public async Task SameKeyDifferentPayloadIsRejectedAndChangesNothing()
        {
            var host = new FakeHost();
            var pipeline = Build(host);

            await pipeline.CallToolAsync("test_mutate", Mutate("甲", "k1"), CancellationToken.None);
            host.DocumentTitle = "原样";

            var conflict = await pipeline.CallToolAsync("test_mutate", Mutate("乙", "k1"), CancellationToken.None);

            Assert.True(conflict.IsError);
            Assert.Contains(McpDomainError.IdempotencyConflict, conflict.Text);
            Assert.Equal("原样", host.DocumentTitle);   // 第二次没有产生任何写入
        }

        [Fact]
        public async Task WithoutAKeyNothingChanges()
        {
            // 防回归：不给 requestKey 时行为必须与以前完全一致
            var host = new FakeHost();
            var pipeline = Build(host);

            await pipeline.CallToolAsync("test_mutate", Mutate("甲"), CancellationToken.None);
            host.DocumentTitle = "原样";
            await pipeline.CallToolAsync("test_mutate", Mutate("甲"), CancellationToken.None);

            Assert.Equal("甲", host.DocumentTitle);   // 第二次真的又跑了一遍
        }

        [Fact]
        public async Task WriteReceiptsCarryAnOperationId()
        {
            var pipeline = Build(new FakeHost());

            var result = await pipeline.CallToolAsync("test_mutate", Mutate("甲"), CancellationToken.None);

            var operationId = result.StructuredContent["operationId"];
            Assert.NotNull(operationId);
            Assert.StartsWith("op_", operationId.AsString);

            // 只读工具没有可查的状态，加了只是噪音
            var read = await pipeline.CallToolAsync(
                "test_greet", JsonValue.NewObject().Set("name", "x"), CancellationToken.None);
            Assert.Null(read.StructuredContent["operationId"]);
        }

        [Fact]
        public async Task ReadOnlyToolsDoNotTakeARequestKey()
        {
            var pipeline = Build(new FakeHost());

            var arguments = JsonValue.NewObject()
                .Set("name", "x")
                .Set(SchemaGenerator.RequestKeyParameter, "k1");

            var result = await pipeline.CallToolAsync("test_greet", arguments, CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains("未知参数", result.Text);
        }

        [Fact]
        public async Task CommittedOperationIsRecorded()
        {
            var journal = new OperationJournal();
            var pipeline = Build(new FakeHost(), journal);

            var result = await pipeline.CallToolAsync("test_mutate", Mutate("甲", "k1"), CancellationToken.None);

            var record = journal.FindByRequestKey("k1");
            Assert.NotNull(record);
            Assert.Equal(OperationState.Committed, record.State);
            Assert.Equal("test_mutate", record.ToolName);
            Assert.Equal(record.OperationId, result.StructuredContent["operationId"].AsString);
        }

        [Fact]
        public async Task AFailedWriteIsRecordedAsRolledBackNotCommitted()
        {
            var journal = new OperationJournal();
            var pipeline = Build(new FakeHost(), journal);

            var arguments = JsonValue.NewObject().Set(SchemaGenerator.RequestKeyParameter, "k-fail");
            var result = await pipeline.CallToolAsync("test_write_fail", arguments, CancellationToken.None);

            Assert.True(result.IsError);

            var record = journal.FindByRequestKey("k-fail");
            Assert.NotNull(record);
            Assert.Equal(OperationState.RolledBack, record.State);
        }

        [Fact]
        public async Task ReplayingAFailedCallDoesNotPretendItSucceeded()
        {
            var pipeline = Build(new FakeHost());
            var arguments = JsonValue.NewObject().Set(SchemaGenerator.RequestKeyParameter, "k-fail");

            await pipeline.CallToolAsync("test_write_fail", arguments, CancellationToken.None);
            var replay = await pipeline.CallToolAsync("test_write_fail", arguments, CancellationToken.None);

            // 上次就没成功：不能装作成功，也不能假装这次是全新的一遍
            Assert.True(replay.IsError);
            Assert.Contains("这是重放", replay.Text);
            Assert.Contains("换一个 requestKey", replay.Text);
        }

        [Fact]
        public async Task TimeoutIsRecordedAsUnknownAndHandsBackTheOperationId()
        {
            // 这是整套机制存在的那一刻：结果不确定，而调用方需要一个抓手去查证。
            // 真机上凑不出超时（每个工具都显式声明了 TimeoutSeconds），所以在这里造
            var journal = new OperationJournal();
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);

            var pipeline = new ToolPipeline<FakeHost>(
                registry,
                new ImmediateDispatcher(null, new DispatchTimeoutException(TimeSpan.FromSeconds(60))),
                new ToolPipelineOptions { WriteEnabled = () => true },
                writeScope: null,
                contextIdentity: h => new ContextIdentity("doc-a", "项目A"),
                journal: journal);

            var result = await pipeline.CallToolAsync("test_mutate", Mutate("甲", "k-timeout"), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains(McpDomainError.Timeout, result.Text);

            var record = journal.FindByRequestKey("k-timeout");
            Assert.NotNull(record);
            Assert.Equal(OperationState.Unknown, record.State);

            // 报错文本里必须带着 operationId：最需要它的那一刻，回执是拿不到的
            Assert.Contains(record.OperationId, result.Text);
            Assert.Contains("不要直接重试", result.Text);
        }

        [Fact]
        public async Task RevitBusyIsCancelledNotUnknown()
        {
            // 主线程被占、工作**从未开始** —— 模型没被碰过，可以放心重发。
            // 把它和 TIMEOUT 混成一种状态，就等于把"可以重试"和"千万别重试"混为一谈
            var journal = new OperationJournal();
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);

            var pipeline = new ToolPipeline<FakeHost>(
                registry,
                new ImmediateDispatcher(null, new RevitBusyException(TimeSpan.FromSeconds(5))),
                new ToolPipelineOptions { WriteEnabled = () => true },
                writeScope: null,
                contextIdentity: h => new ContextIdentity("doc-a", "项目A"),
                journal: journal);

            await pipeline.CallToolAsync("test_mutate", Mutate("甲", "k-busy"), CancellationToken.None);

            Assert.Equal(OperationState.Cancelled, journal.FindByRequestKey("k-busy").State);
        }

        [Fact]
        public void EvictedOperationsAreSimplyGone()
        {
            // 淘汰之后查不到——工具层会把"查不到"翻译成 unknown + 查证指引，
            // 而不是"没有这个操作"。这条只确认日志本身的行为
            var journal = new OperationJournal(capacity: 2);

            var first = journal.Begin("t", "k1", "h1", "doc");
            journal.Begin("t", "k2", "h2", "doc");
            journal.Begin("t", "k3", "h3", "doc");

            Assert.Null(journal.FindById(first.OperationId));
            Assert.Equal(2, journal.Count);
        }

        [Fact]
        public void RetentionDropsOldRecords()
        {
            var now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
            var journal = new OperationJournal(retention: TimeSpan.FromHours(1), clock: () => now);

            var old = journal.Begin("t", "k1", "h1", "doc");

            now = now.AddHours(2);

            Assert.Null(journal.FindById(old.OperationId));
        }

        [Fact]
        public void CommittedIsNeverOverwritten()
        {
            // 提交之后再收到取消，状态仍然是已提交——
            // "客户端断开了所以当作没发生"是不诚实的：改动真的在模型里
            var journal = new OperationJournal();
            var record = journal.Begin("t", "k1", "h1", "doc");

            journal.MarkRunning(record);
            journal.Complete(record, OperationState.Committed, resultJson: "{}");
            journal.Complete(record, OperationState.RolledBack, "X", "取消");

            Assert.Equal(OperationState.Committed, journal.FindById(record.OperationId).State);
        }
    }

    [McpTool("test_write_fail", Description = "会改模型但总是失败。", ReadOnly = false)]
    public sealed class FailingWriteTool : McpTool<FakeHost, EmptyInput, GreetOutput>
    {
        public override GreetOutput Execute(EmptyInput input, ToolExecutionContext<FakeHost> context) =>
            throw new ToolFailureException(McpDomainError.TransactionFailed, "Revit 拒绝了这次修改。");
    }
}
