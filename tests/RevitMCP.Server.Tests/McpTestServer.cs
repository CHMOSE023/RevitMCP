using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Protocol.Mcp;
using RevitMCP.Transport;
using RevitMCP.Transport.Http;

namespace RevitMCP.Server.Tests
{
    /// <summary>
    /// 起一个真实监听回环地址的 MCP 服务，测试通过真实 HTTP 打进来。
    /// 不用 mock：HTTP 解析本身就是被测对象之一。
    /// </summary>
    internal sealed class McpTestServer : IDisposable
    {
        private static readonly Random PortSeed = new Random();

        private readonly MiniHttpServer _http;

        public McpTestServer(Action<McpHttpOptions> configure = null, IToolCatalog catalog = null)
        {
            var options = new McpHttpOptions
            {
                Token = Token,
                AllowedOrigins = new List<string> { "http://localhost", "https://claude.ai" }
            };
            configure?.Invoke(options);

            var server = new McpServer(
                new McpServerOptions { ServerName = "RevitMCP", ServerVersion = "0.1.0" },
                catalog ?? new EmptyToolCatalog());

            _http = new MiniHttpServer(new McpHttpHandler(server, options).HandleAsync);

            // 测试类并行执行，起始端口打散以减少探测次数（真冲突时 Start 会自动向上找）
            int start;
            lock (PortSeed) start = 18000 + PortSeed.Next(0, 4000);
            Port = _http.Start(start, attempts: 200);

            Client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + Port + "/") };
        }

        public const string Token = "test-token-abc123";

        public int Port { get; }

        public HttpClient Client { get; }

        public string Endpoint => "http://127.0.0.1:" + Port + "/mcp";

        public void Dispose()
        {
            try { Client.Dispose(); } catch { }
            try { _http.Dispose(); } catch { }
        }

        // ---------- 请求构造 ----------

        public static JsonValue LegacyRequest(JsonValue id, string method, JsonValue parameters = null)
        {
            var message = JsonValue.NewObject()
                .Set("jsonrpc", "2.0")
                .Set("method", method);
            if (id != null) message.Set("id", id);
            message.Set("params", parameters ?? JsonValue.NewObject());
            return message;
        }

        public static JsonValue ModernRequest(
            JsonValue id, string method, JsonValue parameters = null, string version = McpProtocol.Modern20260728)
        {
            var p = parameters ?? JsonValue.NewObject();
            p.Set("_meta", JsonValue.NewObject()
                .Set(McpProtocol.MetaProtocolVersion, version)
                .Set(McpProtocol.MetaClientInfo, JsonValue.NewObject()
                    .Set("name", "TestClient").Set("version", "1.0.0"))
                .Set(McpProtocol.MetaClientCapabilities, JsonValue.NewObject()));

            var message = JsonValue.NewObject()
                .Set("jsonrpc", "2.0")
                .Set("method", method);
            if (id != null) message.Set("id", id);
            message.Set("params", p);
            return message;
        }

        // ---------- 发送 ----------

        public Task<HttpResult> PostAsync(JsonValue body, Action<HttpRequestMessage> customize = null) =>
            PostRawAsync(body.ToJson(), customize);

        /// <summary>modern 请求会自动补上规范要求的镜像头，便于大多数测试聚焦在别处。</summary>
        public Task<HttpResult> PostModernAsync(
            JsonValue body, string method, string name = null, Action<HttpRequestMessage> customize = null)
        {
            return PostRawAsync(body.ToJson(), request =>
            {
                request.Headers.TryAddWithoutValidation(McpProtocol.HeaderProtocolVersion, McpProtocol.Modern20260728);
                request.Headers.TryAddWithoutValidation(McpProtocol.HeaderMethod, method);
                if (name != null) request.Headers.TryAddWithoutValidation(McpProtocol.HeaderName, name);
                customize?.Invoke(request);
            });
        }

        public async Task<HttpResult> PostRawAsync(string body, Action<HttpRequestMessage> customize = null)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, "mcp"))
            {
                request.Content = new StringContent(body ?? string.Empty, new UTF8Encoding(false), "application/json");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                customize?.Invoke(request);

                using (var response = await Client.SendAsync(request, CancellationToken.None).ConfigureAwait(false))
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return new HttpResult(response, text);
                }
            }
        }

        public async Task<HttpResult> SendAsync(HttpMethod method, string path)
        {
            using (var request = new HttpRequestMessage(method, path))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
                using (var response = await Client.SendAsync(request).ConfigureAwait(false))
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return new HttpResult(response, text);
                }
            }
        }
    }

    internal sealed class HttpResult
    {
        public HttpResult(HttpResponseMessage response, string body)
        {
            Status = (int)response.StatusCode;
            Body = body;
            Headers = response.Headers;
            Content = response.Content?.Headers;
        }

        public int Status { get; }
        public string Body { get; }
        public System.Net.Http.Headers.HttpResponseHeaders Headers { get; }
        public System.Net.Http.Headers.HttpContentHeaders Content { get; }

        public JsonValue Json => JsonValue.Parse(Body);

        public JsonValue Result => Json["result"];

        public JsonValue Error => Json["error"];

        public int ErrorCode => (int)Error["code"].AsInt64;
    }

    /// <summary>用于验证 tools/list 与 tools/call 的假工具目录。</summary>
    internal sealed class FakeToolCatalog : IToolCatalog
    {
        private readonly ToolDefinition[] _tools =
        {
            new ToolDefinition("echo", "回声", "原样返回输入的 text。",
                JsonValue.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonValue.NewObject()
                        .Set("text", JsonValue.NewObject().Set("type", "string")))
                    .Set("required", JsonValue.NewArray().Add("text")))
        };

        public IReadOnlyList<ToolDefinition> ListTools() => _tools;

        public Task<ToolCallResult> CallToolAsync(string name, JsonValue arguments, CancellationToken cancellationToken)
        {
            if (name != "echo") throw new ToolNotFoundException(name);

            var text = arguments["text"];
            if (text == null || text.Kind != JsonKind.String)
                return Task.FromResult(ToolCallResult.Failure("缺少 text 参数。"));

            return Task.FromResult(ToolCallResult.Ok(text.AsString));
        }
    }
}
