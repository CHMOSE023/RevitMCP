using System;
using System.Collections.Generic;
using System.Text;

namespace RevitMCP.Transport.Http
{
    /// <summary>一次已解析的 HTTP 请求。</summary>
    public sealed class HttpRequest
    {
        private readonly Dictionary<string, string> _headers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // 头名不区分大小写

        public string Method { get; internal set; }

        /// <summary>不含查询串的路径。</summary>
        public string Path { get; internal set; }

        public string Body { get; internal set; } = string.Empty;

        public IReadOnlyDictionary<string, string> Headers => _headers;

        internal void SetHeader(string name, string value)
        {
            // 同名头按 RFC 9110 用逗号合并
            _headers[name] = _headers.TryGetValue(name, out var existing)
                ? existing + "," + value
                : value;
        }

        /// <summary>取头；不存在返回 null。</summary>
        public string Header(string name) =>
            _headers.TryGetValue(name, out var value) ? value : null;

        public bool HasHeader(string name) => _headers.ContainsKey(name);
    }

    public sealed class HttpResponse
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public HttpResponse(int statusCode, string reasonPhrase)
        {
            StatusCode = statusCode;
            ReasonPhrase = reasonPhrase;
        }

        public int StatusCode { get; }
        public string ReasonPhrase { get; }
        public string ContentType { get; set; }
        public byte[] Body { get; set; }

        public Dictionary<string, string> Headers { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>连接是否应在响应后关闭。认证/协议层面的硬错误不值得复用连接。</summary>
        public bool CloseConnection { get; set; }

        public static HttpResponse Json(int statusCode, string reasonPhrase, string json)
        {
            return new HttpResponse(statusCode, reasonPhrase)
            {
                ContentType = "application/json; charset=utf-8",
                Body = Utf8NoBom.GetBytes(json ?? string.Empty)
            };
        }

        public static HttpResponse Text(int statusCode, string reasonPhrase, string text)
        {
            return new HttpResponse(statusCode, reasonPhrase)
            {
                ContentType = "text/plain; charset=utf-8",
                Body = Utf8NoBom.GetBytes(text ?? string.Empty)
            };
        }

        /// <summary>无响应体，用于 202 Accepted。</summary>
        public static HttpResponse Empty(int statusCode, string reasonPhrase) =>
            new HttpResponse(statusCode, reasonPhrase) { Body = new byte[0] };
    }

    /// <summary>请求格式非法。携带应回给客户端的状态码。</summary>
    internal sealed class HttpProtocolException : Exception
    {
        public HttpProtocolException(int statusCode, string reasonPhrase, string message) : base(message)
        {
            StatusCode = statusCode;
            ReasonPhrase = reasonPhrase;
        }

        public int StatusCode { get; }
        public string ReasonPhrase { get; }
    }
}
