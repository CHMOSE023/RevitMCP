using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

        /// <summary>
        /// 流式响应体。设了它就不写 Content-Length，改用分块传输，
        /// 由回调决定往流里写什么、写多久。SSE 走这条路。
        /// 与 <see cref="Body"/> 互斥。
        /// </summary>
        public Func<ResponseStream, CancellationToken, Task> StreamBody { get; set; }

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

        /// <summary>
        /// SSE 响应。响应头先发出去，事件随后一条条推。
        /// </summary>
        public static HttpResponse EventStream(Func<ResponseStream, CancellationToken, Task> writer)
        {
            var response = new HttpResponse(200, "OK")
            {
                ContentType = "text/event-stream; charset=utf-8",
                StreamBody = writer ?? throw new ArgumentNullException(nameof(writer))
            };

            // 中间代理缓冲 SSE 会让"实时进度"变成"最后一起到"，等于没有进度
            response.Headers["Cache-Control"] = "no-cache, no-store";
            response.Headers["X-Accel-Buffering"] = "no";
            return response;
        }
    }

    /// <summary>
    /// 分块响应体的写入口。
    ///
    /// 用 chunked 而不是"写完就关连接"：后者实现更省事，
    /// 但每次带进度的调用都要重建连接，而带进度的恰恰是那些要跑几十秒的调用。
    /// </summary>
    public sealed class ResponseStream
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly byte[] Crlf = { 13, 10 };
        private static readonly byte[] Terminator = Utf8NoBom.GetBytes("0\r\n\r\n");

        private readonly Stream _stream;

        internal ResponseStream(Stream stream)
        {
            _stream = stream;
        }

        /// <summary>写一个块并立即冲刷——不冲刷的话"实时进度"就成了摆设。</summary>
        public async Task WriteAsync(string text, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(text)) return;

            var payload = Utf8NoBom.GetBytes(text);
            var header = Utf8NoBom.GetBytes(
                payload.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n");

            await _stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(Crlf, 0, Crlf.Length, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>写结束块。不写它客户端会一直等下去。</summary>
        internal async Task CompleteAsync(CancellationToken cancellationToken)
        {
            await _stream.WriteAsync(Terminator, 0, Terminator.Length, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
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
