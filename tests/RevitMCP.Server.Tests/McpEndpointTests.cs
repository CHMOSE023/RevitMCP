using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Protocol.JsonRpc;
using RevitMCP.Protocol.Mcp;
using Xunit;

namespace RevitMCP.Server.Tests
{
    /// <summary>legacy（2025-11-25 及更早）：initialize 握手建立会话。</summary>
    public class LegacyEraTests
    {
        [Fact]
        public async Task InitializeReturnsNegotiatedVersionAndServerInfo()
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostAsync(McpTestServer.LegacyRequest(1, "initialize",
                    JsonValue.NewObject()
                        .Set("protocolVersion", McpProtocol.Legacy20250618)
                        .Set("capabilities", JsonValue.NewObject())
                        .Set("clientInfo", JsonValue.NewObject().Set("name", "Test").Set("version", "1.0"))));

                Assert.Equal(200, response.Status);
                var result = response.Result;
                Assert.Equal(McpProtocol.Legacy20250618, result["protocolVersion"].AsString);
                Assert.Equal("RevitMCP", result["serverInfo"]["name"].AsString);
                Assert.NotNull(result["capabilities"]["tools"]);

                // legacy 客户端不认识 resultType，不能出现在响应里
                Assert.False(result.ContainsKey("resultType"));
            }
        }

        [Fact]
        public async Task InitializeFallsBackWhenRequestedVersionIsUnknown()
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostAsync(McpTestServer.LegacyRequest(1, "initialize",
                    JsonValue.NewObject().Set("protocolVersion", "1999-01-01")));

                // legacy 握手没有"报错让客户端重试"的机制，只能给出我们支持的版本
                Assert.Equal(McpProtocol.PreferredLegacyVersion, response.Result["protocolVersion"].AsString);
            }
        }

        [Fact]
        public async Task InitializedNotificationIsAccepted()
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostAsync(
                    McpTestServer.LegacyRequest(null, "notifications/initialized"));

                Assert.Equal(202, response.Status);
                Assert.Equal(string.Empty, response.Body);
            }
        }

        [Fact]
        public async Task PingAndToolsListWork()
        {
            using (var server = new McpTestServer())
            {
                var ping = await server.PostAsync(McpTestServer.LegacyRequest(1, "ping"));
                Assert.Equal(200, ping.Status);
                Assert.NotNull(ping.Result);

                var list = await server.PostAsync(McpTestServer.LegacyRequest(2, "tools/list"));
                Assert.Equal(0, list.Result["tools"].Count);
                Assert.False(list.Result.ContainsKey("resultType"));
            }
        }

        [Fact]
        public async Task UnknownMethodReturnsHttp200WithJsonRpcError()
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostAsync(McpTestServer.LegacyRequest(1, "no/such/method"));

                // legacy era 沿用传统做法：HTTP 200，错误在 JSON-RPC 层
                Assert.Equal(200, response.Status);
                Assert.Equal(JsonRpcErrorCodes.MethodNotFound, response.ErrorCode);
            }
        }

        [Fact]
        public async Task ServerDiscoverIsNotAvailableInLegacyEra()
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostAsync(McpTestServer.LegacyRequest(1, "server/discover"));
                Assert.Equal(JsonRpcErrorCodes.MethodNotFound, response.ErrorCode);
            }
        }
    }

    /// <summary>modern（2026-07-28）：无状态，版本与能力随每个请求的 _meta 传递。</summary>
    public class ModernEraTests
    {
        [Fact]
        public async Task ServerDiscoverListsSupportedVersionsAndCapabilities()
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostModernAsync(
                    McpTestServer.ModernRequest("d1", "server/discover"), "server/discover");

                Assert.Equal(200, response.Status);
                var result = response.Result;
                Assert.Equal("complete", result["resultType"].AsString);

                var versions = result["supportedVersions"];
                Assert.Equal(McpProtocol.Modern20260728, versions[0].AsString);

                Assert.NotNull(result["capabilities"]["tools"]);
                Assert.Equal("RevitMCP", result["_meta"][McpProtocol.MetaServerInfo]["name"].AsString);
            }
        }

        [Fact]
        public async Task PingCarriesResultTypeComplete()
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostModernAsync(McpTestServer.ModernRequest(1, "ping"), "ping");

                Assert.Equal(200, response.Status);
                Assert.Equal("complete", response.Result["resultType"].AsString);
            }
        }

        [Fact]
        public async Task InitializeIsNotAvailableInModernEra()
        {
            using (var server = new McpTestServer())
            {
                // 带 modern _meta 却调 initialize：该方法在本代已不存在
                var response = await server.PostModernAsync(
                    McpTestServer.ModernRequest(1, "initialize"), "initialize");

                Assert.Equal(404, response.Status);
                Assert.Equal(JsonRpcErrorCodes.MethodNotFound, response.ErrorCode);
            }
        }

        [Fact]
        public async Task UnknownMethodReturnsHttp404SoClientsCanTellUsApart()
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostModernAsync(
                    McpTestServer.ModernRequest(1, "no/such/method"), "no/such/method");

                // 规范要求 404 + -32601：与"这台服务器压根没有 MCP 端点"的裸 404 区分开
                Assert.Equal(404, response.Status);
                Assert.Equal(JsonRpcErrorCodes.MethodNotFound, response.ErrorCode);
            }
        }

        [Fact]
        public async Task UnsupportedVersionReturnsSupportedList()
        {
            using (var server = new McpTestServer())
            {
                var body = McpTestServer.ModernRequest(1, "ping", version: "1900-01-01");
                var response = await server.PostRawAsync(body.ToJson(), request =>
                {
                    request.Headers.TryAddWithoutValidation(McpProtocol.HeaderProtocolVersion, "1900-01-01");
                    request.Headers.TryAddWithoutValidation(McpProtocol.HeaderMethod, "ping");
                });

                Assert.Equal(400, response.Status);
                Assert.Equal(JsonRpcErrorCodes.UnsupportedProtocolVersion, response.ErrorCode);

                var data = response.Error["data"];
                Assert.Equal("1900-01-01", data["requested"].AsString);
                Assert.Equal(McpProtocol.Modern20260728, data["supported"][0].AsString);
            }
        }

        [Fact]
        public async Task MissingProtocolVersionHeaderIsRejected()
        {
            using (var server = new McpTestServer())
            {
                // 消息体声明了 modern，但没有镜像到 HTTP 头
                var response = await server.PostRawAsync(
                    McpTestServer.ModernRequest(1, "ping").ToJson(),
                    request => request.Headers.TryAddWithoutValidation(McpProtocol.HeaderMethod, "ping"));

                Assert.Equal(400, response.Status);
                Assert.Equal(JsonRpcErrorCodes.HeaderMismatch, response.ErrorCode);
            }
        }

        [Fact]
        public async Task MethodHeaderMismatchIsRejected()
        {
            using (var server = new McpTestServer())
            {
                // 中间件按头路由、服务端按体执行，两边不一致就是安全漏洞
                var response = await server.PostModernAsync(
                    McpTestServer.ModernRequest(1, "ping"), method: "tools/list");

                Assert.Equal(400, response.Status);
                Assert.Equal(JsonRpcErrorCodes.HeaderMismatch, response.ErrorCode);
                Assert.Contains("Mcp-Method", response.Error["message"].AsString);
            }
        }

        [Fact]
        public async Task ToolsCallRequiresMatchingNameHeader()
        {
            using (var server = new McpTestServer(catalog: new FakeToolCatalog()))
            {
                var body = McpTestServer.ModernRequest(1, "tools/call",
                    JsonValue.NewObject()
                        .Set("name", "echo")
                        .Set("arguments", JsonValue.NewObject().Set("text", "hi")));

                var mismatched = await server.PostModernAsync(body, "tools/call", name: "other");
                Assert.Equal(JsonRpcErrorCodes.HeaderMismatch, mismatched.ErrorCode);

                var ok = await server.PostModernAsync(body, "tools/call", name: "echo");
                Assert.Equal(200, ok.Status);
                Assert.Equal("hi", ok.Result["content"][0]["text"].AsString);
            }
        }

        [Fact]
        public async Task Base64SentinelNameHeaderIsDecodedBeforeComparing()
        {
            using (var server = new McpTestServer(catalog: new FakeToolCatalog()))
            {
                var body = McpTestServer.ModernRequest(1, "tools/call",
                    JsonValue.NewObject()
                        .Set("name", "echo")
                        .Set("arguments", JsonValue.NewObject().Set("text", "hi")));

                var encoded = "=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("echo")) + "?=";
                var response = await server.PostModernAsync(body, "tools/call", name: encoded);

                Assert.Equal(200, response.Status);
            }
        }
    }

    public class ToolTests
    {
        [Fact]
        public async Task ToolsListReturnsDefinitionWithSchema()
        {
            using (var server = new McpTestServer(catalog: new FakeToolCatalog()))
            {
                var response = await server.PostAsync(McpTestServer.LegacyRequest(1, "tools/list"));

                var tool = response.Result["tools"][0];
                Assert.Equal("echo", tool["name"].AsString);
                Assert.Equal("回声", tool["title"].AsString);
                Assert.Equal("object", tool["inputSchema"]["type"].AsString);
            }
        }

        [Fact]
        public async Task ToolExecutionFailureIsReportedAsIsErrorNotJsonRpcError()
        {
            using (var server = new McpTestServer(catalog: new FakeToolCatalog()))
            {
                // 执行失败要让模型看见并自我纠正，包成 JSON-RPC 错误客户端会当成传输故障
                var response = await server.PostAsync(McpTestServer.LegacyRequest(1, "tools/call",
                    JsonValue.NewObject().Set("name", "echo").Set("arguments", JsonValue.NewObject())));

                Assert.Equal(200, response.Status);
                Assert.Null(response.Json["error"]);
                Assert.True(response.Result["isError"].AsBool);
            }
        }

        [Fact]
        public async Task UnknownToolIsAJsonRpcError()
        {
            using (var server = new McpTestServer(catalog: new FakeToolCatalog()))
            {
                // "工具不存在"是请求结构问题，模型很难自我纠正
                var response = await server.PostAsync(McpTestServer.LegacyRequest(1, "tools/call",
                    JsonValue.NewObject().Set("name", "nope")));

                Assert.Equal(JsonRpcErrorCodes.InvalidParams, response.ErrorCode);
            }
        }

        [Fact]
        public async Task ToolsCallWithoutNameIsInvalidParams()
        {
            using (var server = new McpTestServer(catalog: new FakeToolCatalog()))
            {
                var response = await server.PostAsync(
                    McpTestServer.LegacyRequest(1, "tools/call", JsonValue.NewObject()));

                Assert.Equal(JsonRpcErrorCodes.InvalidParams, response.ErrorCode);
            }
        }
    }

    public class SecurityTests
    {
        [Fact]
        public async Task MissingTokenIsUnauthorized()
        {
            using (var server = new McpTestServer())
            {
                using (var client = new HttpClient())
                {
                    var response = await client.PostAsync(server.Endpoint,
                        new StringContent("{}", Encoding.UTF8, "application/json"));
                    Assert.Equal(401, (int)response.StatusCode);
                    Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
                }
            }
        }

        [Fact]
        public async Task WrongTokenIsUnauthorized()
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostRawAsync(
                    McpTestServer.LegacyRequest(1, "ping").ToJson(),
                    request =>
                    {
                        request.Headers.Remove("Authorization");
                        request.Headers.TryAddWithoutValidation("Authorization", "Bearer wrong");
                    });

                Assert.Equal(401, response.Status);
            }
        }

        [Fact]
        public async Task DisallowedOriginIsForbidden()
        {
            using (var server = new McpTestServer())
            {
                // DNS rebinding：恶意网页把域名解析到 127.0.0.1，从浏览器直接打本地服务
                var response = await server.PostRawAsync(
                    McpTestServer.LegacyRequest(1, "ping").ToJson(),
                    request => request.Headers.TryAddWithoutValidation("Origin", "https://evil.example.com"));

                Assert.Equal(403, response.Status);
            }
        }

        [Fact]
        public async Task AllowedOriginPasses()
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostRawAsync(
                    McpTestServer.LegacyRequest(1, "ping").ToJson(),
                    request => request.Headers.TryAddWithoutValidation("Origin", "https://claude.ai"));

                Assert.Equal(200, response.Status);
            }
        }

        [Theory]
        // 前缀匹配时代的漏网之鱼：这些字符串都以某个允许项开头，却是完全不同的域名
        [InlineData("https://claude.ai.evil.example")]
        [InlineData("https://claude.ainode.example")]
        [InlineData("http://localhost.evil.example")]
        // scheme 不同就是不同的来源
        [InlineData("http://claude.ai")]
        // 端口：非本机来源必须完全一致
        [InlineData("https://claude.ai:8443")]
        // 带用户信息的写法不能被当成同一个 host
        [InlineData("https://claude.ai@evil.example")]
        // 浏览器对沙箱/不透明来源写 null，它不是一个可比较的 origin
        [InlineData("null")]
        [InlineData("not a url")]
        public async Task LookAlikeOriginsAreRejected(string origin)
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostRawAsync(
                    McpTestServer.LegacyRequest(1, "ping").ToJson(),
                    request => request.Headers.TryAddWithoutValidation("Origin", origin));

                Assert.Equal(403, response.Status);
            }
        }

        [Theory]
        // 允许项写的是 http://localhost（无端口），本机开发服务器的端口天天在变
        [InlineData("http://localhost:3000")]
        [InlineData("http://localhost")]
        // 大小写不敏感
        [InlineData("https://CLAUDE.ai")]
        public async Task EquivalentOriginsPass(string origin)
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostRawAsync(
                    McpTestServer.LegacyRequest(1, "ping").ToJson(),
                    request => request.Headers.TryAddWithoutValidation("Origin", origin));

                Assert.Equal(200, response.Status);
            }
        }

        [Fact]
        public async Task AbsentOriginPassesBecauseNativeClientsDoNotSendIt()
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostAsync(McpTestServer.LegacyRequest(1, "ping"));
                Assert.Equal(200, response.Status);
            }
        }

        [Fact]
        public async Task ServerBindsLoopbackOnly()
        {
            using (var server = new McpTestServer())
            {
                // 绑定在 127.0.0.1 上时，连本机的非回环地址应当连不上
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(GetNonLoopbackAddress(), server.Port);
                    var finished = await Task.WhenAny(connect, Task.Delay(1500));
                    if (finished == connect)
                        Assert.True(connect.IsFaulted, "服务不应在非回环地址上可达。");
                }
            }
        }

        private static string GetNonLoopbackAddress()
        {
            foreach (var address in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork &&
                    !System.Net.IPAddress.IsLoopback(address))
                    return address.ToString();
            }
            return "127.0.0.2";   // 没有外部网卡时的退路，同样不是我们绑定的地址
        }
    }

    public class HttpLayerTests
    {
        [Fact]
        public async Task GetOnEndpointIsMethodNotAllowed()
        {
            using (var server = new McpTestServer())
            {
                // 2026-07-28 移除了 GET SSE 流；对老客户端的 GET 规范要求回 405
                var response = await server.SendAsync(HttpMethod.Get, "mcp");

                Assert.Equal(405, response.Status);
                // Allow 在 .NET 中归类为 content header，不在 HttpResponseMessage.Headers 里
                Assert.Contains("POST", response.Content.Allow);
            }
        }

        [Fact]
        public async Task DeleteOnEndpointIsMethodNotAllowed()
        {
            using (var server = new McpTestServer())
            {
                // 会话已被移除，DELETE 不再用于结束会话
                var response = await server.SendAsync(HttpMethod.Delete, "mcp");
                Assert.Equal(405, response.Status);
            }
        }

        [Fact]
        public async Task UnknownPathIsNotFound()
        {
            using (var server = new McpTestServer())
            {
                var response = await server.SendAsync(HttpMethod.Get, "somewhere-else");
                Assert.Equal(404, response.Status);
            }
        }

        [Fact]
        public async Task MalformedJsonIsParseError()
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostRawAsync("{not json");

                Assert.Equal(400, response.Status);
                Assert.Equal(JsonRpcErrorCodes.ParseError, response.ErrorCode);
            }
        }

        [Fact]
        public async Task BatchRequestIsRejected()
        {
            using (var server = new McpTestServer())
            {
                // JSON-RPC 批量请求已从规范移除
                var response = await server.PostRawAsync("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}]");

                Assert.Equal(400, response.Status);
                Assert.Equal(JsonRpcErrorCodes.InvalidRequest, response.ErrorCode);
            }
        }

        [Fact]
        public async Task MissingJsonRpcVersionIsInvalidRequest()
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostRawAsync("{\"id\":1,\"method\":\"ping\"}");
                Assert.Equal(JsonRpcErrorCodes.InvalidRequest, response.ErrorCode);
            }
        }

        [Fact]
        public async Task StringIdIsEchoedBackWithOriginalType()
        {
            using (var server = new McpTestServer())
            {
                var response = await server.PostAsync(McpTestServer.LegacyRequest("abc", "ping"));
                Assert.Equal("abc", response.Json["id"].AsString);
            }
        }

        [Fact]
        public async Task LargeIntegerIdSurvivesRoundTrip()
        {
            using (var server = new McpTestServer())
            {
                const long big = 9007199254740993L;   // 2^53 + 1
                var response = await server.PostAsync(McpTestServer.LegacyRequest(big, "ping"));
                Assert.Equal(big, response.Json["id"].AsInt64);
            }
        }

        [Fact]
        public async Task KeepAliveServesMultipleRequestsOnOneConnection()
        {
            using (var server = new McpTestServer())
            {
                // HttpClient 默认复用连接，连续请求走的是同一个 socket
                for (var i = 1; i <= 5; i++)
                {
                    var response = await server.PostAsync(McpTestServer.LegacyRequest(i, "ping"));
                    Assert.Equal(200, response.Status);
                    Assert.Equal(i, response.Json["id"].AsInt64);
                }
            }
        }

        [Fact]
        public async Task UnicodeBodySurvivesUtf8RoundTrip()
        {
            using (var server = new McpTestServer(catalog: new FakeToolCatalog()))
            {
                const string text = "墙体 W-01 · 高度 3000mm 😀";
                var response = await server.PostAsync(McpTestServer.LegacyRequest(1, "tools/call",
                    JsonValue.NewObject()
                        .Set("name", "echo")
                        .Set("arguments", JsonValue.NewObject().Set("text", text))));

                Assert.Equal(text, response.Result["content"][0]["text"].AsString);
            }
        }

        [Fact]
        public async Task GarbageRequestLineIsRejectedWithoutKillingTheServer()
        {
            using (var server = new McpTestServer())
            {
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync("127.0.0.1", server.Port);
                    var junk = Encoding.ASCII.GetBytes("这不是HTTP\r\n\r\n");
                    await client.GetStream().WriteAsync(junk, 0, junk.Length);

                    var buffer = new byte[256];
                    var read = await client.GetStream().ReadAsync(buffer, 0, buffer.Length);
                    Assert.Contains("400", Encoding.ASCII.GetString(buffer, 0, read));
                }

                // 服务必须还活着
                var response = await server.PostAsync(McpTestServer.LegacyRequest(1, "ping"));
                Assert.Equal(200, response.Status);
            }
        }

        [Fact]
        public async Task ChunkedTransferEncodingIsRejected()
        {
            using (var server = new McpTestServer())
            {
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync("127.0.0.1", server.Port);
                    var request = Encoding.ASCII.GetBytes(
                        "POST /mcp HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n");
                    await client.GetStream().WriteAsync(request, 0, request.Length);

                    var buffer = new byte[256];
                    var read = await client.GetStream().ReadAsync(buffer, 0, buffer.Length);
                    Assert.Contains("411", Encoding.ASCII.GetString(buffer, 0, read));
                }
            }
        }

        [Fact]
        public void StopIsIdempotentAndReleasesThePort()
        {
            int port;
            using (var server = new McpTestServer())
            {
                port = server.Port;
            }

            // Dispose 之后端口必须能被重新绑定，否则 Revit 重启服务会一路向上飘端口
            var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
        }
    }
}
