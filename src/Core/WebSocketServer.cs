using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClashControlConnector.Core
{
    /// <summary>
    /// WebSocket server on localhost using built-in HttpListener.
    /// Accepts a single client at a time. Every request is gated, in order, by:
    ///   1. Host    - must be a loopback name (DNS-rebinding guard).
    ///   2. Origin  - when present must exactly match the ClashControl app
    ///                (or an origin the user opted into); otherwise 403 before
    ///                anything else happens.
    ///   3. Pairing - the WebSocket upgrade additionally needs a short-lived
    ///                token from POST /pair, so geometry is never streamed to a
    ///                page that merely reached 127.0.0.1.
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
        private readonly PairingStore _pairing = new PairingStore();

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

                    var request = context.Request;
                    var response = context.Response;

                    // 1. Host: only loopback names. A rebinding attacker's DNS name never matches.
                    if (!IsHostAllowed(request.Headers["Host"]))
                    {
                        Debug.WriteLine($"[CC] Rejected request for host: {request.Headers["Host"]}");
                        Respond(response, 421, null, null);
                        continue;
                    }

                    // 2. Origin. null => header absent (not a cross-origin browser request).
                    //    Present-but-not-allowed (incl. "" and the literal "null") => 403
                    //    BEFORE anything runs; CORS alone only hides a response, it does
                    //    not stop a "simple" cross-site request from being executed.
                    var origin = request.Headers["Origin"];
                    if (origin != null && !IsOriginAllowed(origin))
                    {
                        Debug.WriteLine($"[CC] Rejected connection from origin: {origin}");
                        Respond(response, 403, null, null);
                        continue;
                    }

                    if (!request.IsWebSocketRequest)
                    {
                        var path = request.Url?.AbsolutePath ?? "/";

                        if (request.HttpMethod == "OPTIONS")
                        {
                            // CORS + Chromium Private Network Access preflight (public https
                            // page -> loopback needs Access-Control-Allow-Private-Network).
                            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-CC-Pairing-Token");
                            response.Headers.Add("Access-Control-Allow-Private-Network", "true");
                            response.Headers.Add("Access-Control-Max-Age", "600");
                            Respond(response, 204, null, origin);
                        }
                        else if (request.HttpMethod == "POST" && path == "/pair")
                        {
                            // Only a real, allow-listed browser origin can pair, and only that
                            // origin can read the reply.
                            if (string.IsNullOrEmpty(origin))
                            {
                                Respond(response, 400, "{\"error\":\"Origin header required\",\"code\":\"origin_required\"}", null);
                            }
                            else
                            {
                                var token = _pairing.Issue();
                                Respond(response, 200,
                                    $"{{\"token\":\"{token}\",\"expiresIn\":{_pairing.LifetimeSeconds},\"protocol\":1}}",
                                    origin);
                            }
                        }
                        else if (request.HttpMethod == "GET")
                        {
                            // Liveness probe (GET /): version only, nothing sensitive.
                            Respond(response, 200, $"{{\"status\":\"ok\",\"connectorVersion\":\"{App.Version}\"}}", origin);
                        }
                        else
                        {
                            Respond(response, 400, null, origin);
                        }
                        continue;
                    }

                    // 3. WebSocket upgrade. Browsers always send Origin on a WebSocket
                    //    handshake, so a missing one is not the ClashControl page; and
                    //    the pairing token must be valid before we accept anything.
                    if (string.IsNullOrEmpty(origin))
                    {
                        Debug.WriteLine("[CC] Rejected WebSocket upgrade without an Origin");
                        Respond(response, 403, null, null);
                        continue;
                    }
                    if (!_pairing.IsValid(request.QueryString["cc_token"]))
                    {
                        Debug.WriteLine("[CC] Rejected WebSocket upgrade: pairing required");
                        Respond(response, 401, "{\"error\":\"Pairing required\",\"code\":\"pairing_required\"}", null);
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

        // Exact-match allow-list of the ClashControl web app. Anything else (other
        // sites, localhost dev servers, the literal "null") is refused; to
        // self-host or develop the web app, list its origin in the
        // CLASHCONTROL_ALLOWED_ORIGINS environment variable (comma separated).
        private static readonly HashSet<string> AllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "https://clashcontrol.io",
            "https://www.clashcontrol.io",
        };

        // Deliberately does NOT allow the literal "null" origin: any web page,
        // anywhere, can force Origin: null on its outbound requests via
        // <iframe sandbox="allow-scripts">, which would let a hostile site
        // reach this localhost WebSocket through the victim's browser with no
        // other interaction. The caller treats a MISSING header separately
        // (non-browser request) and the WebSocket path refuses it outright.
        internal static bool IsOriginAllowed(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return false;
            if (AllowedOrigins.Contains(origin)) return true;
            return ExtraOrigins().Contains(origin);
        }

        // Exact origins only - no wildcards, never "*" or "null".
        private static HashSet<string> ExtraOrigins()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var raw = Environment.GetEnvironmentVariable("CLASHCONTROL_ALLOWED_ORIGINS");
            if (string.IsNullOrWhiteSpace(raw)) return set;
            foreach (var part in raw.Split(','))
            {
                var o = part.Trim().TrimEnd('/');
                if (o.Length == 0 || o == "*" || o.Equals("null", StringComparison.OrdinalIgnoreCase)) continue;
                set.Add(o);
            }
            return set;
        }

        // Host header must name a loopback address (any port).
        internal static bool IsHostAllowed(string hostHeader)
        {
            if (string.IsNullOrWhiteSpace(hostHeader)) return false;
            var host = hostHeader.Trim().ToLowerInvariant();
            if (host.StartsWith("["))
            {
                var end = host.IndexOf(']');
                if (end >= 0) host = host.Substring(0, end + 1);
            }
            else if (host.Count(c => c == ':') == 1)
            {
                host = host.Substring(0, host.IndexOf(':'));
            }
            return host == "127.0.0.1" || host == "localhost" || host == "[::1]" || host == "::1";
        }

        private static void Respond(HttpListenerResponse response, int status, string json, string allowedOrigin)
        {
            try
            {
                response.StatusCode = status;
                if (!string.IsNullOrEmpty(allowedOrigin))
                {
                    // Echo the already-validated origin; never a wildcard.
                    response.Headers.Add("Access-Control-Allow-Origin", allowedOrigin);
                    response.Headers.Add("Vary", "Origin");
                }
                if (json != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(json);
                    response.ContentType = "application/json";
                    response.ContentLength64 = bytes.Length;
                    response.OutputStream.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    response.ContentLength64 = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CC] Respond error: {ex.Message}");
            }
            finally
            {
                try { response.Close(); } catch { }
            }
        }

        #endregion
    }
}
