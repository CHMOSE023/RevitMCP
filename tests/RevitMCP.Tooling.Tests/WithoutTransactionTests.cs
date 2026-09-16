using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Tooling;
using Xunit;

namespace RevitMCP.Tooling.Tests
{
    public sealed class UiStateOutput
    {
        public string Result { get; set; }
    }

    /// <summary>
    /// 改的是界面状态而不是模型：受写保护管辖，但不能开事务。
    /// 现实里的原型是「切换活动视图」——Revit 不允许在事务打开时切换。
    /// </summary>
    [McpTool("test_ui_state", Description = "改界面不改模型。", WithoutTransaction = true)]
    public sealed class UiStateTool : McpTool<FakeHost, EmptyInput, UiStateOutput>
    {
        public override UiStateOutput Execute(EmptyInput input, ToolExecutionContext<FakeHost> context)
        {
            return new UiStateOutput { Result = "切过去了" };
        }
    }

    /// <summary>
    /// <c>WithoutTransaction</c> 把"要不要写保护"和"要不要事务"拆开了。
    /// 在它出现之前，ReadOnly 一个标志同时管着这两件事，
    /// 于是"受管辖但无事务"这一类工具根本没法表达。
    /// </summary>
    public class WithoutTransactionTests
    {
        private static ToolRegistry<FakeHost> Registry()
        {
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);
            return registry;
        }

        private static ToolPipeline<FakeHost> Build(RecordingWriteScope scope, bool writeEnabled)
        {
            return new ToolPipeline<FakeHost>(
                Registry(),
                new ImmediateDispatcher(new FakeHost()),
                new ToolPipelineOptions { WriteEnabled = () => writeEnabled },
                scope);
        }

        [Fact]
        public async Task StillGovernedByWriteProtection()
        {
            // 不开事务不等于不受管辖：它照样改变用户眼前的状态
            var scope = new RecordingWriteScope();
            var result = await Build(scope, writeEnabled: false)
                .CallToolAsync("test_ui_state", JsonValue.NewObject(), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains(McpDomainError.WriteDisabled, result.Text);
        }

        [Fact]
        public async Task RunsWithoutOpeningATransaction()
        {
            var scope = new RecordingWriteScope();
            var result = await Build(scope, writeEnabled: true)
                .CallToolAsync("test_ui_state", JsonValue.NewObject(), CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Empty(scope.Entered);   // 写作用域根本没被进过
        }

        [Fact]
        public async Task OrdinaryWriteToolsStillGetATransaction()
        {
            // 对照组：没有这个标志的写工具，行为不能被改变
            var scope = new RecordingWriteScope();
            var result = await Build(scope, writeEnabled: true)
                .CallToolAsync("test_mutate", JsonValue.NewObject().Set("title", "x"), CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Equal(new[] { "test_mutate" }, scope.Entered.ToArray());
        }

        [Fact]
        public void ItIsNotReportedAsReadOnly()
        {
            // tools/list 里它必须显示成会改东西的工具，否则客户端会以为它无害
            var definition = Registry().Definitions.Single(d => d.Name == "test_ui_state");

            Assert.False(definition.Annotations.ReadOnlyHint);
        }
    }
}
