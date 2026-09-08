using System.Text.Json;
using MetroidvaniaStudio;

namespace MetroidvaniaStudio.Server;

internal static class BundledTilesets
{
    internal static string? Source(string id, string mode)
    {
        string? file = id switch
        {
            "terrain" => "default/green", "terrain-blue" => "default/blue", "terrain-dark-gray" => "default/gray",
            "terrain-orange" => "default/orange", "terrain-yellow" => "default/yellow",
            "biome-grassland" => "biomes/grassland", "biome-rock" => "biomes/rock", "biome-ice-cavern" => "biomes/ice-cavern",
            "biome-volcanic" => "biomes/volcanic", "biome-ancient-ruins" => "biomes/ancient-ruins", _ => null
        };
        if (file == null || mode is not ("four" or "blob47")) return null;
        var parts = file.Split('/'); return "Textures/" + parts[0] + (mode == "four" ? "/4-tiles/" : "/47-tiles/") + parts[1] + ".png";
    }
    internal static PaletteTileset Four(string source) => new("four", source,
        Enumerable.Range(0, 8).Select(i => (TilesetSlot?)new TilesetSlot(i % 4 * 16, i / 4 * 16)).ToArray());

    // Upgrade untouched built-ins and identical imported copies or the identifiable color-template fallback from earlier versions.
    // Explicit custom images, rules and 47-tile configurations keep their existing settings.
    public static void Prepare(ProjectFiles files, string studioRoot)
    {
        var catalog = new Catalog(files); catalog.Refresh();
        foreach (var original in catalog.Data.GetProperty("materials").EnumerateArray().ToArray())
        {
            string id = original.GetProperty("id").GetString()!;
            string? source = Source(id, "four"); if (source == null) continue;
            foreach (string mode in new[] { "four", "blob47" })
            {
                string asset = Source(id, mode)!, destination = files.TextureDestination(asset);
                if (File.Exists(destination)) continue;
                string input = files.Asset(asset); if (!File.Exists(input)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.Copy(input, temporary); File.Move(temporary, destination); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            if (!File.Exists(files.Asset(source))) continue;
            bool configure = !original.TryGetProperty("editorTileset", out var metadata) || metadata.ValueKind == JsonValueKind.Null;
            if (configure)
            {
                string expectedAsset = Source(id, "blob47")!;
                string bundled = Path.Combine(studioRoot, "samples", "textures", expectedAsset[9..]);
                string current = files.Asset(expectedAsset);
                configure = original.GetProperty("sprites").EnumerateArray().All(s => s.GetProperty("asset").GetString() == expectedAsset)
                    && File.Exists(bundled) && File.Exists(current) && new FileInfo(current).Length <= PngRaster.MaximumBytes
                    && File.ReadAllBytes(current).AsSpan().SequenceEqual(File.ReadAllBytes(bundled));
            }
            else if (id.StartsWith("biome-", StringComparison.Ordinal)) configure = CanReconnect(files, original, metadata);
            if (!configure) continue;
            catalog.ConfigurePalette(id, original.GetProperty("name").GetString()!, original.GetProperty("color").GetString()!, Four(source), null, original);
        }
    }
    private static bool CanReconnect(ProjectFiles files, JsonElement material, JsonElement metadata)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<PaletteTileset>(metadata, Catalog.Json);
            if (settings?.mode != "four" || settings.slots?.Length != 8 || string.IsNullOrEmpty(settings.source) || !settings.source.StartsWith("Textures/palettes/", StringComparison.Ordinal)) return false;
            for (int i = 0; i < 8; i++)
            {
                var slot = settings.slots[i];
                if (slot == null || slot.x != i % 4 * 16 || slot.y != i / 4 * 16 || slot.asset != null && slot.asset != settings.source) return false;
            }
            string source = files.Asset(settings.source);
            if (!File.Exists(source) || new FileInfo(source).Length > PngRaster.MaximumBytes) return false;
            var image = PngRaster.Decode(File.ReadAllBytes(source));
            var expected = PngRaster.Decode(TilesetComposer.Template("four", material.GetProperty("color").GetString()!));
            if (image.Width == expected.Width && image.Height == expected.Height && image.Pixels.AsSpan().SequenceEqual(expected.Pixels)) return true;
            var bundled = PngRaster.Decode(File.ReadAllBytes(files.Asset(Source(material.GetProperty("id").GetString()!, "four")!)));
            return image.Width == bundled.Width && image.Height == bundled.Height && image.Pixels.AsSpan().SequenceEqual(bundled.Pixels);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return false; }
    }
}
