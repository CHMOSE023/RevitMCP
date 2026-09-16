using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Protocol.Mcp;
using Xunit;

namespace RevitMCP.Server.Tests
{
    /// <summary>报若干条进度再返回，用来验证进度确实先于结果到达。</summary>
    internal sealed class ProgressingToolCatalog : IToolCatalog
    {
        private readonly ToolDefinition[] _tools =
        {
            new ToolDefinition("crunch", "慢活", "报进度然后返回。",
                JsonValue.NewObject().Set("type", "object"))
        };

        public bool LastSinkWasActive { get; private set; }

        public IReadOnlyList<ToolDefinition> ListTools() => _tools;

        public async Task<ToolCallResult> CallToolAsync(
            string name, JsonValue arguments, IProgressSink progress, CancellationToken cancellationToken)
        {
            if (name != "crunch") throw new ToolNotFoundException(name);

            LastSinkWasActive = progress.IsActive;

            var steps = arguments["steps"];
            var count = steps != null && steps.Kind == JsonKind.Number ? (int)steps.AsInt64 : 3;

            for (var i = 1; i <= count; i++)
            {
                progress.Report(i, count, "第 " + i + " 步");
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }

            var fail = arguments["fail"];
            if (fail != null && fail.Kind == JsonKind.Bool && fail.AsBool)
                return ToolCallResult.Failure("INVALID_PARAMETER: 故意失败。");

            return ToolCallResult.Ok("干完了");
        }
    }

    public sealed class ProgressStreamTests
    {
        private static JsonValue CallWithToken(JsonValue token, int steps = 3, bool fail = false)
        {
            var arguments = JsonValue.NewObject().Set("steps", steps);
            if (fail) arguments.Set("fail", true);

            var parameters = JsonValue.NewObject()
                .Set("name", "crunch")
                .Set("arguments", arguments);

            if (token != null)
                parameters.Set("_meta", JsonValue.NewObject().Set("progressToken", token));

            return McpTestServer.LegacyRequest(JsonValue.Number(1), "tools/call", parameters);
        }

        /// <summary>把 SSE 流拆成一条条 data 负载。</summary>
        private static List<JsonValue> Events(string body)
        {
            return body
                .Split(new[] { "\n" }, StringSplitOptions.None)
                .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
                .Select(line => JsonValue.Parse(line.Substring("data: ".Length)))
                .ToList();
        }

        private static bool IsProgress(JsonValue message)
        {
            var method = message["method"];
            return method != null && method.Kind == JsonKind.String &&
                   method.AsString == "notifications/progress";
        }

        // ---------- 何时走流 ----------

        [Fact]
        public async Task ProgressTokenTurnsTheResponseIntoAnEventStream()
        {
            using (var server = new McpTestServer(catalog: new ProgressingToolCatalog()))
            {
                var response = await server.PostAsync(CallWithToken(JsonValue.String("tok-1")));

                Assert.Equal(200, response.Status);
                Assert.Equal("text/event-stream", response.Content.ContentType.MediaType);
            }
        }

        [Fact]
        public async Task WithoutAProgressTokenItStaysPlainJson()
        {
            using (var server = new McpTestServer(catalog: new ProgressingToolCatalog()))
            {
                var response = await server.PostAsync(CallWithToken(null));

                // 没人要进度就不该多开一条流——规范把这个决定权交给客户端
                Assert.Equal("application/json", response.Content.ContentType.MediaType);
                Assert.Equal("干完了", response.Result["content"][0]["text"].AsString);
            }
        }

        [Fact]
        public async Task AClientThatDoesNotAcceptEventStreamGetsJson()
        {
            using (var server = new McpTestServer(catalog: new ProgressingToolCatalog()))
            {
                var response = await server.PostAsync(CallWithToken(JsonValue.String("tok-2")), request =>
                {
                    request.Headers.Remove("Accept");
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                });

                // 给了 token 却不收 SSE，只能退回单响应——发一条它读不了的流更糟
                Assert.Equal("application/json", response.Content.ContentType.MediaType);
            }
        }

        [Fact]
        public async Task ProgressTokenOnANonToolCallIsIgnored()
        {
            using (var server = new McpTestServer(catalog: new ProgressingToolCatalog()))
            {
                var parameters = JsonValue.NewObject()
                    .Set("_meta", JsonValue.NewObject().Set("progressToken", JsonValue.String("tok-3")));

                var response = await server.PostAsync(
                    McpTestServer.LegacyRequest(JsonValue.Number(1), "tools/list", parameters));

                // tools/list 瞬间就返回了，没有进度可报
                Assert.Equal("application/json", response.Content.ContentType.MediaType);
            }
        }

        // ---------- 流里有什么 ----------

