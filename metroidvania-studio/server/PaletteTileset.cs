using MetroidvaniaStudio;

namespace MetroidvaniaStudio.Server;

public sealed record TilesetSlot(int x, int y, string? asset = null);
public sealed record PaletteTileset(string mode, string source, TilesetSlot?[] slots, string atlasHash = "");

internal static class TilesetComposer
{
    internal static readonly int[] Masks = TileMask.GetValidMasks();
    private static readonly int[] CanonicalMasks = [124, 112, 127];
    public static PaletteAtlas.Result Compose(string asset, string color, PaletteTileset settings, PngRaster? source, IReadOnlyDictionary<string, PngRaster>? sources = null)
    {
        var template = PaletteAtlas.Create(asset, color);
        if (settings.mode == "template") return template;
        if (settings.mode is not ("four" or "blob47") || source == null) throw new ArgumentException("@tilesetInvalidSettings");
        int count = settings.mode == "four" ? 8 : 51;
        if (settings.slots.Length != count) throw new ArgumentException("@tilesetInvalidSettings");
        foreach (var slot in settings.slots)
            if (slot != null)
            {
                var pixels = Source(slot);
                if (slot.x < 0 || slot.y < 0 || slot.x > pixels.Width - 16 || slot.y > pixels.Height - 16) throw new ArgumentException("@tilesetSlotOutside");
            }
        if (settings.mode == "four" && settings.slots.Take(4).Any(slot => slot == null)) throw new ArgumentException("@tilesetFourRequired");
        var target = PngRaster.Decode(template.Png);
        for (int index = 0; index < 51; index++)
        {
            var sprite = template.Sprites[index]; int left = sprite.x, top = target.Height - sprite.y - 16;
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
            {
                var part = settings.mode == "four" && index < 47 ? FourPart(Masks[index], x, y) : (slot: settings.mode == "four" ? index - 43 : index, rotation: 0);
                int slotIndex = part.slot;
                var slot = settings.slots[slotIndex]; if (slot == null) continue;
                int sx = x, sy = y;
                (sx, sy) = part.rotation switch { 1 => (y, 15 - x), 2 => (15 - x, 15 - y), 3 => (15 - y, x), _ => (x, y) };
                target.CopyPixel(Source(slot), slot.x + sx, slot.y + sy, left + x, top + y);
            }
        }
        return new PaletteAtlas.Result(target.Encode(), template.Sprites);
        PngRaster Source(TilesetSlot slot) => string.IsNullOrEmpty(slot.asset) ? source! : sources != null && sources.TryGetValue(slot.asset, out var value)
            ? value : throw new ArgumentException("@tilesetChooseImage");
    }
    internal static (int slot, int rotation) FourPart(int mask, int x, int y)
    {
        // Preserve entire source sprites for the ordinary edge, convex corner and concave corner.
        for (int slot = 0; slot < CanonicalMasks.Length; slot++) for (int turn = 0; turn < 4; turn++)
        {
            int shift = turn * 2, rotated = (CanonicalMasks[slot] << shift | CanonicalMasks[slot] >> (8 - shift)) & 255;
            if (mask == rotated) return (slot, turn);
        }
        bool horizontal = (mask & (x < 8 ? TileMask.W : TileMask.E)) != 0, vertical = (mask & (y < 8 ? TileMask.N : TileMask.S)) != 0;
        int diagonal = y < 8 ? x < 8 ? TileMask.NW : TileMask.NE : x < 8 ? TileMask.SW : TileMask.SE;
        if (!horizontal && !vertical) return (1, y < 8 ? x < 8 ? 3 : 0 : x < 8 ? 2 : 1);
        if (horizontal && vertical) return (mask & diagonal) != 0 ? (3, 0) : (2, y < 8 ? x < 8 ? 0 : 1 : x < 8 ? 3 : 2);
        return (0, !vertical ? y < 8 ? 0 : 2 : x < 8 ? 3 : 1);
    }
    public static byte[] Template(string mode, string color)
    {
        var result = PaletteAtlas.Create("", color);
        if (mode == "blob47")
        {
            var pixels = PngRaster.Decode(result.Png); var ordered = new PngRaster(128, 112, new byte[128 * 112 * 4]);
            for (int i = 0; i < 51; i++) for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
                ordered.CopyPixel(pixels, result.Sprites[i].x + x, pixels.Height - result.Sprites[i].y - 16 + y, i % 8 * 16 + x, i / 8 * 16 + y);
            return ordered.Encode();
        }
        if (mode != "four") throw new ArgumentException("@tilesetInvalidSettings");
        var source = PngRaster.Decode(result.Png); var target = new PngRaster(64, 32, new byte[64 * 32 * 4]);
        int[] indices = [Array.IndexOf(Masks, 124), Array.IndexOf(Masks, 112), Array.IndexOf(Masks, 127), Array.IndexOf(Masks, 255), 47, 48, 49, 50];
        for (int i = 0; i < 8; i++)
        {
            var sprite = result.Sprites[indices[i]];
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
                target.CopyPixel(source, sprite.x + x, source.Height - sprite.y - 16 + y, i % 4 * 16 + x, i / 4 * 16 + y);
        }
        return target.Encode();
    }
}
