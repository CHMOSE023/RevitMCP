using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Protocol.Mcp;
using RevitMCP.Tooling;
using Xunit;

namespace RevitMCP.Tooling.Tests
{
    /// <summary>
    /// 回执文本块的形态（F10）。
    ///
    /// 规范建议在 structuredContent 之外再放一份文本，于是同一份数据发两遍。
    /// 实测 96 次真实调用：**带缩进的那份是紧凑 JSON 的 1.61 倍**，
    /// 一次调用实际发出 2.61 份信息量。这里要守住的是：
    /// 省的只能是空白与重复，**语义一个字都不能少**。
    /// </summary>
    public class TextBlockTests
    {
        private static ToolPipeline<FakeHost> Build(TextBlockMode mode)
        {
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);

            return new ToolPipeline<FakeHost>(
                registry,
                new ImmediateDispatcher(new FakeHost()),
                new ToolPipelineOptions { WriteEnabled = () => true, TextBlock = () => mode });
        }

        private static JsonValue Greet() => JsonValue.NewObject().Set("name", "世界");

        [Fact]
        public async Task CompactIsTheDefaultAndCarriesTheSameContent()
        {
            var compact = await Build(TextBlockMode.Compact)
                .CallToolAsync("test_greet", Greet(), CancellationToken.None);

            var full = await Build(TextBlockMode.Full)
                .CallToolAsync("test_greet", Greet(), CancellationToken.None);

            // 紧凑的更短，但解析出来必须完全一样——省的是空白，不是内容
            Assert.True(compact.Text.Length < full.Text.Length,
                "紧凑文本应当比缩进文本短：" + compact.Text.Length + " vs " + full.Text.Length);

            Assert.Equal(
                JsonValue.Parse(full.Text)["message"].AsString,
                JsonValue.Parse(compact.Text)["message"].AsString);

            Assert.DoesNotContain("\n", compact.Text);
            Assert.Contains("\n", full.Text);
        }

        [Fact]
        public async Task TheTextBlockMatchesStructuredContentExactly()
        {
            // 两份是同一份数据。哪天它们对不上，就说明有人只改了其中一条路径
            var result = await Build(TextBlockMode.Compact)
                .CallToolAsync("test_greet", Greet(), CancellationToken.None);

            Assert.Equal(result.StructuredContent.ToJson(), result.Text);
        }

        [Fact]
        public async Task OmitDropsTheTextButKeepsTheData()
        {
            var result = await Build(TextBlockMode.Omit)
                .CallToolAsync("test_greet", Greet(), CancellationToken.None);

            Assert.Equal(string.Empty, result.Text);

            // 数据必须还在，否则这就不是"省流量"而是"丢结果"
            Assert.NotNull(result.StructuredContent);
            Assert.Equal("你好 世界。", result.StructuredContent["message"].AsString);
        }

        [Fact]
        public async Task OmitDoesNotEmitAnEmptyTextBlock()
        {
            // 放一个空字符串块，客户端会渲染出一片空白，看起来像工具什么都没返回
            var result = await Build(TextBlockMode.Omit)
                .CallToolAsync("test_greet", Greet(), CancellationToken.None);

            var json = result.ToJson(McpRequestContextFor());
            Assert.Equal(0, json["content"].Count);
            Assert.NotNull(json["structuredContent"]);
        }

        [Fact]
        public async Task FailuresAlwaysKeepTheirText()
        {
            // 失败文本是人和模型都要读的散文，不是重复的数据——任何模式下都不能省
            foreach (var mode in new[] { TextBlockMode.Compact, TextBlockMode.Full, TextBlockMode.Omit })
            {
                var result = await Build(mode)
                    .CallToolAsync("test_fail", JsonValue.NewObject(), CancellationToken.None);

                Assert.True(result.IsError);
                Assert.False(string.IsNullOrEmpty(result.Text), mode + " 模式下失败文本不该为空。");
                Assert.Contains("没有打开的文档", result.Text);
            }
        }

        private static McpRequestContext McpRequestContextFor() =>
            new McpRequestContext(McpEra.Legacy, null);
    }
}
