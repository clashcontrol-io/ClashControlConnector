using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClashControlConnector.Core
{
    /// <summary>
    /// WebSocket server on localhost using built-in HttpListener.
    /// Accepts a single client at a time with origin validation.
    /// </summary>
    public class WsServer : IDisposable
    {
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private volatile WebSocket _client;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly int _port;

        public bool IsClientConnected => _client?.State == WebSocketState.Open;

        public event Action<string> OnMessage;

        public WsServer(int port = 19780)
        {
            _port = port;
        }

        /// <summary>
        /// Start the WebSocket server. Returns true on success, false if the port
        /// could not be bound (e.g. already in use).
        /// </summary>
        public bool Start()
        {
            _cts = new CancellationTokenSource();

            // Bind BOTH IPv4 and IPv6 loopback. "localhost" resolves to only one
            // stack on some machines (e.g. [::1] only), so a single localhost/IP
            // prefix can leave the other stack closed — the browser then gets an
            // immediate ECONNREFUSED on whichever address we didn't bind. Binding
            // 127.0.0.1 and [::1] explicitly covers any client resolution.
            if (TryStartListener(new[] { $"http://127.0.0.1:{_port}/", $"http://[::1]:{_port}/" }))
                return true;

            // Fallback: binding an explicit loopback IP can require a URL ACL
            // reservation / elevation on locked-down machines. HttpListener
            // special-cases the "localhost" prefix so it never needs one — use it
            // so the connector still comes up on at least one stack.
            if (TryStartListener(new[] { $"http://localhost:{_port}/" }))
                return true;

            _cts.Dispose();
            _cts = null;
            return false;
        }

        /// <summary>
        /// Attempt to start an HttpListener bound to the given prefixes. Returns
        /// true and assigns _listener on success; leaves state untouched on failure
        /// so the caller can try a different prefix set.
        /// </summary>
        private bool TryStartListener(string[] prefixes)
        {
            var listener = new HttpListener();
            foreach (var p in prefixes) listener.Prefixes.Add(p);
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                Debug.WriteLine($"[CC] Failed to bind {string.Join(", ", prefixes)}: {ex.Message}");
                try { listener.Close(); } catch { }
                return false;
            }
            _listener = listener;
            Task.Run(() => AcceptLoop(_cts.Token));
            Debug.WriteLine($"[CC] WebSocket server started on {string.Join(", ", prefixes)}");
            return true;
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();

                    if (!IsOriginAllowed(context.Request))
                    {
                        Debug.WriteLine($"[CC] Rejected connection from origin: {context.Request.Headers["Origin"]}");
                        context.Response.StatusCode = 403;
                        context.Response.Close();
                        continue;
                    }

                    if (!context.Request.IsWebSocketRequest)
                    {
                        // Respond to HTTP health checks (e.g. GET /) with status JSON
                        if (context.Request.HttpMethod == "GET")
                        {
                            var statusJson = Encoding.UTF8.GetBytes(
                                $"{{\"status\":\"ok\",\"connectorVersion\":\"{App.Version}\"}}");
                            context.Response.StatusCode = 200;
                            context.Response.ContentType = "application/json";
                            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                            context.Response.ContentLength64 = statusJson.Length;
                            await context.Response.OutputStream.WriteAsync(statusJson, 0, statusJson.Length);
                            context.Response.Close();
                        }
                        else if (context.Request.HttpMethod == "OPTIONS")
                        {
                            context.Response.StatusCode = 204;
                            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                            context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, OPTIONS");
                            context.Response.Headers.Add("Access-Control-Allow-Headers", "*");
                            context.Response.Close();
                        }
                        else
                        {
                            context.Response.StatusCode = 400;
                            context.Response.Close();
                        }
                        continue;
                    }

                    var wsContext = await context.AcceptWebSocketAsync(null);

                    // Close previous client if any
                    var oldClient = _client;
                    if (oldClient?.State == WebSocketState.Open)
                    {
                        try
                        {
                            await oldClient.CloseAsync(
                                WebSocketCloseStatus.NormalClosure, "New client", CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch { /* ignore close errors on old client */ }
                    }
                    _client = wsContext.WebSocket;

                    Debug.WriteLine("[CC] Client connected");
                    await ReceiveLoop(wsContext.WebSocket, ct);
                }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Debug.WriteLine($"[CC] Accept error: {ex.Message}");
                    await Task.Delay(1000, ct);
                }
            }
        }

        private async Task ReceiveLoop(WebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[1024 * 64];
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var ms = new MemoryStream();
                        ms.Write(buffer, 0, result.Count);
                        while (!result.EndOfMessage)
                        {
                            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                            ms.Write(buffer, 0, result.Count);
                        }

                        var text = Encoding.UTF8.GetString(ms.ToArray());
                        OnMessage?.Invoke(text);
                    }
                }
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) { }

            Debug.WriteLine("[CC] Client disconnected");
        }

        /// <summary>
        /// Send a JSON message. Returns false if the client is disconnected.
        /// Thread-safe via SemaphoreSlim to prevent interleaved frames.
        /// </summary>
        public async Task<bool> SendAsync(string json)
        {
            var ws = _client;
            if (ws?.State != WebSocketState.Open) return false;

            var bytes = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int chunkSize = Math.Min(bytes.Length - offset, 64 * 1024);
                    bool isLast = (offset + chunkSize) >= bytes.Length;
                    await ws.SendAsync(
                        new ArraySegment<byte>(bytes, offset, chunkSize),
                        WebSocketMessageType.Text,
                        isLast,
                        CancellationToken.None).ConfigureAwait(false);
                    offset += chunkSize;
                }
                return true;
            }
            catch (WebSocketException ex)
            {
                Debug.WriteLine($"[CC] Send failed: {ex.Message}");
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            var ws = _client;
            if (ws?.State == WebSocketState.Open)
            {
                try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", CancellationToken.None).Wait(1000); }
                catch { }
            }
            _client = null;
            try { _listener?.Stop(); _listener?.Close(); }
            catch { }
            Debug.WriteLine("[CC] WebSocket server stopped");
        }

        public void Dispose()
        {
            Stop();
            _sendLock.Dispose();
        }

        #region Origin Validation

        private static readonly HashSet<string> AllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "https://clashcontrol.io",
            "https://www.clashcontrol.io",
            "https://clashcontrol-io.github.io",
            "http://localhost:3000",
            "http://localhost:5173",
            "http://127.0.0.1:3000",
            "http://127.0.0.1:5173",
            "null",
        };

        private static bool IsOriginAllowed(HttpListenerRequest request)
        {
            var origin = request.Headers["Origin"];
            if (string.IsNullOrEmpty(origin)) return true;
            return AllowedOrigins.Contains(origin);
        }

        #endregion
    }
}
