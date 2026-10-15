using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClashControlConnector.Core;

namespace WsSecurityTests
{
    internal static class Program
    {
        private const string App = "https://www.clashcontrol.io";
        private const string Evil = "https://evil.example.com";
        private static int _passed, _failed;

        private static void Check(string name, bool ok, string detail = null)
        {
            if (ok) { _passed++; Console.WriteLine("PASS " + name); }
            else { _failed++; Console.WriteLine("FAIL " + name + (detail != null ? "  -> " + detail : "")); }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private static async Task<(int status, string body, HttpResponseMessage res)> Send(
            HttpClient http, int port, string method, string path, string origin = null, string host = null, string token = null)
        {
            var req = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{port}{path}");
            if (origin != null) req.Headers.TryAddWithoutValidation("Origin", origin);
            if (host != null) req.Headers.Host = host;
            if (token != null) req.Headers.TryAddWithoutValidation("X-CC-Pairing-Token", token);
            var res = await http.SendAsync(req);
            return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(), res);
        }

        private static string Header(HttpResponseMessage r, string name)
        {
            IEnumerable<string> v;
            return r.Headers.TryGetValues(name, out v) ? string.Join(",", v) : null;
        }

        // Returns "open" or the HTTP status that refused the upgrade.
        private static async Task<string> WsOutcome(int port, string origin, string token)
        {
            using (var ws = new ClientWebSocket())
            {
                if (origin != null) ws.Options.SetRequestHeader("Origin", origin);
                var url = $"ws://127.0.0.1:{port}/" + (token != null ? "?cc_token=" + token : "");
                try
                {
                    using (var cts = new CancellationTokenSource(5000))
                        await ws.ConnectAsync(new Uri(url), cts.Token);
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                    return "open";
                }
                catch (WebSocketException e)
                {
                    var m = Regex.Match(e.Message, @"\b(\d{3})\b");
                    return m.Success ? m.Groups[1].Value : "error:" + e.Message;
                }
            }
        }

