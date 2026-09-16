using System;
using RevitMCP.Protocol.Json;

namespace RevitMCP.Protocol.JsonRpc
{
    public static class JsonRpcErrorCodes
    {
        // JSON-RPC 2.0 标准码
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        // MCP 规范在保留区间分配的协议码
        public const int HeaderMismatch = -32020;
        public const int UnsupportedProtocolVersion = -32022;
    }

    /// <summary>
    /// 一条从客户端收到的 JSON-RPC 消息。
    /// 按规范，客户端只会发 request 与 notification，不会发 response——
    /// 两者的唯一区别是有没有 id。
    /// </summary>
    public sealed class JsonRpcMessage
    {
        private JsonRpcMessage(string method, JsonValue id, JsonValue parameters)
        {
            Method = method;
            Id = id;
            Params = parameters;
        }

        public string Method { get; }

        /// <summary>null 表示这是一个通知。保留原始 JsonValue：id 可能是字符串或数字，原样回填。</summary>
        public JsonValue Id { get; }

        /// <summary>始终非 null；缺省时为空对象，省掉调用方到处判空。</summary>
        public JsonValue Params { get; }

        public bool IsNotification => Id == null;

        /// <summary>
        /// 解析一条消息。格式错误抛 <see cref="JsonRpcParseException"/>，
        /// 调用方据此决定返回 -32700 还是 -32600。
        /// </summary>
        public static JsonRpcMessage Parse(JsonValue root)
        {
            // 批量请求已从规范中移除，直接拒绝而不是勉强支持（先于对象判定，好给出准确的错误信息）
            if (root != null && root.IsArray)
                throw new JsonRpcParseException(JsonRpcErrorCodes.InvalidRequest, "不支持 JSON-RPC 批量请求。");

            if (root == null || !root.IsObject)
                throw new JsonRpcParseException(JsonRpcErrorCodes.InvalidRequest, "JSON-RPC 消息必须是对象。");

            var version = root["jsonrpc"];
            if (version == null || version.Kind != JsonKind.String || version.AsString != "2.0")
                throw new JsonRpcParseException(JsonRpcErrorCodes.InvalidRequest, "jsonrpc 字段必须为 \"2.0\"。");

            var method = root["method"];
            if (method == null || method.Kind != JsonKind.String || method.AsString.Length == 0)
                throw new JsonRpcParseException(JsonRpcErrorCodes.InvalidRequest, "method 字段缺失或非法。");

            var id = root["id"];
            if (id != null && id.Kind != JsonKind.String && id.Kind != JsonKind.Number)
            {
                // id 为 null 在规范中等同于通知；其他类型一律非法
                if (id.Kind != JsonKind.Null)
                    throw new JsonRpcParseException(JsonRpcErrorCodes.InvalidRequest, "id 必须是字符串或数字。");
                id = null;
            }

            var parameters = root["params"];
            if (parameters == null || parameters.IsNull) parameters = JsonValue.NewObject();
            else if (!parameters.IsObject)
                throw new JsonRpcParseException(JsonRpcErrorCodes.InvalidParams, "params 必须是对象。");

            return new JsonRpcMessage(method.AsString, id, parameters);
        }

        // ---------- 构造出站消息 ----------

        public static JsonValue Result(JsonValue id, JsonValue result) =>
            JsonValue.NewObject()
                .Set("jsonrpc", "2.0")
                .Set("id", id ?? JsonValue.Null)
                .Set("result", result ?? JsonValue.NewObject());

        public static JsonValue Error(JsonValue id, int code, string message, JsonValue data = null)
        {
            var error = JsonValue.NewObject()
                .Set("code", code)
                .Set("message", message ?? string.Empty);
            if (data != null) error.Set("data", data);

            return JsonValue.NewObject()
                .Set("jsonrpc", "2.0")
                .Set("id", id ?? JsonValue.Null)
                .Set("error", error);
        }

        public static JsonValue Notification(string method, JsonValue parameters)
        {
            var message = JsonValue.NewObject()
                .Set("jsonrpc", "2.0")
                .Set("method", method);
            if (parameters != null) message.Set("params", parameters);
            return message;
        }
    }

    public sealed class JsonRpcParseException : Exception
    {
        public JsonRpcParseException(int code, string message) : base(message)
        {
            Code = code;
        }

        public int Code { get; }
    }
}
