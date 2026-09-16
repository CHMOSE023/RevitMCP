using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Tooling;
using RevitMCP.Tooling.Dispatch;
using Xunit;

namespace RevitMCP.Tooling.Tests
{
    // ==================== 测试替身 ====================

    /// <summary>
    /// 记录型写作用域，替代 RevitWriteScope。
    /// 真实实现里"开事务/提交/回滚"这几步要有 Revit 才跑得起来，
    /// 但"谁该进作用域、失败时该不该回滚"这套判断不需要——
    /// 而恰恰是后者错了会直接改坏用户模型。
    /// </summary>
    internal sealed class RecordingWriteScope : IWriteScope<FakeHost>
    {
        public List<string> Entered { get; } = new List<string>();
        public int Committed { get; private set; }
        public int RolledBack { get; private set; }

        /// <summary>模拟失败预处理器吞掉一条 Revit 警告。</summary>
        public string WarningToEmit { get; set; }

        public TResult Run<TResult>(FakeHost host, WriteScopeInfo info, Func<TResult> work)
        {
            Entered.Add(info.ToolName);
            if (WarningToEmit != null) info.Warnings.Add(WarningToEmit);

            try
            {
                var result = work();
                Committed++;
                return result;
            }
            catch
            {
                RolledBack++;
                throw;   // 异常必须继续上抛，否则管线没法把它映射成领域错误码
            }
        }
    }

    public sealed class WarnInput
    {
        [McpParam("提示内容", Required = true)]
        public string Text { get; set; }
    }

    [McpTool("test_warn", Description = "只读工具也能往 warnings 里写东西。", ReadOnly = true)]
    public sealed class WarningTool : McpTool<FakeHost, WarnInput, GreetOutput>
    {
        public override GreetOutput Execute(WarnInput input, ToolExecutionContext<FakeHost> context)
        {
            context.Warnings.Add(input.Text);
            context.Warnings.Add("第二条");
            return new GreetOutput { Message = "好了", Document = context.Host.DocumentTitle };
        }
    }

    [McpTool("test_write_boom", Description = "写工具，执行到一半炸了。", ReadOnly = false)]
    public sealed class ExplodingWriteTool : McpTool<FakeHost, MutateInput, GreetOutput>
    {
        public override GreetOutput Execute(MutateInput input, ToolExecutionContext<FakeHost> context)
        {
            context.Host.DocumentTitle = input.Title;   // 先改一下，模拟"事务里已经动过模型"
            throw new ToolFailureException(McpDomainError.ElementNotFound, "第 3 个构件不存在。");
        }
    }

    [McpTool("test_write_crash", Description = "写工具，抛未预期异常。", ReadOnly = false)]
    public sealed class CrashingWriteTool : McpTool<FakeHost, MutateInput, GreetOutput>
    {
        public override GreetOutput Execute(MutateInput input, ToolExecutionContext<FakeHost> context) =>
            throw new InvalidOperationException("Revit API 内部炸了");
    }

    // ==================== 测试 ====================

    public sealed class WriteScopeTests
    {
        private static ToolPipeline<FakeHost> Build(
            out RecordingWriteScope scope,
            FakeHost host = null,
            bool writeEnabled = true,
            int maxElementsPerWrite = 500,
            string warningToEmit = null)
        {
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);

            scope = new RecordingWriteScope { WarningToEmit = warningToEmit };

            return new ToolPipeline<FakeHost>(
                registry,
                new ImmediateDispatcher(host ?? new FakeHost()),
                new ToolPipelineOptions
                {
                    WriteEnabled = () => writeEnabled,
                    MaxElementsPerWrite = () => maxElementsPerWrite,
                    DefaultTimeoutSeconds = () => 30
                },
                scope);
        }

        private static JsonValue Args(params (string Key, JsonValue Value)[] pairs)
        {
            var json = JsonValue.NewObject();
            foreach (var pair in pairs) json.Set(pair.Key, pair.Value);
            return json;
        }

