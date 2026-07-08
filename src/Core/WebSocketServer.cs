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
        // Guards swaps of _client (hot-swap on new connection, clear on disconnect)
        // so a concurrent accept/disconnect can't act on the wrong socket.
        private readonly object _clientLock = new object();
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

            // Try progressively narrower binds. Best case binds BOTH loopback stacks
            // so the browser connects however it resolves "localhost". If binding one
            // explicit IP is denied (URL ACL / elevation on locked-down machines) the
            // combined bind fails atomically, so fall back to each stack alone — IPv4
            // first, since the browser dials 127.0.0.1 first — and finally to the
            // "localhost" prefix, which HttpListener special-cases so it never needs
            // a reservation. This guarantees we never lose IPv4 just because IPv6
            // (or vice versa) couldn't be bound.
            string[][] candidates =
            {
                new[] { $"http://127.0.0.1:{_port}/", $"http://[::1]:{_port}/" },
                new[] { $"http://127.0.0.1:{_port}/" },
                new[] { $"http://[::1]:{_port}/" },
                new[] { $"http://localhost:{_port}/" },
            };

            foreach (var prefixes in candidates)
                if (TryStartListener(prefixes))
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
                    var newClient = wsContext.WebSocket;

                    // Hot-swap: replace the previous client atomically, then close it
                    // in the background so the accept loop is never blocked.
                    WebSocket oldClient;
                    lock (_clientLock)
                    {
                        oldClient = _client;
                        _client = newClient;
                    }
                    if (oldClient != null && oldClient.State == WebSocketState.Open)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await oldClient.CloseAsync(
                                    WebSocketCloseStatus.NormalClosure, "New client", CancellationToken.None)
                                    .ConfigureAwait(false);
                            }
                            catch { /* ignore close errors on old client */ }
                        });
                    }

                    Debug.WriteLine("[CC] Client connected");

                    // Receive in the background so the accept loop returns to
                    // GetContextAsync immediately — a second client's upgrade must
                    // not hang until the first disconnects (that also kept the
                    // hot-swap above from ever running while connected).
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ReceiveLoop(newClient, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[CC] Receive loop error: {ex.Message}");
                        }
                        finally
                        {
                            // Drop our reference only if this socket is still current
                            // (it may already have been hot-swapped by a newer client).
                            lock (_clientLock)
                            {
                                if (ReferenceEquals(_client, newClient)) _client = null;
                            }
                        }
                    });
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
                        string text;
                        using (var ms = new MemoryStream())
                        {
                            ms.Write(buffer, 0, result.Count);
                            while (!result.EndOfMessage)
                            {
                                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                                ms.Write(buffer, 0, result.Count);
                            }
                            text = Encoding.UTF8.GetString(ms.ToArray());
                        }
                        OnMessage?.Invoke(text);
                    }
                }
            }
            catch (WebSocketException ex)
            {
                Debug.WriteLine($"[CC] Receive error: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                // Server shutting down — expected.
            }

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
            WebSocket ws;
            lock (_clientLock)
            {
                ws = _client;
                _client = null;
            }
            if (ws?.State == WebSocketState.Open)
            {
                try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", CancellationToken.None).Wait(1000); }
                catch { }
            }
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