        private static async Task<int> Main()
        {
            Environment.SetEnvironmentVariable("CLASHCONTROL_ALLOWED_ORIGINS", null);

            // ---- pure logic -------------------------------------------------
            foreach (var (o, want) in new[] {
                (App, true), ("https://clashcontrol.io", true),
                ("https://WWW.clashcontrol.io", true),                 // origins are case-insensitive
                ("http://localhost:3000", false), ("http://127.0.0.1:5173", false),
                ("http://localhost", false), ("https://clashcontrol-io.github.io", false),
                ("http://www.clashcontrol.io", false), ("https://clashcontrol.io.evil.com", false),
                ("https://xclashcontrol.io", false), ("null", false), ("NULL", false), ("", false), ((string)null, false) })
                Check($"origin {(o ?? "<absent>")} -> {want}", WsServer_IsOriginAllowed(o) == want);

            foreach (var (h, want) in new[] {
                ("127.0.0.1", true), ("127.0.0.1:19780", true), ("localhost:19780", true), ("LOCALHOST", true),
                ("[::1]:19780", true), ("[::1]", true), ("attacker.example", false), ("attacker.example:19780", false),
                ("127.0.0.1.evil.com", false), ("", false), ((string)null, false) })
                Check($"host {(h ?? "<absent>")} -> {want}", WsServer_IsHostAllowed(h) == want);

            Environment.SetEnvironmentVariable("CLASHCONTROL_ALLOWED_ORIGINS", "http://localhost:8000, https://self.example/ ,*,null");
            Check("extra origin from env is allowed", WsServer_IsOriginAllowed("http://localhost:8000"));
            Check("extra origin (trailing slash trimmed) is allowed", WsServer_IsOriginAllowed("https://self.example"));
            Check("wildcard in env is ignored", !WsServer_IsOriginAllowed("*"));
            Check("\"null\" in env is ignored", !WsServer_IsOriginAllowed("null"));
            Check("unlisted localhost port still refused", !WsServer_IsOriginAllowed("http://localhost:9999"));
            Environment.SetEnvironmentVariable("CLASHCONTROL_ALLOWED_ORIGINS", null);

            // ---- pairing store ----------------------------------------------
            var st = new PairingStore();
            var a = st.Issue(); var b = st.Issue();
            Check("tokens are unique and long", a != b && a.Length >= 40);
            Check("issued tokens validate", st.IsValid(a) && st.IsValid(b));
            Check("junk tokens rejected", !st.IsValid(null) && !st.IsValid("") && !st.IsValid("nope"));
            var now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var exp = new PairingStore(() => now, TimeSpan.FromSeconds(60));
            var t = exp.Issue();
            now = now.AddSeconds(59); var stillOk = exp.IsValid(t);
            now = now.AddSeconds(2);  var expired = !exp.IsValid(t);
            Check("token valid just before expiry", stillOk);
            Check("token rejected after expiry", expired);
            var cap = new PairingStore();
            var tokens = new List<string>();
            for (int i = 0; i < PairingStore.MaxActiveTokens + 3; i++) { tokens.Add(cap.Issue()); System.Threading.Thread.Sleep(2); }
            Check("active tokens are capped (oldest evicted)", !cap.IsValid(tokens[0]) && cap.IsValid(tokens[tokens.Count - 1]));

            // ---- the real server over real sockets --------------------------
            var port = FreePort();
            var server = new WsServer(port);
            Check("server starts", server.Start());
            var http = new HttpClient();
            try
            {
                // hostile origin: 403 everywhere, no CORS grant
                foreach (var (m, p) in new[] { ("GET", "/"), ("POST", "/pair"), ("OPTIONS", "/"), ("POST", "/anything") })
                {
                    var r = await Send(http, port, m, p, origin: Evil);
                    Check($"hostile origin {m} {p} -> 403", r.status == 403, r.status.ToString());
                    Check($"hostile origin {m} {p} gets no CORS header", Header(r.res, "Access-Control-Allow-Origin") == null);
                }
                Check("\"null\" origin -> 403", (await Send(http, port, "GET", "/", origin: "null")).status == 403);
                Check("localhost dev origin no longer trusted -> 403", (await Send(http, port, "GET", "/", origin: "http://localhost:5173")).status == 403);
                Check("empty Origin header -> 403", (await Send(http, port, "GET", "/", origin: "")).status == 403);

                // DNS rebinding
                // The listener's loopback-only prefixes already refuse a foreign Host
                // (404 here, 400 from http.sys on Windows) before our code runs; our own
                // check (421) is defence in depth. Either way: refused, nothing served.
                var rb = await Send(http, port, "GET", "/", host: "attacker.example");
                Check("non-loopback Host is refused and serves nothing",
                    (rb.status == 400 || rb.status == 404 || rb.status == 421) && !rb.body.Contains("connectorVersion"),
                    "got " + rb.status);

                // preflight from the app
                var pre = await Send(http, port, "OPTIONS", "/", origin: App);
                Check("preflight -> 204", pre.status == 204, pre.status.ToString());
                Check("preflight echoes the exact origin, not *", Header(pre.res, "Access-Control-Allow-Origin") == App);
                Check("preflight answers Private Network Access", Header(pre.res, "Access-Control-Allow-Private-Network") == "true");
                Check("preflight allows the pairing header", (Header(pre.res, "Access-Control-Allow-Headers") ?? "").Contains("X-CC-Pairing-Token"));

                // status probe
                var st1 = await Send(http, port, "GET", "/", origin: App);
                Check("status probe works for the app", st1.status == 200 && st1.body.Contains("\"status\":\"ok\""));
                Check("status CORS is the exact origin, never *", Header(st1.res, "Access-Control-Allow-Origin") == App);
                var st2 = await Send(http, port, "GET", "/");
                Check("origin-less probe (no-cors detect) still answered", st2.status == 200);
                Check("origin-less probe gets no CORS header", Header(st2.res, "Access-Control-Allow-Origin") == null);

                // pairing
                Check("POST /pair without Origin -> 400", (await Send(http, port, "POST", "/pair")).status == 400);
                var pr = await Send(http, port, "POST", "/pair", origin: App);
                Check("POST /pair from the app -> 200", pr.status == 200);
                var token = Regex.Match(pr.body, "\"token\":\"([^\"]+)\"").Groups[1].Value;
                Check("pair reply carries a token and lifetime", token.Length >= 40 && pr.body.Contains("\"expiresIn\":3600"));
                Check("pair reply is CORS-readable by the app only", Header(pr.res, "Access-Control-Allow-Origin") == App);

                // websocket gate
                Check("WS: hostile origin refused", await WsOutcome(port, Evil, token) == "403");
                Check("WS: \"null\" origin refused", await WsOutcome(port, "null", token) == "403");
                Check("WS: no Origin refused even with a valid token", await WsOutcome(port, null, token) == "403");
                Check("WS: app origin without token refused", await WsOutcome(port, App, null) == "401");
                Check("WS: app origin with bad token refused", await WsOutcome(port, App, "nope") == "401");
                var ok = await WsOutcome(port, App, token);
                Check("WS: app origin + paired token connects", ok == "open", ok);
            }
            finally { server.Dispose(); http.Dispose(); }

            Console.WriteLine($"\n{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // The members under test are internal to the linked source; call them through the same assembly.
        private static bool WsServer_IsOriginAllowed(string o) => ClashControlConnector.Core.WsServer.IsOriginAllowed(o);
        private static bool WsServer_IsHostAllowed(string h) => ClashControlConnector.Core.WsServer.IsHostAllowed(h);
    }
}
