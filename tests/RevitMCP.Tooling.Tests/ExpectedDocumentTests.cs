using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Tooling;
using RevitMCP.Tooling.Schema;
using Xunit;

namespace RevitMCP.Tooling.Tests
{
    /// <summary>
    /// 写操作的目标文档前置条件。
    ///
    /// 这条机制补的是一个能悄悄毁掉别人模型的空档：写工具一律作用于**活动文档**，
    /// 而活动文档会在两次调用之间变——用户点了另一个窗口，或者上一步的另存
    /// 把当前文件换成了新文件。此前唯一的防护是**事后**附加一句"文档换了"，
    /// 那时候东西已经改完了。
    ///
    /// 所以核对必须发生在主线程上、执行之前，且不匹配就一个字节都不动。
    /// </summary>
    public class ExpectedDocumentTests
    {
        private sealed class Switcher
        {
            public string Key = "C:\\项目\\A.rvt";
            public string Label = "A";

            public ContextIdentity Describe(FakeHost host)
            {
                return Key == null ? null : new ContextIdentity(Key, Label);
            }
        }

        private static ToolPipeline<FakeHost> Build(
            Switcher switcher, FakeHost host = null, ToolPipelineOptions options = null)
        {
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly);

            return new ToolPipeline<FakeHost>(
                registry,
                new ImmediateDispatcher(host ?? new FakeHost()),
                options ?? new ToolPipelineOptions { WriteEnabled = () => true },
                writeScope: null,
                contextIdentity: switcher.Describe);
        }

        private static JsonValue Mutate(string expected = null)
        {
            var arguments = JsonValue.NewObject().Set("title", "新标题");
            if (expected != null) arguments.Set(SchemaGenerator.ExpectedDocumentParameter, expected);
            return arguments;
        }

        [Fact]
        public async Task MatchingDocumentProceeds()
        {
            var host = new FakeHost();
            var pipeline = Build(new Switcher(), host);

            var result = await pipeline.CallToolAsync(
                "test_mutate", Mutate("C:\\项目\\A.rvt"), CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Equal("新标题", host.DocumentTitle);
        }

        [Fact]
        public async Task TitleAlsoCounts()
        {
            // 模型手里常常只有标题（另存/切换的回执里给的就是它），
            // 为此逼它再查一次 list_documents 没有意义
            var pipeline = Build(new Switcher());

            var result = await pipeline.CallToolAsync("test_mutate", Mutate("A"), CancellationToken.None);

            Assert.False(result.IsError);
        }

        [Fact]
        public async Task WrongDocumentIsRejectedAndNothingIsWritten()
        {
            var host = new FakeHost { DocumentTitle = "原样" };
            var pipeline = Build(new Switcher(), host);

            var result = await pipeline.CallToolAsync(
                "test_mutate", Mutate("C:\\项目\\B.rvt"), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains(McpDomainError.WrongDocument, result.Text);
            Assert.Contains("B.rvt", result.Text);
            Assert.Contains("A", result.Text);

            // 关键断言：被拒绝的写入没有碰过任何东西
            Assert.Equal("原样", host.DocumentTitle);
        }

        [Fact]
        public async Task SwitchingBetweenCallsIsCaughtBeforeWriting()
        {
            // 真实场景：查完 A 的构件 ID，用户切到了 B，然后模型拿着 A 的 ID 来写
            var switcher = new Switcher();
            var host = new FakeHost { DocumentTitle = "原样" };
            var pipeline = Build(switcher, host);

            await pipeline.CallToolAsync("test_greet", JsonValue.NewObject().Set("name", "x"),
                CancellationToken.None);

            switcher.Key = "C:\\项目\\B.rvt";
            switcher.Label = "B";

            var result = await pipeline.CallToolAsync(
                "test_mutate", Mutate("C:\\项目\\A.rvt"), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Equal("原样", host.DocumentTitle);
        }

        [Fact]
        public async Task OmittingItKeepsTheOldBehaviour()
        {
            // 这是一个前置条件，不是必填项：不给就按原来那样作用在活动文档上
            var host = new FakeHost();
            var pipeline = Build(new Switcher(), host);

            var result = await pipeline.CallToolAsync("test_mutate", Mutate(), CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Equal("新标题", host.DocumentTitle);
        }

        [Fact]
        public async Task UnknownIdentityRefusesRatherThanGuesses()
        {
            var host = new FakeHost { DocumentTitle = "原样" };
            var pipeline = Build(new Switcher { Key = null }, host);

            var result = await pipeline.CallToolAsync(
                "test_mutate", Mutate("C:\\项目\\A.rvt"), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains(McpDomainError.WrongDocument, result.Text);
            Assert.Equal("原样", host.DocumentTitle);
        }

        [Fact]
        public async Task ReadOnlyToolsDoNotTakeIt()
        {
            // 只读工具不需要这个前置条件，也就不该默默接受它——
            // 悄悄忽略一个参数，会让调用方以为自己加了一道保护
            var pipeline = Build(new Switcher());

            var arguments = JsonValue.NewObject()
                .Set("name", "x")
                .Set(SchemaGenerator.ExpectedDocumentParameter, "C:\\项目\\A.rvt");

            var result = await pipeline.CallToolAsync("test_greet", arguments, CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains("未知参数", result.Text);
        }

        [Fact]
        public void WriteToolSchemasDeclareIt()
        {
            var pipeline = Build(new Switcher());
            var tools = pipeline.ListTools();

            var mutate = tools.Single(t => t.Name == "test_mutate");
            var greet = tools.Single(t => t.Name == "test_greet");

            Assert.True(HasExpectedDocument(mutate.InputSchema), "写工具的 schema 必须声明它，否则模型不知道有这个前置条件。");
            Assert.False(HasExpectedDocument(greet.InputSchema), "只读工具不收这个参数，声明了就是骗人。");
        }

        [Fact]
        public async Task WriteSwitchIsRecheckedOnTheMainThread()
        {
            // 排队期间用户把「修改模型」关掉了：入队时读到的 true 到执行时已经不作数
            var host = new FakeHost { DocumentTitle = "原样" };
            var enabled = true;

            var pipeline = Build(new Switcher(), host,
                new ToolPipelineOptions { WriteEnabled = () => enabled });

            enabled = true;
            var first = await pipeline.CallToolAsync("test_mutate", Mutate(), CancellationToken.None);
            Assert.False(first.IsError);

            host.DocumentTitle = "原样";
            enabled = false;

            var second = await pipeline.CallToolAsync("test_mutate", Mutate(), CancellationToken.None);

            Assert.True(second.IsError);
            Assert.Contains(McpDomainError.WriteDisabled, second.Text);
            Assert.Equal("原样", host.DocumentTitle);
        }

        private static bool HasExpectedDocument(JsonValue schema)
        {
            JsonValue properties;
            if (schema == null || !schema.TryGet("properties", out properties)) return false;

            JsonValue unused;
            return properties.TryGet(SchemaGenerator.ExpectedDocumentParameter, out unused);
        }
    }
}