        private static string[] WarningsOf(JsonValue structuredContent)
        {
            var warnings = structuredContent["warnings"];
            if (warnings == null || !warnings.IsArray) return new string[0];
            return warnings.Items.Select(w => w.AsString).ToArray();
        }

        // ---------- 谁进作用域 ----------

        [Fact]
        public async Task ReadOnlyToolNeverOpensATransaction()
        {
            var pipeline = Build(out var scope);

            var result = await pipeline.CallToolAsync(
                "test_greet", Args(("name", JsonValue.String("世界"))), CancellationToken.None);

            Assert.False(result.IsError);
            // 只读工具开事务不只是浪费：它会让"只读"这个承诺在实现上不成立
            Assert.Empty(scope.Entered);
        }

        [Fact]
        public async Task WriteToolRunsInsideTheWriteScope()
        {
            var host = new FakeHost();
            var pipeline = Build(out var scope, host);

            var result = await pipeline.CallToolAsync(
                "test_mutate", Args(("title", JsonValue.String("新标题"))), CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Equal(new[] { "test_mutate" }, scope.Entered.ToArray());
            Assert.Equal(1, scope.Committed);
            Assert.Equal(0, scope.RolledBack);
            Assert.Equal("新标题", host.DocumentTitle);
        }

        [Fact]
        public async Task ScopeReceivesTheToolNameSoTheTransactionCanBeNamed()
        {
            var pipeline = Build(out var scope);

            await pipeline.CallToolAsync(
                "test_mutate", Args(("title", JsonValue.String("x"))), CancellationToken.None);

            // 事务名形如 "MCP: test_mutate"——用户在撤销栈里认得出这一步是谁做的
            Assert.Equal("test_mutate", scope.Entered.Single());
        }

        [Fact]
        public async Task WriteProtectionRejectsBeforeTheScopeIsEverEntered()
        {
            var host = new FakeHost();
            var pipeline = Build(out var scope, host, writeEnabled: false);

            var result = await pipeline.CallToolAsync(
                "test_mutate", Args(("title", JsonValue.String("新标题"))), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains(McpDomainError.WriteDisabled, result.Text);

            // 被写保护拒掉的调用既不该占主线程，也不该开事务。
            // 否则一个反复试探写工具的模型能把 Revit 的 UI 拖垮
            Assert.Empty(scope.Entered);
            Assert.Equal("项目1.rvt", host.DocumentTitle);
        }

        // ---------- 失败即回滚 ----------

        [Fact]
        public async Task WriteToolFailureRollsBackAndKeepsTheDomainErrorCode()
        {
            var pipeline = Build(out var scope);

            var result = await pipeline.CallToolAsync(
                "test_write_boom", Args(("title", JsonValue.String("改了一半"))), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains(McpDomainError.ElementNotFound, result.Text);

            Assert.Equal(1, scope.RolledBack);
            Assert.Equal(0, scope.Committed);
        }

        [Fact]
        public async Task UnexpectedExceptionAlsoRollsBack()
        {
            var pipeline = Build(out var scope);

            // ToolFailureException 是工具主动抛的，作用域回滚它不奇怪；
            // 真正要盯的是 NullReference 这类没人预料到的异常也一样回滚——
            // 否则模型会停在"改了一半"的状态上
            var result = await pipeline.CallToolAsync(
                "test_write_crash", Args(("title", JsonValue.String("x"))), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Equal(1, scope.RolledBack);
            Assert.Equal(0, scope.Committed);
        }

        // ---------- 警告回填 ----------

        [Fact]
        public async Task SuppressedWarningsAreSurfacedInTheOutput()
        {
            var pipeline = Build(out var scope, warningToEmit: "Revit 警告已自动忽略：高亮显示的墙重叠");

            var result = await pipeline.CallToolAsync(
                "test_mutate", Args(("title", JsonValue.String("新标题"))), CancellationToken.None);

            Assert.False(result.IsError);

            // 静默吞掉警告比弹模态框更危险：模型和用户都不会知道刚才发生过什么
            var warnings = WarningsOf(result.StructuredContent);
            Assert.Single(warnings);
            Assert.Contains("墙重叠", warnings[0]);
            Assert.Contains("墙重叠", result.Text);
        }

        [Fact]
        public async Task ToolsCanAddTheirOwnWarningsEvenWhenReadOnly()
        {
            var pipeline = Build(out var scope);

            var result = await pipeline.CallToolAsync(
                "test_warn", Args(("text", JsonValue.String("未指定 levelId，使用「标高 1」"))), CancellationToken.None);

            Assert.False(result.IsError);
            var warnings = WarningsOf(result.StructuredContent);
            Assert.Equal(2, warnings.Length);
            Assert.Contains("标高 1", warnings[0]);
        }

        [Fact]
        public async Task NoWarningsMeansNoWarningsField()
        {
            var pipeline = Build(out var scope);

            var result = await pipeline.CallToolAsync(
                "test_mutate", Args(("title", JsonValue.String("新标题"))), CancellationToken.None);

            // 空数组和缺字段对模型是两种信号，别让它去分辨 "warnings": []
            Assert.Null(result.StructuredContent["warnings"]);
        }

        [Fact]
        public async Task FailedCallDoesNotDragWarningsAlong()
        {
            var pipeline = Build(out var scope, warningToEmit: "某条警告");

            var result = await pipeline.CallToolAsync(
                "test_write_boom", Args(("title", JsonValue.String("x"))), CancellationToken.None);

            Assert.True(result.IsError);
            // 失败文本本身已经说明了原因，再挂一串警告只会喧宾夺主
            Assert.Null(result.StructuredContent);
            Assert.DoesNotContain("某条警告", result.Text);
        }

        // ---------- 默认行为 ----------

        [Fact]
        public async Task PipelineWithoutAScopeStillRunsWriteTools()
        {
            // 没注入写作用域时用 PassthroughWriteScope：
            // 只有只读工具的场景和单元测试都不该因此跑不起来
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);
            var host = new FakeHost();

            var pipeline = new ToolPipeline<FakeHost>(
                registry,
                new ImmediateDispatcher(host),
                new ToolPipelineOptions { WriteEnabled = () => true });

            var result = await pipeline.CallToolAsync(
                "test_mutate", Args(("title", JsonValue.String("新标题"))), CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Equal("新标题", host.DocumentTitle);
        }

        [Fact]
        public void PassthroughScopeJustRunsTheWork()
        {
            var scope = new PassthroughWriteScope<FakeHost>();
            var ran = false;

            var result = scope.Run(new FakeHost(), new WriteScopeInfo("t", new List<string>()), () =>
            {
                ran = true;
                return 42;
            });

            Assert.True(ran);
            Assert.Equal(42, result);
        }

        // ---------- 规模阈值 ----------

        [Fact]
        public async Task MaxElementsPerWriteReachesTheTool()
        {
            // 阈值判断本身在 Revit 工具里（GuardScale），管线只负责把配置值送到位。
            // 送不到位的话那道闸就形同虚设，所以这里盯住的是"值确实传下去了"
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);

            var pipeline = new ToolPipeline<FakeHost>(
                registry,
                new ImmediateDispatcher(new FakeHost()),
                new ToolPipelineOptions
                {
                    WriteEnabled = () => true,
                    MaxElementsPerWrite = () => 7
                });

            var result = await pipeline.CallToolAsync(
                "test_limit", JsonValue.NewObject(), CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Equal("7", result.StructuredContent["message"].AsString);
        }
    }

    [McpTool("test_limit", Description = "回显上下文里的规模上限。", ReadOnly = true)]
    public sealed class LimitEchoTool : McpTool<FakeHost, EmptyInput, GreetOutput>
    {
        public override GreetOutput Execute(EmptyInput input, ToolExecutionContext<FakeHost> context) =>
            new GreetOutput
            {
                Message = context.MaxElementsPerWrite.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Document = context.Host.DocumentTitle
            };
    }
}
