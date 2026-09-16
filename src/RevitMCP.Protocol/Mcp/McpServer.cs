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

        public McpServer(McpServerOptions options, IToolCatalog tools)
        {
            _options = options ?? new McpServerOptions();
            _tools = tools ?? new EmptyToolCatalog();
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
            JsonRpcMessage message, McpRequestContext context, CancellationToken cancellationToken)
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
                        return await CallToolAsync(message, context, cancellationToken).ConfigureAwait(false);

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
            JsonRpcMessage message, McpRequestContext context, CancellationToken cancellationToken)
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
                    .CallToolAsync(nameValue.AsString, arguments, cancellationToken)
                    .ConfigureAwait(false);
                return JsonRpcMessage.Result(message.Id, result.ToJson(context));
            }
            catch (ToolNotFoundException ex)
            {
                // "工具不存在"是请求结构问题，模型很难自我纠正，按规范走 JSON-RPC 错误
                return JsonRpcMessage.Error(message.Id, JsonRpcErrorCodes.InvalidParams, ex.Message);
            }
        }

        // ---------- 共用片段 ----------

        private JsonValue Capabilities() =>
            JsonValue.NewObject()
                .Set("tools", JsonValue.NewObject().Set("listChanged", false));

        private JsonValue ServerInfo() =>
            JsonValue.NewObject()
                .Set("name", _options.ServerName)
                .Set("version", _options.ServerVersion);

        private string InstructionsText() =>
            _options.Instructions ?? "在 Revit 中查询与修改当前打开的模型。";
    }
}
