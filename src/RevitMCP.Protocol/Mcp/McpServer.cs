using System;
using System.Threading;
using System.Threading.Tasks;
using RevitMCP.Protocol.Json;
using RevitMCP.Protocol.JsonRpc;

namespace RevitMCP.Protocol.Mcp
{
    public sealed class McpServerOptions
    {
        public string ServerName { get; set; } = "RevitMCP";
        public string ServerVersion { get; set; } = "0.1.0";
        public string Instructions { get; set; }
    }

    /// <summary>
    /// 与传输无关的 MCP 方法分发。输入是一条已解析的 JSON-RPC 消息 + 请求上下文，
    /// 输出是一个 JSON-RPC 响应对象（通知则返回 null）。
    ///
    /// 双 era 的差异在这里收敛：<see cref="McpRequestContext.Era"/> 决定
    /// 结果里是否带 resultType、以及 initialize 是否可用。
    /// 传输层负责判定 era 并做 HTTP 层校验，见 RevitMCP.Transport.McpHttpHandler。
    /// </summary>
    public sealed class McpServer
    {
        private readonly McpServerOptions _options;
        private readonly IToolCatalog _tools;
        private readonly IResourceCatalog _resources;

        public McpServer(McpServerOptions options, IToolCatalog tools, IResourceCatalog resources = null)
        {
            _options = options ?? new McpServerOptions();
            _tools = tools ?? new EmptyToolCatalog();
            _resources = resources ?? new EmptyResourceCatalog();
        }

