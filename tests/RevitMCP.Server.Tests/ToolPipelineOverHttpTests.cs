using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Tooling;
using RevitMCP.Tooling.Dispatch;
using Xunit;

namespace RevitMCP.Server.Tests
{
    /// <summary>
    /// M3 的端到端验收：HTTP → JSON-RPC → MCP 分发 → 执行管线 → Schema 生成 → 工具执行，
    /// 全程走真实 socket。除了 Revit API 本身，这条链路上的每一环都被覆盖到了。
    /// </summary>
    public sealed class FakeModel
    {
        public List<string> Walls { get; } = new List<string> { "墙 W-01", "墙 W-02", "外墙 EW-01" };
    }

    internal sealed class DirectDispatcher : IWorkDispatcher<FakeModel>
    {
        private readonly FakeModel _model = new FakeModel();

        public Task<TResult> InvokeAsync<TResult>(
            Func<FakeModel, TResult> work, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(work(_model));
    }

    public sealed class FindWallsInput
    {
        [McpParam("按名称过滤（子串匹配）", Required = true)]
        public string NameContains { get; set; }

        [McpParam("最多返回多少条")]
        public int? Limit { get; set; }
    }

    public sealed class FindWallsOutput
    {
        public int Total { get; set; }
        public List<string> Names { get; set; } = new List<string>();
    }

    [McpTool("model_find_walls", Title = "查找墙", Description = "按名称查找墙体。", ReadOnly = true)]
    public sealed class FindWallsTool : McpTool<FakeModel, FindWallsInput, FindWallsOutput>
    {
        public override FindWallsOutput Execute(FindWallsInput input, ToolExecutionContext<FakeModel> context)
        {
            var matched = context.Host.Walls
                .Where(w => w.IndexOf(input.NameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            return new FindWallsOutput
            {
                Total = matched.Count,
                Names = matched.Take(input.Limit ?? 100).ToList()
            };
        }
    }

    public class ToolPipelineOverHttpTests
    {
        private static McpTestServer StartServer()
        {
            var registry = new ToolRegistry<FakeModel>();
            registry.Register(typeof(FindWallsTool));

            var pipeline = new ToolPipeline<FakeModel>(registry, new DirectDispatcher());
            return new McpTestServer(catalog: pipeline);
        }

        [Fact]
        public async Task ToolsListExposesTheGeneratedSchema()
        {
            using (var server = StartServer())
            {
                var response = await server.PostAsync(McpTestServer.LegacyRequest(1, "tools/list"));

                var tool = response.Result["tools"][0];
                Assert.Equal("model_find_walls", tool["name"].AsString);
                Assert.Equal("查找墙", tool["title"].AsString);

                var schema = tool["inputSchema"];
                Assert.Equal("object", schema["type"].AsString);
                Assert.Equal("string", schema["properties"]["nameContains"]["type"].AsString);
                Assert.Equal("按名称过滤（子串匹配）", schema["properties"]["nameContains"]["description"].AsString);
                Assert.Equal("integer", schema["properties"]["limit"]["type"].AsString);

                // 必填规则：显式 Required 进 required，可空的不进
                var required = schema["required"].Items.Select(i => i.AsString).ToArray();
                Assert.Equal(new[] { "nameContains" }, required);
            }
        }

        [Fact]
        public async Task ToolsCallReturnsStructuredContentOverHttp()
        {
            using (var server = StartServer())
            {
                var response = await server.PostAsync(McpTestServer.LegacyRequest(1, "tools/call",
                    JsonValue.NewObject()
                        .Set("name", "model_find_walls")
                        .Set("arguments", JsonValue.NewObject().Set("nameContains", "墙"))));

                Assert.Equal(200, response.Status);
                Assert.False(response.Result["isError"].AsBool);

                var structured = response.Result["structuredContent"];
                Assert.Equal(3, structured["total"].AsInt64);
                Assert.Equal(3, structured["names"].Count);

                // 规范建议：结构化内容之外也给一份序列化文本
                Assert.Contains("W-01", response.Result["content"][0]["text"].AsString);
            }
        }

        [Fact]
        public async Task BadArgumentsComeBackAsIsErrorWithAUsefulMessage()
        {
            using (var server = StartServer())
            {
                var response = await server.PostAsync(McpTestServer.LegacyRequest(1, "tools/call",
                    JsonValue.NewObject()
                        .Set("name", "model_find_walls")
                        .Set("arguments", JsonValue.NewObject().Set("nmaeContains", "墙"))));

                // 参数错误不是传输故障：必须以 isError 形式回去，模型才能改对再来
                Assert.Equal(200, response.Status);
                Assert.Null(response.Json["error"]);
                Assert.True(response.Result["isError"].AsBool);

                var text = response.Result["content"][0]["text"].AsString;
                Assert.Contains(McpDomainError.InvalidParameter, text);
                Assert.Contains("nmaeContains", text);
                Assert.Contains("nameContains", text);   // 带出正确拼写
            }
        }

        [Fact]
        public async Task ModernEraToolCallWorksThroughThePipeline()
        {
            using (var server = StartServer())
            {
                var body = McpTestServer.ModernRequest(1, "tools/call",
                    JsonValue.NewObject()
                        .Set("name", "model_find_walls")
                        .Set("arguments", JsonValue.NewObject().Set("nameContains", "外")));

                var response = await server.PostModernAsync(body, "tools/call", name: "model_find_walls");

                Assert.Equal(200, response.Status);
                Assert.Equal("complete", response.Result["resultType"].AsString);
                Assert.Equal(1, response.Result["structuredContent"]["total"].AsInt64);
            }
        }
    }
}
