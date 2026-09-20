using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Protocol.JsonRpc;
using RevitMCP.Protocol.Mcp;
using RevitMCP.Transport.Http;

namespace RevitMCP.Transport
{
    public sealed class McpHttpOptions
    {
        public string EndpointPath { get; set; } = "/mcp";

        /// <summary>Bearer 令牌。为空则不校验认证（仅建议在测试中这么用）。</summary>
        public string Token { get; set; }

        /// <summary>
        /// 允许的 Origin 前缀。规范要求校验 Origin 以防 DNS rebinding：
        /// 恶意网页可以把域名解析到 127.0.0.1，从浏览器里直接操作本地 MCP 服务。
        /// 原生客户端不带 Origin 头，那种情况放行。
        /// </summary>
        public List<string> AllowedOrigins { get; set; } = new List<string>();
    }

    /// <summary>
    /// 把 HTTP 请求翻译成 MCP 分发，并承担所有 HTTP 层的协议义务：
    /// Origin 校验、认证、era 判定、头与消息体一致性校验、状态码选择。
    ///
    /// 双 era：规范允许服务端同时服务两代客户端。判定依据是消息体里有没有
    /// modern 的 _meta.protocolVersion——带就按 2026-07-28 无状态处理，
    /// 不带（或是 initialize）就按 legacy 握手语义处理。
    /// </summary>
    public sealed class McpHttpHandler
    {
        private readonly McpServer _server;
        private readonly McpHttpOptions _options;
        private readonly Action<string, Exception> _log;

        public McpHttpHandler(McpServer server, McpHttpOptions options, Action<string, Exception> log = null)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _options = options ?? new McpHttpOptions();
            _log = log ?? ((m, e) => { });
        }

        public async Task<HttpResponse> HandleAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            // 1) Origin：规范要求 Origin 存在且非法时返回 403
            if (!IsOriginAllowed(request.Header("Origin")))
            {
                _log("拒绝了来源非法的请求：" + request.Header("Origin"), null);
                return Deny(403, "Forbidden", JsonRpcErrorCodes.InvalidRequest, "Origin 不被允许。");
            }

            // 2) 路径
            if (!string.Equals(request.Path, _options.EndpointPath, StringComparison.Ordinal))
                return HttpResponse.Text(404, "Not Found", "未知路径。MCP 端点为 " + _options.EndpointPath);

            // 3) 方法：2026-07-28 移除了 GET 流与 DELETE 会话，对老客户端的这类请求回 405
            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            {
                var response = HttpResponse.Text(405, "Method Not Allowed", "MCP 端点仅接受 POST。");
                response.Headers["Allow"] = "POST";
                return response;
            }

            // 4) 认证
            if (!IsAuthorized(request))
            {
                var response = Deny(401, "Unauthorized", JsonRpcErrorCodes.InvalidRequest, "缺少或无效的访问令牌。");
                response.Headers["WWW-Authenticate"] = "Bearer";
                response.CloseConnection = true;
                return response;
            }

            // 5) 解析 JSON
            JsonValue root;
            try
            {
                root = JsonValue.Parse(request.Body ?? string.Empty);
            }
            catch (JsonException ex)
            {
                return Deny(400, "Bad Request", JsonRpcErrorCodes.ParseError, ex.Message);
            }

            JsonRpcMessage message;
            try
            {
                message = JsonRpcMessage.Parse(root);
            }
            catch (JsonRpcParseException ex)
            {
                return Deny(400, "Bad Request", ex.Code, ex.Message);
            }

            // 6) era 判定 + 版本校验
            var context = DetectEra(message, out var declaredVersion);

            if (context.Era == McpEra.Modern)
            {
                if (!McpProtocol.IsSupported(declaredVersion))
                    return UnsupportedVersion(message.Id, declaredVersion);

                var mismatch = ValidateHeaders(request, message, declaredVersion);
                if (mismatch != null)
                    return JsonResponse(400, "Bad Request",
                        JsonRpcMessage.Error(message.Id, JsonRpcErrorCodes.HeaderMismatch, mismatch));
            }

            // 7) 未知方法：modern 下规范要求 HTTP 404 + -32601，
            //    以便与"这台服务器根本没有 MCP 端点"的 404 区分开
            if (!_server.IsKnownMethod(message.Method, context.Era) && !message.IsNotification)
            {
                var error = JsonRpcMessage.Error(message.Id,
                    JsonRpcErrorCodes.MethodNotFound, "未知方法：" + message.Method);
                return context.Era == McpEra.Modern
                    ? JsonResponse(404, "Not Found", error)
                    : JsonResponse(200, "OK", error);
            }

            // 8) 要不要流式。规范把决定权交给客户端：给了 progressToken 才发进度，
            //    没给就不发。这正好省掉"该不该用 SSE"这个判断——
            //    没人要进度时，多一条流只是徒增一次连接状态
            var progressToken = string.Equals(message.Method, "tools/call", StringComparison.Ordinal)
                ? ProgressToken.From(message.Params)
                : null;

