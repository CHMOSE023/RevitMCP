using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Tooling;
using Xunit;

namespace RevitMCP.Tooling.Tests
{
    public sealed class CarrierOutput
    {
        [McpParam("随便一个兄弟字段，用来确认输出没被整个换掉")]
        public int Total { get; set; }

        [McpParam("工具自己的 warnings，和管线要挂的那个同名")]
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>输出里自带一个 warnings 字段，同时也让管线有话要说。</summary>
    [McpTool("test_warnings_carrier", Description = "输出字段与管线撞名。", ReadOnly = true)]
    public sealed class WarningsCarrierTool : McpTool<FakeHost, EmptyInput, CarrierOutput>
    {
        public override CarrierOutput Execute(EmptyInput input, ToolExecutionContext<FakeHost> context)
        {
            context.Warnings.Add("管线要挂的提示");

            return new CarrierOutput
            {
                Total = 7,
                Warnings = new List<string> { "工具自己查到的数据" }
            };
        }
    }

    /// <summary>
    /// 管线往工具输出里挂 warnings 时，不能盖掉工具已有的同名字段。
    ///
    /// 这个坑实测踩过：revit_get_warnings 的输出字段恰好也叫 warnings，
    /// 管线一挂上去，工具查到的警告数据就整个消失了——而且消失得悄无声息，
    /// total、groupCount 这些兄弟字段都还在，只有数组被换了内容，
    /// 看输出的人只会以为是工具没查到东西。
    /// </summary>
    public class OutputCollisionTests
    {
        private static ToolPipeline<FakeHost> Build()
        {
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);

            return new ToolPipeline<FakeHost>(registry, new ImmediateDispatcher(new FakeHost()));
        }

        [Fact]
        public async Task ToolOwnWarningsFieldSurvives()
        {
            var result = await Build().CallToolAsync(
                "test_warnings_carrier", JsonValue.NewObject(), CancellationToken.None);

            var payload = result.StructuredContent;

            Assert.Equal(7, (int)payload["total"].AsDouble);

            var own = payload["warnings"].Items.Select(i => i.AsString).ToArray();
            Assert.Equal(new[] { "工具自己查到的数据" }, own);
        }

        [Fact]
        public async Task PipelineWarningsMoveAsideInsteadOfOverwriting()
        {
            var result = await Build().CallToolAsync(
                "test_warnings_carrier", JsonValue.NewObject(), CancellationToken.None);

            // 管线的提示不能因为撞名就被丢掉——它换个地方，但必须还在
            var server = result.StructuredContent["serverWarnings"];

            Assert.NotNull(server);
            Assert.Contains("管线要挂的提示", server.Items.Select(i => i.AsString));
        }

        [Fact]
        public async Task ToolsWithoutTheFieldStillGetPlainWarnings()
        {
            // 不撞名的常规工具照旧用 warnings，不该被这套避让逻辑波及
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);

            var pipeline = new ToolPipeline<FakeHost>(
                registry,
                new ImmediateDispatcher(new FakeHost()),
                new ToolPipelineOptions { WriteEnabled = () => true },
                writeScope: null,
                contextIdentity: host => new ContextIdentity("doc-a", "项目A"));

            await pipeline.CallToolAsync(
                "test_greet", JsonValue.NewObject().Set("name", "x"), CancellationToken.None);

            // 第二次换个文档，逼管线产生一条警告
            var switching = new ToolPipeline<FakeHost>(
                registry,
                new ImmediateDispatcher(new FakeHost()),
                new ToolPipelineOptions { WriteEnabled = () => true },
                writeScope: null,
                contextIdentity: host => new ContextIdentity("doc-b", "项目B"));

            var result = await switching.CallToolAsync(
                "test_greet", JsonValue.NewObject().Set("name", "x"), CancellationToken.None);

            Assert.Null(result.StructuredContent["serverWarnings"]);
        }
    }
}
