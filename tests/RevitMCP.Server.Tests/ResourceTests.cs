using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Protocol.JsonRpc;
using RevitMCP.Protocol.Mcp;
using Xunit;

namespace RevitMCP.Server.Tests
{
    /// <summary>
    /// 资源接口。本服务用它发布建模指引——
    /// 指引必须跟着服务走，不能指望每个客户端先手工装一份文件：
    /// 装漏了没人发现，装旧了 Agent 会对着已经修好的缺陷执行补救动作。
    /// </summary>
    public class ResourceTests
    {
        private sealed class FakeCatalog : IResourceCatalog
        {
            public IReadOnlyList<ResourceDefinition> ListResources() => new[]
            {
                new ResourceDefinition("revitmcp://guide/overview", "建模指引 · 总则", "开工前先读这一份。"),
                new ResourceDefinition("revitmcp://guide/validation", "建模指引 · 验收", "建完一类构件读它。")
            };

            public ResourceContents ReadResource(string uri) =>
                uri == "revitmcp://guide/overview"
                    ? new ResourceContents(uri, "# 建模总则\n先确认目标文档。")
                    : null;
        }

        private static McpTestServer WithResources() =>
            new McpTestServer(catalog: null, resources: new FakeCatalog());

        [Fact]
        public async Task ListReturnsResources()
        {
            using (var server = WithResources())
            {
                var response = await server.PostAsync(McpTestServer.LegacyRequest(1, "resources/list"));

                Assert.Equal(200, response.Status);

                var resources = response.Result["resources"];
                Assert.Equal(2, resources.Count);
                Assert.Equal("revitmcp://guide/overview", resources.Items.First()["uri"].AsString);

                // description 决定它会不会被读，不能丢
                Assert.False(string.IsNullOrEmpty(resources.Items.First()["description"].AsString));
            }
        }

        [Fact]
        public async Task ReadReturnsContent()
        {
            using (var server = WithResources())
            {
                var parameters = JsonValue.NewObject().Set("uri", "revitmcp://guide/overview");
                var response = await server.PostAsync(
                    McpTestServer.LegacyRequest(1, "resources/read", parameters));

                Assert.Equal(200, response.Status);

                var contents = response.Result["contents"];
                Assert.Equal(1, contents.Count);
                Assert.Contains("先确认目标文档", contents.Items.First()["text"].AsString);
                Assert.Equal("text/markdown", contents.Items.First()["mimeType"].AsString);
            }
        }

        [Fact]
        public async Task UnknownUriListsTheKnownOnes()
        {
            // 光说"找不到"会让调用方去猜拼写
            using (var server = WithResources())
            {
                var parameters = JsonValue.NewObject().Set("uri", "revitmcp://guide/nope");
                var response = await server.PostAsync(
                    McpTestServer.LegacyRequest(1, "resources/read", parameters));

                Assert.Equal(JsonRpcErrorCodes.InvalidParams, response.ErrorCode);
                Assert.Contains("revitmcp://guide/overview", response.Error["message"].AsString);
            }
        }

        [Fact]
        public async Task CapabilityIsAdvertised()
        {
            using (var server = WithResources())
            {
                var response = await server.PostAsync(McpTestServer.LegacyRequest(1, "initialize"));

                Assert.Equal(200, response.Status);
                Assert.NotNull(response.Result["capabilities"]["resources"]);
            }
        }

        private sealed class ExplodingCatalog : IResourceCatalog
        {
            public IReadOnlyList<ResourceDefinition> ListResources() =>
                throw new InvalidOperationException("资源读不出来");

            public ResourceContents ReadResource(string uri) =>
                throw new InvalidOperationException("资源读不出来");
        }

        [Fact]
        public async Task ABrokenCatalogDoesNotTakeDownTheServer()
        {
            // 资源是锦上添花，不能是单点故障。
            // IsKnownMethod 在传输层是在 try/catch **之外**跑的——
            // 目录一抛异常，ping 和 tools/call 也会跟着变成 500
            using (var server = new McpTestServer(catalog: null, resources: new ExplodingCatalog()))
            {
                var ping = await server.PostAsync(McpTestServer.LegacyRequest(1, "ping"));
                Assert.Equal(200, ping.Status);

                var tools = await server.PostAsync(McpTestServer.LegacyRequest(2, "tools/list"));
                Assert.Equal(200, tools.Status);

                // 坏掉的目录等同于"没有资源"：不声明能力，方法也不存在
                var initialize = await server.PostAsync(McpTestServer.LegacyRequest(3, "initialize"));
                Assert.Null(initialize.Result["capabilities"]["resources"]);

                var list = await server.PostAsync(McpTestServer.LegacyRequest(4, "resources/list"));
                Assert.Equal(JsonRpcErrorCodes.MethodNotFound, list.ErrorCode);
            }
        }

        [Fact]
        public async Task WithoutResourcesTheMethodDoesNotExist()
        {
            // 没有资源可发布时不声明这份能力，也不接受 resources/*——
            // 声明了却返回空，客户端会以为是自己问错了
            using (var server = new McpTestServer())
            {
                var initialize = await server.PostAsync(McpTestServer.LegacyRequest(1, "initialize"));
                Assert.Null(initialize.Result["capabilities"]["resources"]);

                var list = await server.PostAsync(McpTestServer.LegacyRequest(2, "resources/list"));
                Assert.Equal(JsonRpcErrorCodes.MethodNotFound, list.ErrorCode);
            }
        }
    }
}