        [Fact]
        public async Task ProgressNotificationsArriveBeforeTheResult()
        {
            using (var server = new McpTestServer(catalog: new ProgressingToolCatalog()))
            {
                var response = await server.PostAsync(CallWithToken(JsonValue.String("tok-4"), steps: 4));
                var events = Events(response.Body);

                Assert.Equal(5, events.Count);                       // 4 条进度 + 1 条结果
                Assert.All(events.Take(4), e => Assert.True(IsProgress(e)));
                Assert.False(IsProgress(events[4]));

                // 结果必须是最后一条：客户端读到它就认为这次调用结束了
                Assert.Equal("干完了", events[4]["result"]["content"][0]["text"].AsString);
            }
        }

        [Fact]
        public async Task ProgressCarriesTheTokenBackVerbatim()
        {
            using (var server = new McpTestServer(catalog: new ProgressingToolCatalog()))
            {
                var response = await server.PostAsync(CallWithToken(JsonValue.String("tok-5"), steps: 2));
                var progress = Events(response.Body).Where(IsProgress).ToList();

                // 客户端靠 token 把进度对上是哪次调用，回传必须一字不差
                Assert.All(progress, e =>
                    Assert.Equal("tok-5", e["params"]["progressToken"].AsString));
            }
        }

        [Fact]
        public async Task NumericTokensSurviveToo()
        {
            using (var server = new McpTestServer(catalog: new ProgressingToolCatalog()))
            {
                var response = await server.PostAsync(CallWithToken(JsonValue.Number(42), steps: 1));
                var progress = Events(response.Body).First(IsProgress);

                // 规范允许 token 是字符串或数字，别把数字悄悄变成字符串
                Assert.Equal(JsonKind.Number, progress["params"]["progressToken"].Kind);
                Assert.Equal(42, progress["params"]["progressToken"].AsInt64);
            }
        }

        [Fact]
        public async Task ProgressCarriesCountsAndMessage()
        {
            using (var server = new McpTestServer(catalog: new ProgressingToolCatalog()))
            {
                var response = await server.PostAsync(CallWithToken(JsonValue.String("tok-6"), steps: 3));
                var first = Events(response.Body).First(IsProgress)["params"];

                Assert.Equal(1, first["progress"].AsInt64);
                Assert.Equal(3, first["total"].AsInt64);
                Assert.Equal("第 1 步", first["message"].AsString);
            }
        }

        [Fact]
        public async Task ToolFailureStillEndsTheStreamWithAResult()
        {
            using (var server = new McpTestServer(catalog: new ProgressingToolCatalog()))
            {
                var response = await server.PostAsync(
                    CallWithToken(JsonValue.String("tok-7"), steps: 2, fail: true));

                var events = Events(response.Body);
                var last = events.Last();

                // 工具失败仍是一次成功的调用：isError 在结果里，不是传输层的事
                Assert.Equal(200, response.Status);
                Assert.True(last["result"]["isError"].AsBool);
                Assert.Contains("故意失败", last["result"]["content"][0]["text"].AsString);
            }
        }

        [Fact]
        public async Task ToolSeesAnActiveSinkOnlyWhenStreaming()
        {
            using (var server = new McpTestServer(catalog: new ProgressingToolCatalog()))
            {
                var catalog = new ProgressingToolCatalog();
                using (var streaming = new McpTestServer(catalog: catalog))
                {
                    await streaming.PostAsync(CallWithToken(JsonValue.String("tok-8"), steps: 1));
                    Assert.True(catalog.LastSinkWasActive);

                    await streaming.PostAsync(CallWithToken(null, steps: 1));
                    // 没人听的时候工具应该知道，好跳过拼进度文案这类白费的活
                    Assert.False(catalog.LastSinkWasActive);
                }
            }
        }

        // ---------- 分块传输本身 ----------

        [Fact]
        public async Task TheStreamIsChunkedNotContentLengthed()
        {
            using (var server = new McpTestServer(catalog: new ProgressingToolCatalog()))
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, "mcp"))
                {
                    request.Content = new StringContent(
                        CallWithToken(JsonValue.String("tok-9")).ToJson(),
                        new System.Text.UTF8Encoding(false), "application/json");
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + McpTestServer.Token);
                    request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

                    using (var response = await server.Client.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead))
                    {
                        // 长度事先不知道，所以必须是 chunked；两者同时出现是协议错误
                        Assert.Contains("chunked", response.Headers.TransferEncoding.ToString());
                        Assert.Null(response.Content.Headers.ContentLength);
                    }
                }
            }
        }

        [Fact]
        public async Task TheConnectionStaysUsableAfterAStream()
        {
            using (var server = new McpTestServer(catalog: new ProgressingToolCatalog()))
            {
                await server.PostAsync(CallWithToken(JsonValue.String("tok-10")));

                // 用 chunked 而不是"写完就关"，图的就是这个：
                // 带进度的调用往往接二连三，不该每次都重建连接
                var next = await server.PostAsync(CallWithToken(null));
                Assert.Equal(200, next.Status);
                Assert.Equal("干完了", next.Result["content"][0]["text"].AsString);
            }
        }
    }
}
