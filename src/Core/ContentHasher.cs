using System.Security.Cryptography;
using System.Text;
using ClashControlConnector.Protocol;

namespace ClashControlConnector.Core
{
    /// <summary>
    /// Computes a content hash for an element combining geometry and key properties.
    /// Used for content-addressable caching to detect unchanged elements on re-export.
    /// </summary>
    public static class ContentHasher
    {
        public static string ComputeHash(ElementData data)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                sb.Append(data.GlobalId ?? "");
                sb.Append('|');
                sb.Append(data.Name ?? "");
                sb.Append('|');
                sb.Append(data.Category ?? "");
                sb.Append('|');
                sb.Append(data.Level ?? "");
                sb.Append('|');
                sb.Append(data.Type ?? "");
                sb.Append('|');
                sb.Append(data.Geometry?.Positions ?? "");
                sb.Append('|');
                sb.Append(data.Geometry?.Indices ?? "");
                sb.Append('|');
                sb.Append(data.Geometry?.Normals ?? "");

                // Colors aren't in the position/normal buffers, so fold them in
                // explicitly — otherwise a pure material-color change (same geometry)
                // would hash identical and get skipped as "unchanged" on a delta pull.
                if (data.Geometry?.Color != null)
                {
                    sb.Append('|');
                    sb.Append(string.Join(",", data.Geometry.Color));
                }
                if (data.Geometry?.Groups != null)
                {
                    sb.Append('|');
                    foreach (var g in data.Geometry.Groups)
                    {
                        sb.Append(g.Start);
                        sb.Append(':');
                        sb.Append(g.Count);
                        sb.Append(':');
                        if (g.Color != null) sb.Append(string.Join(",", g.Color));
                        sb.Append(';');
                    }
                }

                if (data.Materials != null)
                {
                    sb.Append('|');
                    sb.Append(string.Join(",", data.Materials));
                }

                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                // Return first 8 bytes as hex (16 chars) — enough for collision avoidance
                var hex = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                    hex.Append(bytes[i].ToString("x2"));
                return hex.ToString();
            }
        }
    }
}
