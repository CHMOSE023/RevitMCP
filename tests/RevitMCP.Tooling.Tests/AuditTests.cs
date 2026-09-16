using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Protocol.Mcp;
using RevitMCP.Tooling;
using RevitMCP.Tooling.Dispatch;
using Xunit;

namespace RevitMCP.Tooling.Tests
{
    public sealed class CountedOutput : IReportsAffectedElements
    {
        [McpParam("改了几个")]
        public int Changed { get; set; }

        int IReportsAffectedElements.AffectedElements => Changed;
    }

    [McpTool("test_counted", Description = "写工具，向审计报告影响面。", ReadOnly = false)]
    public sealed class CountedTool : McpTool<FakeHost, MutateInput, CountedOutput>
    {
        public override CountedOutput Execute(MutateInput input, ToolExecutionContext<FakeHost> context)
        {
            context.Warnings.Add("顺便提一句");
            return new CountedOutput { Changed = 17 };
        }
    }

    public sealed class AuditTests
    {
        private static ToolPipeline<FakeHost> Build(
            out List<ToolAuditEntry> log,
            IWorkDispatcher<FakeHost> dispatcher = null,
            bool writeEnabled = true,
            Action<ToolAuditEntry> onAudit = null)
        {
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);

            var entries = new List<ToolAuditEntry>();
            log = entries;

            return new ToolPipeline<FakeHost>(
                registry,
                dispatcher ?? new ImmediateDispatcher(new FakeHost()),
                new ToolPipelineOptions
                {
                    WriteEnabled = () => writeEnabled,
                    Audit = entry =>
                    {
                        entries.Add(entry);
                        onAudit?.Invoke(entry);
                    }
                });
        }

        private static JsonValue Args(params (string Key, JsonValue Value)[] pairs)
        {
            var json = JsonValue.NewObject();
            foreach (var pair in pairs) json.Set(pair.Key, pair.Value);
            return json;
        }

        // ---------- 每条路径都要留痕 ----------

        [Fact]
        public async Task SuccessfulCallIsAudited()
        {
            var pipeline = Build(out var log);

            await pipeline.CallToolAsync(
                "test_greet", Args(("name", JsonValue.String("世界"))), CancellationToken.None);

            var entry = Assert.Single(log);
            Assert.Equal("test_greet", entry.ToolName);
            Assert.Equal(ToolOutcome.Succeeded, entry.Outcome);
            Assert.True(entry.ReadOnly);
            Assert.Null(entry.ErrorCode);
            Assert.Contains("name=", entry.Arguments);
        }

        [Fact]
        public async Task RejectedWriteIsAuditedToo()
        {
            var pipeline = Build(out var log, writeEnabled: false);

            await pipeline.CallToolAsync(
                "test_mutate", Args(("title", JsonValue.String("x"))), CancellationToken.None);

            // 一串被写保护拒掉的写请求本身就是值得看见的信号，不记就看不见了
            var entry = Assert.Single(log);
            Assert.Equal(ToolOutcome.Rejected, entry.Outcome);
            Assert.Equal(McpDomainError.WriteDisabled, entry.ErrorCode);
            Assert.False(entry.ReadOnly);
        }

        [Fact]
        public async Task ToolFailureIsAuditedWithItsErrorCode()
        {
            var pipeline = Build(out var log);

            await pipeline.CallToolAsync("test_fail", JsonValue.NewObject(), CancellationToken.None);

            var entry = Assert.Single(log);
            Assert.Equal(ToolOutcome.Failed, entry.Outcome);
            Assert.Equal(McpDomainError.NoActiveDocument, entry.ErrorCode);
        }

        [Fact]
        public async Task BadArgumentsAreAuditedAsRejected()
        {
            var pipeline = Build(out var log);

            // 绑定就没过，工具根本没跑
            await pipeline.CallToolAsync("test_greet", JsonValue.NewObject(), CancellationToken.None);

            var entry = Assert.Single(log);
            Assert.Equal(ToolOutcome.Rejected, entry.Outcome);
            Assert.Equal(McpDomainError.InvalidParameter, entry.ErrorCode);
        }

        [Fact]
        public async Task UnknownToolStillLeavesATrace()
        {
            var pipeline = Build(out var log);

            await Assert.ThrowsAsync<ToolNotFoundException>(() =>
                pipeline.CallToolAsync("nope", JsonValue.NewObject(), CancellationToken.None));

            // 走的是 JSON-RPC 错误而非 isError，但审计照记——
            // 模型反复叫一个不存在的工具，是值得看见的
            var entry = Assert.Single(log);
            Assert.Equal("nope", entry.ToolName);
            Assert.Equal(ToolOutcome.Rejected, entry.Outcome);
        }

        // ---------- REVIT_BUSY 与 TIMEOUT 的区分要传导到审计 ----------

        [Fact]
        public async Task RevitBusyIsRejectedBecauseTheModelWasNeverTouched()
        {
            var pipeline = Build(out var log,
                new ImmediateDispatcher(throwInstead: new RevitBusyException(TimeSpan.FromSeconds(5))));

            await pipeline.CallToolAsync(
                "test_mutate", Args(("title", JsonValue.String("x"))), CancellationToken.None);

            var entry = Assert.Single(log);
            Assert.Equal(ToolOutcome.Rejected, entry.Outcome);
            Assert.Equal(McpDomainError.RevitBusy, entry.ErrorCode);
        }

