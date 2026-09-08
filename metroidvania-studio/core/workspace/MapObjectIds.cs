using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MetroidvaniaStudio
{
    /// <summary>Compact decimal identities. The v2 wire format remains a string for legacy ID compatibility.</summary>
    public static class MapObjectIds
    {
        public const ulong MaximumValue = (1UL << 53) - 1;

        public static string Create(HashSet<string> used = null)
        {
            Span<byte> bytes = stackalloc byte[8];
            while (true)
            {
                RandomNumberGenerator.Fill(bytes);
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(bytes) & MaximumValue;
                if (value == 0) continue;
                string id = value.ToString(CultureInfo.InvariantCulture);
                if (used == null || used.Add(id)) return id;
            }
        }

        public static string Derive(string seed, HashSet<string> used)
        {
            if (seed == null) throw new ArgumentNullException(nameof(seed));
            if (used == null) throw new ArgumentNullException(nameof(used));
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(digest) & MaximumValue;
            if (value == 0) value = 1;
            while (true)
            {
                string id = value.ToString(CultureInfo.InvariantCulture);
                if (used.Add(id)) return id;
                value = value == MaximumValue ? 1 : value + 1;
            }
        }
    }
}