        /// <summary>
        /// 有没有资源可发布。没有就不声明这份能力，也不接受 resources/*。
        ///
        /// **必须吞掉异常。** 这个属性被 <see cref="IsKnownMethod"/> 调用，
        /// 而那个方法在传输层是在 try/catch **之外**跑的：
        /// 目录实现一抛异常，每一个请求（包括 ping 和 tools/call）都会变成 500，
        /// 整个服务因为一份可有可无的文档而瘫掉。资源是锦上添花，不能是单点故障。
        /// </summary>
        private bool HasResources
        {
            get
            {
                try
                {
                    var list = _resources.ListResources();
                    return list != null && list.Count > 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>该方法是否存在。传输层据此决定 modern 下是否返回 HTTP 404。</summary>
        public bool IsKnownMethod(string method, McpEra era)
        {
            switch (method)
            {
                case "ping":
                case "tools/list":
                case "tools/call":
                    return true;
                case "resources/list":
                case "resources/read":
                    return HasResources;
                case "server/discover":
                    return era == McpEra.Modern;
                case "initialize":
                case "notifications/initialized":
                    return era == McpEra.Legacy;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 分发一条消息。通知返回 null（传输层据此回 202）。
        /// 任何未预料的异常都会被兜成 -32603，绝不让异常穿透到连接处理循环。
        /// </summary>
        public async Task<JsonValue> DispatchAsync(
            JsonRpcMessage message, McpRequestContext context, CancellationToken cancellationToken,
            IProgressSink progress = null)
        {
            if (message.IsNotification)
            {
                // 本期唯一接受的通知是 legacy 的 initialized，收下即可，无需回应
                return null;
            }

            try
            {
                switch (message.Method)
                {
                    case "ping":
                        return JsonRpcMessage.Result(message.Id, context.NewResult());

                    case "server/discover":
                        return JsonRpcMessage.Result(message.Id, Discover(context));

                    case "initialize":
                        return JsonRpcMessage.Result(message.Id, Initialize(message.Params));

                    case "tools/list":
                        return JsonRpcMessage.Result(message.Id, ListTools(context));

                    case "tools/call":
                        return await CallToolAsync(message, context, progress, cancellationToken)
                            .ConfigureAwait(false);

                    case "resources/list":
                        if (!HasResources) goto default;
                        return JsonRpcMessage.Result(message.Id, ListResources(context));

                    case "resources/read":
                        if (!HasResources) goto default;
                        return ReadResource(message, context);

                    default:
                        return JsonRpcMessage.Error(message.Id,
                            JsonRpcErrorCodes.MethodNotFound, "未知方法：" + message.Method);
                }
            }
            catch (OperationCanceledException)
            {
                throw;   // 取消由传输层处理：关闭响应流即可，不回错误
            }
            catch (Exception ex)
            {
                return JsonRpcMessage.Error(message.Id, JsonRpcErrorCodes.InternalError, ex.Message);
            }
        }

        // ---------- modern：server/discover ----------

        private JsonValue Discover(McpRequestContext context)
        {
            return context.NewResult()
                .Set("supportedVersions", McpProtocol.SupportedVersionsJson())
                .Set("capabilities", Capabilities())
                .Set("instructions", InstructionsText())
                .Set("_meta", JsonValue.NewObject().Set(McpProtocol.MetaServerInfo, ServerInfo()));
        }

        // ---------- legacy：initialize 握手 ----------

        private JsonValue Initialize(JsonValue parameters)
        {
            var requested = parameters["protocolVersion"];
            var requestedVersion = requested != null && requested.Kind == JsonKind.String
                ? requested.AsString
                : null;

            return JsonValue.NewObject()
                .Set("protocolVersion", McpProtocol.NegotiateLegacyVersion(requestedVersion))
                .Set("capabilities", Capabilities())
                .Set("serverInfo", ServerInfo())
                .Set("instructions", InstructionsText());
        }

        // ---------- 工具 ----------

        private JsonValue ListTools(McpRequestContext context)
        {
            var tools = JsonValue.NewArray();
            foreach (var tool in _tools.ListTools()) tools.Add(tool.ToJson());
            return context.NewResult().Set("tools", tools);
        }

        private async Task<JsonValue> CallToolAsync(
            JsonRpcMessage message, McpRequestContext context, IProgressSink progress,
            CancellationToken cancellationToken)
        {
            var nameValue = message.Params["name"];
            if (nameValue == null || nameValue.Kind != JsonKind.String || nameValue.AsString.Length == 0)
                return JsonRpcMessage.Error(message.Id, JsonRpcErrorCodes.InvalidParams, "缺少 params.name。");

            var arguments = message.Params["arguments"];
            if (arguments == null || arguments.IsNull) arguments = JsonValue.NewObject();
            else if (!arguments.IsObject)
                return JsonRpcMessage.Error(message.Id, JsonRpcErrorCodes.InvalidParams, "params.arguments 必须是对象。");

            try
            {
                var result = await _tools
                    .CallToolAsync(nameValue.AsString, arguments,
                        progress ?? (IProgressSink)NullProgressSink.Instance, cancellationToken)
                    .ConfigureAwait(false);
                return JsonRpcMessage.Result(message.Id, result.ToJson(context));
            }
            catch (ToolNotFoundException ex)
            {
                // "工具不存在"是请求结构问题，模型很难自我纠正，按规范走 JSON-RPC 错误
                return JsonRpcMessage.Error(message.Id, JsonRpcErrorCodes.InvalidParams, ex.Message);
            }
        }

        // ---------- 资源 ----------

        private JsonValue ListResources(McpRequestContext context)
        {
            var resources = JsonValue.NewArray();
            foreach (var resource in _resources.ListResources()) resources.Add(resource.ToJson());
            return context.NewResult().Set("resources", resources);
        }

        private JsonValue ReadResource(JsonRpcMessage message, McpRequestContext context)
        {
            var uriValue = message.Params["uri"];
            if (uriValue == null || uriValue.Kind != JsonKind.String || uriValue.AsString.Length == 0)
                return JsonRpcMessage.Error(message.Id, JsonRpcErrorCodes.InvalidParams, "缺少 params.uri。");

            var contents = _resources.ReadResource(uriValue.AsString);

            if (contents == null)
            {
                // 把有哪些 URI 一并说出来：找不到资源时，光说"找不到"会让调用方去猜拼写
                var known = new System.Text.StringBuilder();
                foreach (var resource in _resources.ListResources())
                {
                    if (known.Length > 0) known.Append("、");
                    known.Append(resource.Uri);
                }

                return JsonRpcMessage.Error(message.Id, JsonRpcErrorCodes.InvalidParams,
                    "没有 URI 为 " + uriValue.AsString + " 的资源。可用的是：" + known + "。");
            }

            var items = JsonValue.NewArray();
            items.Add(contents.ToJson());

            return JsonRpcMessage.Result(message.Id, context.NewResult().Set("contents", items));
        }

        // ---------- 共用片段 ----------

        private JsonValue Capabilities()
        {
            var capabilities = JsonValue.NewObject()
                .Set("tools", JsonValue.NewObject().Set("listChanged", false));

            if (HasResources)
                capabilities.Set("resources", JsonValue.NewObject()
                    .Set("subscribe", false)
                    .Set("listChanged", false));

            return capabilities;
        }

        private JsonValue ServerInfo() =>
            JsonValue.NewObject()
                .Set("name", _options.ServerName)
                .Set("version", _options.ServerVersion);

        private string InstructionsText() =>
            _options.Instructions ?? "在 Revit 中查询与修改当前打开的模型。";
    }
}