        [Fact]
        public async Task TimeoutIsFailedBecauseTheWorkHadAlreadyStarted()
        {
            var pipeline = Build(out var log,
                new ImmediateDispatcher(throwInstead: new DispatchTimeoutException(TimeSpan.FromSeconds(5))));

            await pipeline.CallToolAsync(
                "test_mutate", Args(("title", JsonValue.String("x"))), CancellationToken.None);

            // 这条区分和 REVIT_BUSY / TIMEOUT 的区分是同一件事：
            // 事后翻审计时，"没碰过模型"和"可能改了一半"绝不能混为一谈
            var entry = Assert.Single(log);
            Assert.Equal(ToolOutcome.Failed, entry.Outcome);
            Assert.Equal(McpDomainError.Timeout, entry.ErrorCode);
        }

        // ---------- 影响面与警告 ----------

        [Fact]
        public async Task AffectedElementsComeFromTheOutput()
        {
            var pipeline = Build(out var log);

            await pipeline.CallToolAsync(
                "test_counted", Args(("title", JsonValue.String("x"))), CancellationToken.None);

            var entry = Assert.Single(log);
            Assert.Equal(17, entry.AffectedElements);
            Assert.Equal(1, entry.WarningCount);
        }

        [Fact]
        public async Task OutputsThatDoNotReportAffectedElementsLeaveItUnset()
        {
            var pipeline = Build(out var log);

            await pipeline.CallToolAsync(
                "test_greet", Args(("name", JsonValue.String("x"))), CancellationToken.None);

            // -1 而不是 0：区分"没报告"和"确实一个都没动"
            Assert.Equal(-1, log.Single().AffectedElements);
        }

        // ---------- 审计本身不能碍事 ----------

        [Fact]
        public async Task AFailingAuditSinkDoesNotBreakTheCall()
        {
            var pipeline = Build(out var log,
                onAudit: entry => throw new InvalidOperationException("磁盘满了"));

            var result = await pipeline.CallToolAsync(
                "test_greet", Args(("name", JsonValue.String("世界"))), CancellationToken.None);

            // 写不进审计是运维问题，不该变成模型看到的工具失败
            Assert.False(result.IsError);
        }

        // ---------- 入参摘要 ----------

        [Fact]
        public void LongArraysAreSummarisedNotDumped()
        {
            var ids = JsonValue.NewArray();
            for (var i = 0; i < 500; i++) ids.Add(JsonValue.String("10" + i));

            var summary = ArgumentSummary.Of(Args(("elementIds", ids)));

            // 500 个 ID 原样写进日志等于没写
            Assert.Contains("共 500 项", summary);
            Assert.True(summary.Length < 120, "摘要不该超过一行：" + summary);
        }

        [Fact]
        public void LongStringsAreTruncated()
        {
            var summary = ArgumentSummary.Of(Args(("value", JsonValue.String(new string('x', 400)))));

            Assert.True(summary.Length < 120, summary);
            Assert.Contains("…", summary);
        }

        [Fact]
        public void EmptyArgumentsReadAsSuch()
        {
            Assert.Equal("(无参数)", ArgumentSummary.Of(JsonValue.NewObject()));
            Assert.Equal("(无参数)", ArgumentSummary.Of(null));
        }

        [Fact]
        public void ShortValuesSurviveIntact()
        {
            var summary = ArgumentSummary.Of(Args(
                ("category", JsonValue.String("OST_Walls")),
                ("limit", JsonValue.Number(100))));

            Assert.Contains("category=\"OST_Walls\"", summary);
            Assert.Contains("limit=100", summary);
        }

        // ---------- 成行的记录 ----------

        [Fact]
        public void EntryRendersOnOneGreppableLine()
        {
            var entry = new ToolAuditEntry
            {
                ToolName = "revit_set_element_parameters",
                Arguments = "elementIds=[\"1\",\"2\",…共 500 项]",
                ReadOnly = false,
                Outcome = ToolOutcome.Succeeded,
                AffectedElements = 500,
                WarningCount = 2,
                DurationMs = 1234
            };

            var line = entry.ToString();

            Assert.DoesNotContain("\n", line);
            Assert.Contains("revit_set_element_parameters", line);
            Assert.Contains("[写]", line);
            Assert.Contains("成功", line);
            Assert.Contains("1234ms", line);
            Assert.Contains("影响 500 个构件", line);
            Assert.Contains("2 条警告", line);
        }

        [Fact]
        public void FailedEntryCarriesTheErrorCode()
        {
            var line = new ToolAuditEntry
            {
                ToolName = "revit_create_wall",
                Outcome = ToolOutcome.Rejected,
                ErrorCode = McpDomainError.WriteDisabled,
                DurationMs = 1
            }.ToString();

            Assert.Contains("被拒/WRITE_DISABLED", line);
            // 没报告影响面就不要凭空写一个 0 出来
            Assert.DoesNotContain("影响", line);
        }
    }
}
