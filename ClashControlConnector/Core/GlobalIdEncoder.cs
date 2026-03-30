using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace ClashControlConnector.Core
{
    /// <summary>
    /// Converts Revit element UniqueIds to 22-character IFC GlobalIds.
    /// Uses the same algorithm as Revit's built-in IFC exporter:
    /// XOR the EpisodeId GUID with the element ID suffix.
    /// </summary>
    public static class GlobalIdEncoder
    {
        private static readonly char[] Base64Chars =
            "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$".ToCharArray();

        public static string ToIfcGlobalId(Guid guid)
        {
            var bytes = guid.ToByteArray();

            // Rearrange bytes to match IFC encoding order (big-endian groups)
            var num = new byte[16];
            num[0] = bytes[3]; num[1] = bytes[2]; num[2] = bytes[1]; num[3] = bytes[0];
            num[4] = bytes[5]; num[5] = bytes[4];
            num[6] = bytes[7]; num[7] = bytes[6];
            Array.Copy(bytes, 8, num, 8, 8);

            var result = new char[22];
            int offset = 0;

            // Encode 16 bytes (128 bits) into 22 base64 characters
            result[offset++] = Base64Chars[(num[0] & 0xFC) >> 2];
            result[offset++] = Base64Chars[((num[0] & 0x03) << 4) | ((num[1] & 0xF0) >> 4)];

            for (int i = 1; i < 15; i += 3)
            {
                if (i + 2 < 16)
                {
                    result[offset++] = Base64Chars[((num[i] & 0x0F) << 2) | ((num[i + 1] & 0xC0) >> 6)];
                    result[offset++] = Base64Chars[num[i + 1] & 0x3F];
                    result[offset++] = Base64Chars[(num[i + 2] & 0xFC) >> 2];
                    if (i + 3 < 16)
                        result[offset++] = Base64Chars[((num[i + 2] & 0x03) << 4) | ((num[i + 3] & 0xF0) >> 4)];
                    else
                        result[offset++] = Base64Chars[(num[i + 2] & 0x03) << 4];
                }
                else if (i + 1 < 16)
                {
                    result[offset++] = Base64Chars[((num[i] & 0x0F) << 2) | ((num[i + 1] & 0xC0) >> 6)];
                    result[offset++] = Base64Chars[num[i + 1] & 0x3F];
                }
                else
                {
                    result[offset++] = Base64Chars[(num[i] & 0x0F) << 2];
                }
            }

            return new string(result, 0, 22);
        }

        /// <summary>
        /// Derives the IFC GlobalId from a Revit element's UniqueId.
        /// Revit UniqueId format: "{EpisodeId}-{last_8_hex_of_element_id}"
        /// The IFC GUID is derived by XOR-ing the last 4 bytes of the EpisodeId
        /// with the element ID, matching Revit's built-in IFC exporter behavior.
        /// </summary>
        public static string FromElement(Element element)
        {
            string uniqueId = element.UniqueId;

            int lastDash = uniqueId.LastIndexOf('-');
            string guidPart = uniqueId.Substring(0, lastDash);
            string elementSuffix = uniqueId.Substring(lastDash + 1);

            if (!Guid.TryParse(guidPart, out var episodeGuid))
            {
                Debug.WriteLine($"[CC] WARNING: Could not parse EpisodeId from UniqueId: {uniqueId}");
                using (var md5 = MD5.Create())
                {
                    var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(uniqueId));
                    return ToIfcGlobalId(new Guid(hash));
                }
            }

            // XOR the element ID into the last 4 bytes of the GUID
            uint elementIdBits = Convert.ToUInt32(elementSuffix, 16);
            var guidBytes = episodeGuid.ToByteArray();

            guidBytes[12] ^= (byte)((elementIdBits >> 24) & 0xFF);
            guidBytes[13] ^= (byte)((elementIdBits >> 16) & 0xFF);
            guidBytes[14] ^= (byte)((elementIdBits >> 8) & 0xFF);
            guidBytes[15] ^= (byte)(elementIdBits & 0xFF);

            return ToIfcGlobalId(new Guid(guidBytes));
        }
    }
}
