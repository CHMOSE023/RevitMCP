using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Protocol.Mcp;
using RevitMCP.Tooling;
using Xunit;

namespace RevitMCP.Tooling.Tests
{
    /// <summary>
    /// 活动文档切换的检测。
    ///
    /// 这条机制是 M6 实测撞出来的：中途在 Revit 里换了个文档，
    /// 上一轮拿到的构件 ID 在新文档里要么不存在、要么指向完全不相干的东西，
    /// 而调用方**没有任何办法察觉**——它只会看到一连串莫名其妙的 ELEMENT_NOT_FOUND，
    /// 然后开始怀疑自己的 ID 是不是记错了，换一个再试。
    /// </summary>
    public class ContextSwitchTests
    {
        /// <summary>可随时改答案的身份提供者，模拟用户切换文档。</summary>
        private sealed class Switcher
        {
            public string Key = "doc-a";
            public string Label = "项目A";

            public ContextIdentity Describe(FakeHost host)
            {
                return Key == null ? null : new ContextIdentity(Key, Label);
            }
        }

        private static ToolPipeline<FakeHost> Build(Switcher switcher)
        {
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);

            return new ToolPipeline<FakeHost>(
                registry,
                new ImmediateDispatcher(new FakeHost()),
                new ToolPipelineOptions { WriteEnabled = () => true },
                writeScope: null,
                contextIdentity: switcher.Describe);
        }

        private static JsonValue Greet() => JsonValue.NewObject().Set("name", "x");

        private static string[] WarningsOf(ToolCallResult result)
        {
            var warnings = result.StructuredContent?["warnings"];
            return warnings == null || !warnings.IsArray
                ? new string[0]
                : warnings.Items.Select(i => i.AsString).ToArray();
        }

        [Fact]
        public async Task FirstCallDoesNotReportASwitch()
        {
            // 服务刚起来，第一次见到任何文档都不算"换过"
            var pipeline = Build(new Switcher());

            var result = await pipeline.CallToolAsync("test_greet", Greet(), CancellationToken.None);

            Assert.Empty(WarningsOf(result));
        }

        [Fact]
        public async Task SameDocumentAcrossCallsStaysQuiet()
        {
            var pipeline = Build(new Switcher());

            await pipeline.CallToolAsync("test_greet", Greet(), CancellationToken.None);
            var second = await pipeline.CallToolAsync("test_greet", Greet(), CancellationToken.None);

            // 每次调用都提醒一句"文档没变"，等于把 warnings 变成噪音
            Assert.Empty(WarningsOf(second));
        }

        [Fact]
        public async Task SwitchingDocumentsWarnsOnce()
        {
            var switcher = new Switcher();
            var pipeline = Build(switcher);

            await pipeline.CallToolAsync("test_greet", Greet(), CancellationToken.None);

            switcher.Key = "doc-b";
            switcher.Label = "项目B";

            var afterSwitch = await pipeline.CallToolAsync("test_greet", Greet(), CancellationToken.None);
            var warning = Assert.Single(WarningsOf(afterSwitch));

            Assert.Contains("项目A", warning);
            Assert.Contains("项目B", warning);
            Assert.Contains("无效", warning);

            // 提醒过一次就该安静下来——新文档已经是新的基线
            var next = await pipeline.CallToolAsync("test_greet", Greet(), CancellationToken.None);
            Assert.Empty(WarningsOf(next));
        }

        [Fact]
        public async Task FailureTextCarriesTheSwitchNote()
        {
            // 最需要这句话的时刻：模型拿旧 ID 调用，收到"找不到这个构件"。
            // 只说找不到，它会换个 ID 再试；加上"文档换了"，它才知道要重新查
            var switcher = new Switcher();
            var pipeline = Build(switcher);

            await pipeline.CallToolAsync("test_greet", Greet(), CancellationToken.None);

            switcher.Key = "doc-b";
            switcher.Label = "项目B";

            var result = await pipeline.CallToolAsync(
                "test_fail", JsonValue.NewObject(), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains("没有打开的文档", result.Text);   // 工具自己的失败原因还在
            Assert.Contains("活动文档已从", result.Text);      // 切换提示也在
        }

        [Fact]
        public async Task NoIdentityMeansNoDetection()
        {
            // 没有文档可标识时（比如 Revit 里一个文档都没开）不该乱报，
            // 更不该把"无文档 → 有文档"当成一次切换
            var switcher = new Switcher { Key = null };
            var pipeline = Build(switcher);

            await pipeline.CallToolAsync("test_greet", Greet(), CancellationToken.None);

            switcher.Key = "doc-a";
            switcher.Label = "项目A";
            var opened = await pipeline.CallToolAsync("test_greet", Greet(), CancellationToken.None);

            Assert.Empty(WarningsOf(opened));
        }

        [Fact]
        public async Task IdentityFailureDoesNotBreakTheCall()
        {
            // 读身份是附加功能，它自己出问题不该把工具调用一起拖下水
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);

            var pipeline = new ToolPipeline<FakeHost>(
                registry,
                new ImmediateDispatcher(new FakeHost()),
                new ToolPipelineOptions(),
                writeScope: null,
                contextIdentity: host => throw new InvalidOperationException("读文档失败"));

            var result = await pipeline.CallToolAsync("test_greet", Greet(), CancellationToken.None);

            Assert.False(result.IsError);
        }
    }
}
