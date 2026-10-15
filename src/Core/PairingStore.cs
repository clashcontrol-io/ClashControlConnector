using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ClashControlConnector.Core
{
    /// <summary>
    /// Short-lived pairing tokens for the localhost WebSocket.
    ///
    /// The browser app obtains a token from <c>POST /pair</c> (only answered to an
    /// allow-listed Origin, and only readable by that origin through CORS) and
    /// presents it as <c>?cc_token=</c> on the WebSocket upgrade. Geometry is only
    /// ever streamed to a socket that passed this check, so a page that merely
    /// manages to reach 127.0.0.1 gets nothing.
    ///
    /// Tokens live in memory only, expire after <see cref="Ttl"/>, and are stored
    /// as SHA-256 digests. Compatible with both .NET Framework 4.8 (Revit 2024)
    /// and .NET 8 (Revit 2025+): no APIs newer than net48 are used.
    /// </summary>
    internal sealed class PairingStore
    {
        public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
        public const int MaxActiveTokens = 32; // roughly: one per open browser tab

        private readonly object _lock = new object();
        private readonly Dictionary<string, DateTime> _tokens = new Dictionary<string, DateTime>(); // sha256 hex -> expiry (UTC)
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _ttl;

        public PairingStore(Func<DateTime> utcNow = null, TimeSpan? ttl = null)
        {
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _ttl = ttl ?? Ttl;
        }

        public int LifetimeSeconds => (int)_ttl.TotalSeconds;

        /// <summary>Create a new random token (256 bits, URL-safe base64).</summary>
        public string Issue()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            lock (_lock)
            {
                Purge();
                while (_tokens.Count >= MaxActiveTokens)
                {
                    var oldest = _tokens.OrderBy(kv => kv.Value).First().Key;
                    _tokens.Remove(oldest);
                }
                _tokens[Digest(token)] = _utcNow() + _ttl;
            }
            return token;
        }

        public bool IsValid(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            lock (_lock)
            {
                Purge();
                return _tokens.ContainsKey(Digest(token));
            }
        }

        private void Purge()
        {
            var now = _utcNow();
            foreach (var key in _tokens.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                _tokens.Remove(key);
        }

        private static string Digest(string token)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(token))).Replace("-", "");
        }
    }
}
