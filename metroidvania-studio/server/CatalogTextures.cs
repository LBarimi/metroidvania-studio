using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace MetroidvaniaStudio.Server;

public sealed partial class Catalog
{
    private sealed record TextureStamp(string Path, long Length, DateTime Modified, string Hash, long CheckedAt);
    private sealed record TextureProbe(JsonElement Source, Dictionary<string, TextureStamp> Files);
    private Dictionary<string, TextureStamp> textureFiles = new(StringComparer.Ordinal);
    private Task<TextureProbe>? textureProbe;
    private long nextTextureProbeAt;
    private const long MaximumTextureBytes = 32L * 1024 * 1024;
    private static readonly uint[] PngCrcTable = CreatePngCrcTable();

    // Called under the workspace gate. Only completed results are consumed here;
    // metadata, content hashes and image validation run on a single background worker.
    public bool RefreshTextures()
    {
        bool changed = false;
        if (textureProbe is { IsCompleted: true })
        {
            TextureProbe result = textureProbe.GetAwaiter().GetResult(); textureProbe = null;
            if (result.Source.Equals(Data))
            {
                changed = result.Files.Any(pair => !textureFiles.TryGetValue(pair.Key, out var old) || old.Hash != pair.Value.Hash);
                textureFiles = result.Files;
            }
        }
        if (textureProbe == null && Environment.TickCount64 >= nextTextureProbeAt)
        {
            JsonElement source = Data;
            var previous = textureFiles;
            textureProbe = Task.Run(() => ProbeTextures(source, previous));
            nextTextureProbeAt = Environment.TickCount64 + 500;
        }
        return changed;
    }

    private TextureProbe ProbeTextures(JsonElement source, Dictionary<string, TextureStamp> previous)
    {
        var assets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var material in source.GetProperty("materials").EnumerateArray())
        {
            foreach (var sprite in material.GetProperty("sprites").EnumerateArray()) Add(sprite);
            if (material.TryGetProperty("editorTileset", out var settings) && settings.ValueKind == JsonValueKind.Object
                && settings.TryGetProperty("source", out var original) && original.ValueKind == JsonValueKind.String && original.GetString() is { Length: > 0 } asset)
                assets.Add(asset);
            if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
                foreach (var slot in slots.EnumerateArray()) if (slot.ValueKind == JsonValueKind.Object) Add(slot);
        }
        foreach (var item in source.GetProperty("objects").EnumerateArray())
            if (item.TryGetProperty("sprite", out var sprite) && sprite.ValueKind == JsonValueKind.Object) Add(sprite);
        void Add(JsonElement sprite)
        {
            if (sprite.TryGetProperty("asset", out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } asset) assets.Add(asset);
        }
        var result = new Dictionary<string, TextureStamp>(StringComparer.Ordinal);
        foreach (string asset in assets)
        {
            previous.TryGetValue(asset, out var old);
            // A temporarily missing, locked or half-written image retains its last
            // published identity. The next scan retries it, including atomic replaces.
            if (old != null) result[asset] = old;
            try
            {
                string path = files.Asset(asset);
                if (old != null && old.Path != path && !File.Exists(old.Path)) continue;
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0 || info.Length > MaximumTextureBytes) continue;
                long now = Environment.TickCount64;
                if (old != null && old.Path == path && old.Length == info.Length && old.Modified == info.LastWriteTimeUtc
                    && now - old.CheckedAt < ContentProbeIntervalMilliseconds) continue;
                long length = info.Length; DateTime modified = info.LastWriteTimeUtc;
                byte[] bytes = ReadTexture(path, length, modified);
                if (!CompleteImage(bytes, Path.GetExtension(path))) continue;
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (old?.Hash != hash)
                {
                    byte[] confirmation = ReadTexture(path, length, modified);
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(confirmation))) continue;
                }
                result[asset] = new TextureStamp(path, length, modified, hash, now);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        RefreshDerivedTilesets(source, previous, result);
        return new TextureProbe(source, result);
    }

    private static byte[] ReadTexture(string path, long length, DateTime modified)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length != length) throw new IOException("Texture changed during read.");
        byte[] bytes = new byte[checked((int)length)]; stream.ReadExactly(bytes);
        var after = new FileInfo(path);
        if (stream.ReadByte() != -1 || !after.Exists || after.Length != length || after.LastWriteTimeUtc != modified)
            throw new IOException("Texture changed during read.");
        return bytes;
    }

    internal static bool CompleteImage(byte[] bytes, string extension)
    {
        ReadOnlySpan<byte> data = bytes;
        switch (extension.ToLowerInvariant())
        {
            case ".png":
                if (!data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
                bool header = false, pixels = false;
                for (int offset = 8; offset <= data.Length - 12;)
                {
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
                    if (count > data.Length - offset - 12) return false;
                    int size = (int)count; var chunk = data.Slice(offset + 4, size + 4); var type = chunk[..4];
                    uint crc = uint.MaxValue;
                    foreach (byte value in chunk) crc = PngCrcTable[(crc ^ value) & 255] ^ (crc >> 8);
                    if (~crc != BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + size + 8, 4))) return false;
                    if (!header)
                    {
                        if (!type.SequenceEqual("IHDR"u8) || size != 13) return false;
                        uint width = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]), height = BinaryPrimitives.ReadUInt32BigEndian(chunk[8..]);
                        if (width is 0 or > 16384 || height is 0 or > 16384) return false;
                        header = true;
                    }
                    if (type.SequenceEqual("IDAT"u8)) pixels = true;
                    offset += size + 12;
                    if (type.SequenceEqual("IEND"u8)) return pixels && size == 0 && offset == data.Length;
                }
                return false;
            case ".jpg": case ".jpeg": return data.Length >= 4 && data[0] == 255 && data[1] == 216 && data[^2] == 255 && data[^1] == 217;
            case ".gif": return data.Length >= 14 && (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) && data[^1] == 59;
            case ".webp": return data.Length >= 12 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)
                && BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) == data.Length - 8;
            default: return false;
        }
    }

    private static uint[] CreatePngCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xEDB88320u);
            table[index] = value;
        }
        return table;
    }
}
