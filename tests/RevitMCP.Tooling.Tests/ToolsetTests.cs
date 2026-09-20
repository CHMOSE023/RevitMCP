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
    /// 工具集分组与静态启用。
    ///
    /// 目的是砍掉每个会话都要付的固定开销（74 个工具的 tools/list 约 65 KB）。
    /// 但**藏起来和关掉必须是同一件事**：只从清单里拿掉、却还能调用，
    /// 等于给调用方留一个看不见的陷阱——它在别处看到这个工具名，一调居然成功了，
    /// 而部署者以为自己关掉了它。
    /// </summary>
    public class ToolsetTests
    {
        private static ToolRegistry<FakeHost> Register(params string[] enabled)
        {
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly, null, enabled);
            return registry;
        }

        [Fact]
        public void NoToolsetsMeansEverything()
        {
            // 防回归：不配就是全开，与分组这件事出现之前完全一致
            var all = Register();
            var empty = Register(new string[0]);

            Assert.Equal(all.Tools.Count, empty.Tools.Count);
            Assert.True(all.Tools.Count > 0);
        }

        [Fact]
        public void UntaggedToolsAreCore()
        {
            // 测试程序集里的工具都没声明 Toolsets，所以它们全是 core
            var core = Register(Toolsets.Core);
            var all = Register();

            Assert.Equal(all.Tools.Count, core.Tools.Count);
        }

        [Fact]
        public void CoreSurvivesEvenIfNotAsked()
        {
            // 关掉 core 就连"有哪些能力"都问不出来了，所以它不可关闭
            var registry = Register(Toolsets.ModelingMep);

            Assert.Contains(registry.Tools, t => t.Name == "test_greet");
        }

        [Fact]
        public async Task ADisabledToolsetIsNotCallableEither()
        {
            var registry = new ToolRegistry<FakeHost>();
            registry.RegisterAssembly(typeof(GreetTool).Assembly, new[] { "test_mutate" });

            var pipeline = new ToolPipeline<FakeHost>(
                registry, new ImmediateDispatcher(new FakeHost()),
                new ToolPipelineOptions { WriteEnabled = () => true });

            // 不在清单里 → 也必须调不动
            Assert.DoesNotContain(pipeline.ListTools(), t => t.Name == "test_mutate");

            await Assert.ThrowsAsync<ToolNotFoundException>(() =>
                pipeline.CallToolAsync("test_mutate", JsonValue.NewObject().Set("title", "x"),
                    CancellationToken.None));
        }

        [Fact]
        public void MembershipDefaultsToCore()
        {
            var metadata = new McpToolAttribute("t");
            Assert.Equal(new[] { Toolsets.Core }, ToolRegistry<FakeHost>.ToolsetsOf(metadata).ToArray());
        }

        [Fact]
        public void AToolCanBelongToSeveralToolsets()
        {
            // 这套工具按几何范式组织，专业边界横切在内部：
            // 同一个线定位工具既造墙（建筑）也造梁（结构），只能同时挂两个集合
            var metadata = new McpToolAttribute("t")
            {
                Toolsets = new[] { Toolsets.ModelingArchitecture, Toolsets.ModelingStructure }
            };

            var sets = ToolRegistry<FakeHost>.ToolsetsOf(metadata);

            Assert.Contains(Toolsets.ModelingArchitecture, sets);
            Assert.Contains(Toolsets.ModelingStructure, sets);
        }
    }
}
