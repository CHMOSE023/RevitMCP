using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RevitMCP.Transport.Http
{
    /// <summary>
    /// 基于 TcpListener 的最小 HTTP/1.1 服务。
    ///
    /// 为什么不用 System.Net.HttpListener：它依赖 http.sys 的 URL 预留，
    /// 非管理员启动时通常抛 HttpListenerException(5)，要求用户跑 netsh 或以管理员开 Revit
    /// 是不可接受的部署摩擦。TcpListener 绑定 127.0.0.1 不需要任何 ACL。
    ///
    /// 只实现本地 MCP 客户端会用到的子集：定长 body、keep-alive、无分块传输。
    /// </summary>
    public sealed class MiniHttpServer : IDisposable
    {
        private const int MaxHeaderBytes = 32 * 1024;
        private const int MaxBodyBytes = 8 * 1024 * 1024;
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

        private readonly Func<HttpRequest, CancellationToken, Task<HttpResponse>> _handler;
        private readonly Action<string, Exception> _log;
        private readonly object _gate = new object();

        private TcpListener _listener;
        private CancellationTokenSource _shutdown;
        private Task _acceptLoop;

        public MiniHttpServer(
            Func<HttpRequest, CancellationToken, Task<HttpResponse>> handler,
            Action<string, Exception> log = null)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _log = log ?? ((m, e) => { });
        }

        /// <summary>实际绑定到的端口。</summary>
        public int Port { get; private set; }

        public bool IsRunning { get; private set; }

        /// <summary>
        /// 从 <paramref name="preferredPort"/> 起向上探测可用端口。
        /// 同一台机器可能开多个 Revit，端口冲突是常态而非异常。
        /// </summary>
        public int Start(int preferredPort, int attempts = 20)
        {
            lock (_gate)
            {
                if (IsRunning) return Port;

                Exception last = null;
                for (var i = 0; i < attempts; i++)
                {
                    var port = preferredPort + i;
                    try
                    {
                        // 只绑回环地址：MCP 规范对本地服务的明确要求
                        var listener = new TcpListener(IPAddress.Loopback, port);

                        // 不设 ReuseAddress：在 Windows 上它允许绑定到别人已占用的端口，
                        // 会让"端口探测"失去意义，两个实例抢同一个端口。
                        listener.Start();

                        _listener = listener;
                        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                        _shutdown = new CancellationTokenSource();
                        IsRunning = true;
                        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _shutdown.Token));
                        _log("HTTP 服务已监听 127.0.0.1:" + Port, null);
                        return Port;
                    }
                    catch (SocketException ex)
                    {
                        last = ex;   // 端口被占用，试下一个
                    }
                }

                throw new IOException(
                    string.Format(CultureInfo.InvariantCulture,
                        "在 {0}–{1} 范围内找不到可用端口。", preferredPort, preferredPort + attempts - 1),
                    last);
            }
        }

        public void Stop()
        {
            Task acceptLoop;
            lock (_gate)
            {
                if (!IsRunning) return;
                IsRunning = false;

                try { _shutdown.Cancel(); } catch { }
                try { _listener.Stop(); } catch { }   // 唯一能打断阻塞中的 Accept 的办法
                acceptLoop = _acceptLoop;
                _listener = null;
                _acceptLoop = null;
                Port = 0;
            }

            // 等 accept 循环退出，避免 Revit 关闭过程中还有线程在跑
            try { acceptLoop?.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }

            lock (_gate)
            {
                try { _shutdown?.Dispose(); } catch { }
                _shutdown = null;
            }
            _log("HTTP 服务已停止。", null);
        }

        public void Dispose() => Stop();

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken shutdown)
        {
            while (!shutdown.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }   // Stop() 关掉了监听器
                catch (InvalidOperationException) { break; }
                catch (SocketException) { break; }

                // 每个连接独立跑，一个连接出问题不影响其他连接
                var _ = Task.Run(() => HandleConnectionAsync(client, shutdown));
            }
        }

        private async Task HandleConnectionAsync(TcpClient client, CancellationToken shutdown)
        {
            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    client.ReceiveTimeout = (int)IdleTimeout.TotalMilliseconds;
                    client.SendTimeout = (int)IdleTimeout.TotalMilliseconds;

                    using (var stream = client.GetStream())
                    {
                        var reader = new RequestReader(stream);

                        while (!shutdown.IsCancellationRequested)
                        {
                            HttpRequest request;
                            try
                            {
                                request = await reader.ReadRequestAsync(shutdown).ConfigureAwait(false);
                            }
                            catch (HttpProtocolException ex)
                            {
                                await WriteResponseAsync(stream,
                                    HttpResponse.Text(ex.StatusCode, ex.ReasonPhrase, ex.Message),
                                    close: true).ConfigureAwait(false);
                                return;
                            }

                            if (request == null) return;   // 客户端正常关闭连接

                            HttpResponse response;
                            try
                            {
                                response = await _handler(request, shutdown).ConfigureAwait(false)
                                           ?? HttpResponse.Text(500, "Internal Server Error", "处理器未返回响应。");
                            }
                            catch (OperationCanceledException) { return; }
                            catch (Exception ex)
                            {
                                // 处理器抛异常不能拖垮连接循环，更不能泄漏堆栈给客户端
                                _log("请求处理失败。", ex);
                                response = HttpResponse.Text(500, "Internal Server Error", "服务内部错误。");
                            }

                            var close = response.CloseConnection ||
                                        string.Equals(request.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase);

                            await WriteResponseAsync(stream, response, close).ConfigureAwait(false);
                            if (close) return;
                        }
                    }
                }
            }
            catch (IOException) { /* 连接被对端断开，属正常 */ }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _log("连接处理异常。", ex);
            }
        }

        private static async Task WriteResponseAsync(Stream stream, HttpResponse response, bool close)
        {
            var body = response.Body ?? new byte[0];

            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(response.StatusCode.ToString(CultureInfo.InvariantCulture))
                .Append(' ').Append(response.ReasonPhrase).Append("\r\n");

            if (!string.IsNullOrEmpty(response.ContentType))
                head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");

            head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            head.Append("Connection: ").Append(close ? "close" : "keep-alive").Append("\r\n");

            foreach (var header in response.Headers)
                head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");

            head.Append("\r\n");

            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            await stream.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);
            if (body.Length > 0) await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 从流中逐个读取请求。自带缓冲，因为要先按字节找到头部结束标记，
        /// 再按 Content-Length 读定长 body——两者可能落在同一次 Read 里。
        /// </summary>
        private sealed class RequestReader
        {
            private readonly Stream _stream;
            private byte[] _buffer = new byte[8192];
            private int _length;
            private int _position;

            public RequestReader(Stream stream) => _stream = stream;

            public async Task<HttpRequest> ReadRequestAsync(CancellationToken cancellationToken)
            {
                var headerEnd = await FindHeaderEndAsync(cancellationToken).ConfigureAwait(false);
                if (headerEnd < 0) return null;   // 连接关闭且无残留数据

                var headerText = Encoding.ASCII.GetString(_buffer, _position, headerEnd - _position);
                _position = headerEnd + 4;   // 跳过 \r\n\r\n

                var request = ParseHead(headerText);

                var contentLength = ReadContentLength(request);
                if (contentLength > 0)
                {
                    var body = await ReadExactlyAsync(contentLength, cancellationToken).ConfigureAwait(false);
                    request.Body = Encoding.UTF8.GetString(body);
                }

                return request;
            }

            private static HttpRequest ParseHead(string headerText)
            {
                var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0 || lines[0].Length == 0)
                    throw new HttpProtocolException(400, "Bad Request", "请求行为空。");

                var parts = lines[0].Split(' ');
                if (parts.Length < 3)
                    throw new HttpProtocolException(400, "Bad Request", "请求行格式非法。");

                if (!parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                    throw new HttpProtocolException(505, "HTTP Version Not Supported", "仅支持 HTTP/1.x。");

                var target = parts[1];
                var queryStart = target.IndexOf('?');
                var request = new HttpRequest
                {
                    Method = parts[0],
                    Path = queryStart >= 0 ? target.Substring(0, queryStart) : target
                };

                for (var i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.Length == 0) continue;

                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                        throw new HttpProtocolException(400, "Bad Request", "头部格式非法：" + line);

                    request.SetHeader(line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim());
                }

                return request;
            }

            private static int ReadContentLength(HttpRequest request)
            {
                // 分块传输本地客户端不会用，明确拒绝好过悄悄读错
                var transferEncoding = request.Header("Transfer-Encoding");
                if (!string.IsNullOrEmpty(transferEncoding))
                    throw new HttpProtocolException(411, "Length Required", "不支持 Transfer-Encoding，请使用 Content-Length。");

                var raw = request.Header("Content-Length");
                if (string.IsNullOrEmpty(raw)) return 0;

                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) || length < 0)
                    throw new HttpProtocolException(400, "Bad Request", "Content-Length 非法。");

                if (length > MaxBodyBytes)
                    throw new HttpProtocolException(413, "Payload Too Large", "请求体超过上限。");

                return length;
            }

            /// <summary>返回 \r\n\r\n 的起始下标；连接干净关闭返回 -1。</summary>
            private async Task<int> FindHeaderEndAsync(CancellationToken cancellationToken)
            {
                while (true)
                {
                    // 每轮都从 _position 重扫：FillAsync 内部会 Compact，缓冲区下标会整体平移，
                    // 缓存扫描位置反而会读错。头部有 32KB 上限，重扫的代价可以忽略。
                    for (var i = _position; i + 3 < _length; i++)
                    {
                        if (_buffer[i] == '\r' && _buffer[i + 1] == '\n' &&
                            _buffer[i + 2] == '\r' && _buffer[i + 3] == '\n')
                            return i;
                    }

                    if (_length - _position > MaxHeaderBytes)
                        throw new HttpProtocolException(431, "Request Header Fields Too Large", "请求头超过上限。");

                    var read = await FillAsync(cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        if (_length == _position) return -1;   // 连接干净关闭
                        throw new HttpProtocolException(400, "Bad Request", "请求头不完整。");
                    }
                }
            }

            private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken cancellationToken)
            {
                while (_length - _position < count)
                {
                    var read = await FillAsync(cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        throw new HttpProtocolException(400, "Bad Request", "请求体不完整。");
                }

                var result = new byte[count];
                Buffer.BlockCopy(_buffer, _position, result, 0, count);
                _position += count;
                return result;
            }

            private async Task<int> FillAsync(CancellationToken cancellationToken)
            {
                Compact();
                if (_length == _buffer.Length)
                {
                    var grown = new byte[_buffer.Length * 2];
                    Buffer.BlockCopy(_buffer, 0, grown, 0, _length);
                    _buffer = grown;
                }

                var read = await _stream
                    .ReadAsync(_buffer, _length, _buffer.Length - _length, cancellationToken)
                    .ConfigureAwait(false);
                _length += read;
                return read;
            }

            /// <summary>把已消费的部分挪掉，避免缓冲区无限增长。</summary>
            private void Compact()
            {
                if (_position == 0) return;
                var remaining = _length - _position;
                if (remaining > 0) Buffer.BlockCopy(_buffer, _position, _buffer, 0, remaining);
                _position = 0;
                _length = remaining;
            }
        }
    }
}
