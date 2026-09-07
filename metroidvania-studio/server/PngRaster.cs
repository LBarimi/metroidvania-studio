using System.Buffers.Binary;
using System.IO.Compression;

namespace MetroidvaniaStudio.Server;

// Bounded PNG pixels for offline atlas composition; no platform graphics dependency.
internal sealed record PngRaster(int Width, int Height, byte[] Pixels)
{
    public const int MaximumBytes = 8 * 1024 * 1024, MaximumPixels = 2048 * 2048;
    public static PngRaster Decode(byte[] png)
    {
        if (png.Length > MaximumBytes || !Catalog.CompleteImage(png, ".png")) throw new InvalidDataException("@tilesetInvalidPng");
        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)), height = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20));
        int depth = png[24], type = png[25], interlace = png[28];
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels || png[26] != 0 || png[27] != 0 || interlace > 1)
            throw new InvalidDataException("@tilesetImageLimit");
        int channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        bool validDepth = type is 0 or 3 ? depth is 1 or 2 or 4 or 8 || type == 0 && depth == 16 : depth is 8 or 16;
        if (channels == 0 || !validDepth) throw new InvalidDataException("@tilesetInvalidPng");
        byte[] palette = [], alpha = [];
        using var compressed = new MemoryStream();
        for (int offset = 8; offset < png.Length;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            var chunk = png.AsSpan(offset + 4, 4); var data = png.AsSpan(offset + 8, length);
            if (chunk.SequenceEqual("IDAT"u8)) compressed.Write(data);
            else if (chunk.SequenceEqual("PLTE"u8)) palette = data.ToArray();
            else if (chunk.SequenceEqual("tRNS"u8)) alpha = data.ToArray();
            else if (!chunk.SequenceEqual("IHDR"u8) && !chunk.SequenceEqual("IEND"u8) && (chunk[0] & 32) == 0)
                throw new InvalidDataException("@tilesetInvalidPng");
            offset += length + 12;
        }
        if (type == 3 && (palette.Length == 0 || palette.Length % 3 != 0 || palette.Length > 768 || alpha.Length > palette.Length / 3))
            throw new InvalidDataException("@tilesetInvalidPng");
        var image = new PngRaster(width, height, new byte[width * height * 4]);
        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        (int x, int y, int dx, int dy)[] passes = interlace == 0 ? [(0, 0, 1, 1)]
            : [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
        foreach (var pass in passes)
        {
            int columns = Math.Max(0, (width - pass.x + pass.dx - 1) / pass.dx);
            int rows = Math.Max(0, (height - pass.y + pass.dy - 1) / pass.dy);
            if (columns == 0 || rows == 0) continue;
            int stride = (columns * channels * depth + 7) / 8, bpp = Math.Max(1, (channels * depth + 7) / 8);
            byte[] row = new byte[stride], prior = new byte[stride];
            for (int y = 0; y < rows; y++)
            {
                int filter = inflater.ReadByte();
                if (filter < 0 || filter > 4) throw new InvalidDataException("@tilesetInvalidPng");
                inflater.ReadExactly(row);
                for (int i = 0; i < stride; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0, up = prior[i], corner = i >= bpp ? prior[i - bpp] : 0;
                    row[i] = unchecked((byte)(row[i] + (filter switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, _ => Paeth(left, up, corner) })));
                }
                for (int x = 0; x < columns; x++)
                {
                    int sample = x * channels;
                    int a = Sample(row, sample, depth), b = channels > 1 ? Sample(row, sample + 1, depth) : 0;
                    int c = channels > 2 ? Sample(row, sample + 2, depth) : 0, d = channels > 3 ? Sample(row, sample + 3, depth) : 0;
                    int dest = ((pass.y + y * pass.dy) * width + pass.x + x * pass.dx) * 4;
                    if (type == 3)
                    {
                        if (a * 3 + 2 >= palette.Length) throw new InvalidDataException("@tilesetInvalidPng");
                        image.Pixels[dest] = palette[a * 3]; image.Pixels[dest + 1] = palette[a * 3 + 1]; image.Pixels[dest + 2] = palette[a * 3 + 2];
                        image.Pixels[dest + 3] = a < alpha.Length ? alpha[a] : (byte)255;
                    }
                    else
                    {
                        image.Pixels[dest] = Byte(a, depth);
                        image.Pixels[dest + 1] = Byte(type is 0 or 4 ? a : b, depth);
                        image.Pixels[dest + 2] = Byte(type is 0 or 4 ? a : c, depth);
                        bool transparent = type == 0 && alpha.Length == 2 && a == BinaryPrimitives.ReadUInt16BigEndian(alpha)
                            || type == 2 && alpha.Length == 6 && a == BinaryPrimitives.ReadUInt16BigEndian(alpha)
                            && b == BinaryPrimitives.ReadUInt16BigEndian(alpha.AsSpan(2)) && c == BinaryPrimitives.ReadUInt16BigEndian(alpha.AsSpan(4));
                        image.Pixels[dest + 3] = transparent ? (byte)0 : type == 4 ? Byte(b, depth) : type == 6 ? Byte(d, depth) : (byte)255;
                    }
                }
                (row, prior) = (prior, row);
            }
        }
        if (inflater.ReadByte() != -1) throw new InvalidDataException("@tilesetInvalidPng");
        return image;
    }
    private static int Sample(byte[] row, int sample, int depth) => depth == 16 ? BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(sample * 2))
        : depth == 8 ? row[sample] : row[sample * depth / 8] >> (8 - depth - sample * depth % 8) & (1 << depth) - 1;
    private static byte Byte(int sample, int depth) => depth == 16 ? (byte)(sample >> 8) : (byte)(sample * 255 / ((1 << depth) - 1));
    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
    public byte[] Encode()
    {
        using var output = new MemoryStream(); output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, Width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
        header[8] = 8; header[9] = 6; PaletteAtlas.Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
            for (int y = 0; y < Height; y++) { zlib.WriteByte(0); zlib.Write(Pixels, y * Width * 4, Width * 4); }
        PaletteAtlas.Chunk(output, "IDAT", compressed.ToArray()); PaletteAtlas.Chunk(output, "IEND", []);
        return output.ToArray();
    }
    public void CopyPixel(PngRaster source, int sx, int sy, int x, int y)
        => Buffer.BlockCopy(source.Pixels, (sy * source.Width + sx) * 4, Pixels, (y * Width + x) * 4, 4);
}
