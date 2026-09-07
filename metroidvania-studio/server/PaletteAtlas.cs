using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MetroidvaniaStudio;

namespace MetroidvaniaStudio.Server;

/// <summary>Small native-size template atlas, portable to every catalog consumer.</summary>
public static class PaletteAtlas
{
    public sealed record Result(byte[] Png, List<SpriteData> Sprites);
    public static Result Create(string asset, string color)
    {
        const int size = 16, columns = 8, width = size * columns, height = size * 7;
        byte[] rgb = Convert.FromHexString(color[1..]);
        byte[] pixels = new byte[width * height * 4];
        var sprites = new List<SpriteData>();
        int[] masks = TileMask.GetValidMasks();
        for (int index = 0; index < masks.Length + 4; index++)
        {
            int shape = index < masks.Length ? 0 : index - masks.Length + 1;
            int mask = shape == 0 ? masks[index] : 0;
            int left = index % columns * size, bottom = index / columns * size;
            sprites.Add(new SpriteData { asset = asset, x = left, y = bottom, width = size, height = size, mask = mask, shape = shape });
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                bool filled = shape switch { 1 => x + y <= 15, 2 => y <= x, 3 => y >= x, 4 => x + y >= 15, _ => true };
                if (!filled) continue;
                bool edge = shape switch
                {
                    1 or 4 => x + y == 15,
                    2 or 3 => x == y,
                    _ => x == 0 && (mask & TileMask.W) == 0 || x == 15 && (mask & TileMask.E) == 0
                        || y == 0 && (mask & TileMask.S) == 0 || y == 15 && (mask & TileMask.N) == 0
                        || x == 0 && y == 0 && (mask & TileMask.SW) == 0
                        || x == 15 && y == 0 && (mask & TileMask.SE) == 0
                        || x == 0 && y == 15 && (mask & TileMask.NW) == 0
                        || x == 15 && y == 15 && (mask & TileMask.NE) == 0
                };
                int offset = ((height - 1 - bottom - y) * width + left + x) * 4;
                for (int c = 0; c < 3; c++) pixels[offset + c] = edge ? (byte)255 : rgb[c];
                pixels[offset + 3] = 255;
            }
        }
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6;
        Chunk(png, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            for (int y = 0; y < height; y++) { zlib.WriteByte(0); zlib.Write(pixels, y * width * 4, width * 4); }
        Chunk(png, "IDAT", compressed.ToArray()); Chunk(png, "IEND", []);
        return new Result(png.ToArray(), sprites);
    }
    private static void Chunk(Stream output, string type, byte[] data)
    {
        byte[] label = Encoding.ASCII.GetBytes(type);
        Span<byte> value = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(value, data.Length);
        output.Write(value); output.Write(label); output.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte b in label.Concat(data))
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(value, ~crc); output.Write(value);
    }
}