            if (progressToken != null && AcceptsEventStream(request))
                return StreamingResponse(message, context, progressToken);

            // 9) 分发
            var result = await _server.DispatchAsync(message, context, cancellationToken).ConfigureAwait(false);

            // 通知没有响应体，规范要求 202
            if (result == null) return HttpResponse.Empty(202, "Accepted");

            return JsonResponse(200, "OK", result);
        }

        private static bool AcceptsEventStream(HttpRequest request)
        {
            var accept = request.Header("Accept");
            return accept != null &&
                   accept.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 边跑边推：进度通知一条条发，最后发 JSON-RPC 响应，然后收流。
        ///
        /// 响应头在第一个字节写出去的那一刻就定死了，此后再没有改状态码的机会——
        /// 所以认证、版本、方法存在性这些判断必须全在建流之前做完（上面第 1~7 步）。
        /// 建流之后出的任何岔子，都只能作为流里的一条错误事件发出去。
        /// </summary>
        private HttpResponse StreamingResponse(
            JsonRpcMessage message, McpRequestContext context, JsonValue progressToken)
        {
            return HttpResponse.EventStream(async (stream, cancellationToken) =>
            {
                using (var queue = new ProgressQueue(progressToken))
                {
                    // 工具在 Revit 主线程上跑，进度在这条 HTTP 线程上写，两者并行推进
                    var call = _server.DispatchAsync(message, context, cancellationToken, queue);
                    var pump = PumpProgressAsync(queue, stream, cancellationToken);

                    JsonValue result = null;

                    try
                    {
                        result = await call.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // 客户端断了，没有人再需要这条响应
                    }
                    catch (Exception ex)
                    {
                        result = JsonRpcMessage.Error(message.Id, JsonRpcErrorCodes.InternalError, ex.Message);
                    }
                    finally
                    {
                        // 无论如何都要让泵收工，否则这条连接会一直挂着
                        queue.Complete();
                    }

                    try { await pump.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }

                    if (result != null)
                        await WriteEventAsync(stream, result, cancellationToken).ConfigureAwait(false);
                }
            });
        }

        private static async Task PumpProgressAsync(
            ProgressQueue queue, ResponseStream stream, CancellationToken cancellationToken)
        {
            while (true)
            {
                var notification = await queue.TakeAsync(cancellationToken).ConfigureAwait(false);
                if (notification == null) return;   // 工具已返回且队列已排空

                await WriteEventAsync(stream, notification, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 一条 SSE 事件。JSON 必须是单行——data 行里的换行会被当成事件分隔，
        /// 所以这里绝不能用缩进过的 JSON。
        /// </summary>
        private static Task WriteEventAsync(
            ResponseStream stream, JsonValue payload, CancellationToken cancellationToken) =>
            stream.WriteAsync("data: " + payload.ToJson() + "\n\n", cancellationToken);

        /// <summary>
        /// 规范：带 modern 每请求 _meta 的按本版无状态处理；initialize 选择 legacy 语义。
        /// 注意不能只看 MCP-Protocol-Version 头——2025-06-18 起的 legacy 客户端同样会发这个头，
        /// 真正的分界是消息体里有没有 _meta 里的 protocolVersion。
        /// </summary>
        private static McpRequestContext DetectEra(JsonRpcMessage message, out string declaredVersion)
        {
            declaredVersion = null;

            var meta = message.Params["_meta"];
            if (meta != null && meta.IsObject)
            {
                var version = meta[McpProtocol.MetaProtocolVersion];
                if (version != null && version.Kind == JsonKind.String)
                {
                    declaredVersion = version.AsString;
                    return new McpRequestContext(McpEra.Modern, declaredVersion);
                }
            }

            return new McpRequestContext(McpEra.Legacy, null);
        }

        /// <summary>
        /// modern 要求把消息体字段镜像到 HTTP 头，且服务端必须校验两者一致——
        /// 否则中间件按头路由、服务端按体执行，两边看到的不是同一个请求。
        /// </summary>
        private static string ValidateHeaders(HttpRequest request, JsonRpcMessage message, string declaredVersion)
        {
            var headerVersion = request.Header(McpProtocol.HeaderProtocolVersion);
            if (string.IsNullOrEmpty(headerVersion))
                return "缺少 " + McpProtocol.HeaderProtocolVersion + " 头。";
            if (!string.Equals(headerVersion, declaredVersion, StringComparison.Ordinal))
                return "MCP-Protocol-Version 头（" + headerVersion + "）与消息体 _meta 中的版本（" + declaredVersion + "）不一致。";

            var headerMethod = request.Header(McpProtocol.HeaderMethod);
            if (string.IsNullOrEmpty(headerMethod))
                return "缺少 " + McpProtocol.HeaderMethod + " 头。";
            if (!string.Equals(headerMethod, message.Method, StringComparison.Ordinal))
                return "Mcp-Method 头（" + headerMethod + "）与消息体 method（" + message.Method + "）不一致。";

            // Mcp-Name 只对这三个方法是必需的
            string expectedName = null;
            if (message.Method == "tools/call" || message.Method == "prompts/get")
                expectedName = AsString(message.Params["name"]);
            else if (message.Method == "resources/read")
                expectedName = AsString(message.Params["uri"]);

            if (expectedName != null)
            {
                var headerName = request.Header(McpProtocol.HeaderName);
                if (string.IsNullOrEmpty(headerName))
                    return "缺少 " + McpProtocol.HeaderName + " 头。";

                // 非 ASCII 的值用 =?base64?…?= 哨兵编码传输，比较前必须解码
                if (!string.Equals(McpProtocol.DecodeHeaderValue(headerName), expectedName, StringComparison.Ordinal))
                    return "Mcp-Name 头与消息体中的名称不一致。";
            }

            return null;
        }

        private static string AsString(JsonValue value) =>
            value != null && value.Kind == JsonKind.String ? value.AsString : null;

        /// <summary>
        /// Origin 白名单。
        ///
        /// **按 URI 比，不按字符串前缀比。** 前缀匹配下，允许 <c>https://claude.ai</c>
        /// 就等于同时允许了 <c>https://claude.ai.attacker.example</c>——
        /// 那是一个完全不相干的域名，只是恰好以允许项开头。
        ///
        /// 比的是 scheme + host + port 三件套，端口只放宽一处：
        /// · 本机来源（localhost / 127.0.0.1 / ::1）且允许项没写端口时，任意端口都放行——
        ///   本地开发服务器的端口天天在变，写死一个既挡不住谁，又只会逼用户去改配置；
        /// · 其余情况端口必须一致。<c>https://claude.ai</c> 不会连带放行 <c>https://claude.ai:8443</c>。
        ///
        /// 这只是纵深防御的一层：原生客户端根本不带 Origin，真正的门是 Bearer 令牌与 Loopback 监听。
        /// </summary>
        private bool IsOriginAllowed(string origin)
        {
            // 原生客户端（Claude Code 等）不带 Origin。只有浏览器会带，那才是 DNS rebinding 的攻击面。
            if (string.IsNullOrEmpty(origin)) return true;
            if (_options.AllowedOrigins == null || _options.AllowedOrigins.Count == 0) return false;

            // "null" 是浏览器对沙箱化/不透明来源的写法，不能当成一个可比较的 origin
            if (string.Equals(origin.Trim(), "null", StringComparison.OrdinalIgnoreCase)) return false;

            Uri actual;
            if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out actual)) return false;

            foreach (var allowed in _options.AllowedOrigins)
            {
                if (string.IsNullOrEmpty(allowed)) continue;

                Uri expected;
                if (!Uri.TryCreate(allowed.Trim(), UriKind.Absolute, out expected)) continue;

                if (!string.Equals(actual.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase)) continue;

                var anyPort = expected.IsDefaultPort && IsLoopbackHost(expected.Host);
                if (!anyPort && actual.Port != expected.Port) continue;

                return true;
            }

            return false;
        }

        private static bool IsLoopbackHost(string host)
        {
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   host == "127.0.0.1" ||
                   host == "::1" ||
                   host == "[::1]";
        }

        private bool IsAuthorized(HttpRequest request)
        {
            if (string.IsNullOrEmpty(_options.Token)) return true;

            var header = request.Header("Authorization");
            if (string.IsNullOrEmpty(header)) return false;

            const string scheme = "Bearer ";
            if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;

            return FixedTimeEquals(header.Substring(scheme.Length).Trim(), _options.Token);
        }

        /// <summary>定长比较，避免通过响应时间逐字符猜令牌。</summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            var diff = a.Length ^ b.Length;
            for (var i = 0; i < a.Length && i < b.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static HttpResponse UnsupportedVersion(JsonValue id, string requested)
        {
            var data = JsonValue.NewObject()
                .Set("supported", McpProtocol.SupportedVersionsJson())
                .Set("requested", requested ?? string.Empty);

            return JsonResponse(400, "Bad Request",
                JsonRpcMessage.Error(id, JsonRpcErrorCodes.UnsupportedProtocolVersion,
                    "不支持的协议版本。", data));
        }

        /// <summary>连 id 都拿不到时的错误响应：规范允许 error 响应的 id 为 null。</summary>
        private static HttpResponse Deny(int statusCode, string reason, int code, string message) =>
            JsonResponse(statusCode, reason, JsonRpcMessage.Error(null, code, message));

        private static HttpResponse JsonResponse(int statusCode, string reason, JsonValue payload) =>
            HttpResponse.Json(statusCode, reason, payload.ToJson());
    }
}
