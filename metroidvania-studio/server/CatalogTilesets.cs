using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MetroidvaniaStudio;

namespace MetroidvaniaStudio.Server;

public sealed partial class Catalog
{
    public JsonElement Material(string id) => Data.GetProperty("materials").EnumerateArray().FirstOrDefault(m => m.GetProperty("id").GetString() == id)
        is { ValueKind: JsonValueKind.Object } material ? material : throw new ArgumentException("@tilesetMissingPalette");

    public void ConfigurePalette(string id, string name, string color, PaletteTileset settings, string? pngBase64, JsonElement expectedMaterial, Dictionary<string, string>? uploads = null, PalettePlacement? placement = null)
    {
        JsonElement current = Material(id);
        if (!JsonNode.DeepEquals(JsonNode.Parse(current.GetRawText()), JsonNode.Parse(expectedMaterial.GetRawText())))
            throw new WorkspaceConflict("@tilesetChanged");
        name = name.Trim(); color = color.Trim().ToUpperInvariant();
        if (name.Length is 0 or > 80 || name.Any(char.IsControl)) throw new ArgumentException("@paletteInvalidName");
        if (color.Length != 7 || color[0] != '#' || !color[1..].All(char.IsAsciiHexDigit)) throw new ArgumentException("@paletteInvalidColor");
        if (Data.GetProperty("materials").EnumerateArray().Any(m => m.GetProperty("id").GetString() != id
            && string.Equals(m.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("@paletteDuplicateName");
        CheckGroupExpectation(placement?.expectedGroups);
        var placedGroups = placement == null ? null : PlacePalette(PaletteGroups, id, placement.groupId, placement.beforeId);
        ValidateTileset(settings);
        ProjectFiles.DiskFingerprint? expected = null;
        string catalogPath = files.CatalogPath;
        if (File.Exists(catalogPath))
        {
            var info = new FileInfo(catalogPath); var content = ReadBounded(catalogPath, info.LastWriteTimeUtc, info.Length);
            if (!loadedFromFile || content.Fingerprint != modifiedFingerprint) throw new WorkspaceConflict("@tilesetChanged");
            if (string.Equals(catalogPath, files.CatalogWritePath, ProjectFiles.PathComparison)) expected = new(info.Length, info.LastWriteTimeUtc.Ticks, content.Fingerprint);
        }
        else if (loadedFromFile) throw new WorkspaceConflict("@tilesetChanged");
        string directory = files.PaletteDirectory(name, current.GetProperty("sprites")[0].GetProperty("asset").GetString());
        string asset = files.NextPaletteAsset(directory, "atlas");
        var additional = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        PngRaster? source = null;
        var images = new Dictionary<string, PngRaster>(StringComparer.Ordinal);
        if (settings.mode != "template")
        {
            uploads ??= new(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(pngBase64)) uploads[settings.source] = pngBase64;
            if (uploads.Count > 52) throw new ArgumentException("@tilesetImageLimit");
            var allowed = current.GetProperty("sprites").EnumerateArray().Select(s => s.GetProperty("asset").GetString()!).ToHashSet(StringComparer.Ordinal);
            if (current.TryGetProperty("editorTileset", out var saved) && saved.ValueKind == JsonValueKind.Object)
            {
                var prior = JsonSerializer.Deserialize<PaletteTileset>(saved, Json);
                if (prior != null) { ValidateTileset(prior); foreach (string nameOfSource in Sources(prior)) allowed.Add(nameOfSource); }
            }
            var remap = new Dictionary<string, string>(StringComparer.Ordinal); long totalBytes = 0, totalPixels = 0;
            foreach (string input in Sources(settings))
            {
                byte[] png; string target = input;
                if (uploads.TryGetValue(input, out string? encoded))
                {
                    if (encoded.Length > (PngRaster.MaximumBytes + 2L) / 3 * 4) throw new ArgumentException("@tilesetImageLimit");
                    try { png = Convert.FromBase64String(encoded); } catch (FormatException) { throw new ArgumentException("@tilesetInvalidPng"); }
                    target = files.NextPaletteAsset(directory, "source", additional.Keys); additional.Add(target, png); remap[input] = target;
                }
                else
                {
                    if (!allowed.Contains(input)) throw new ArgumentException("@tilesetChooseImage");
                    string file = files.Asset(input); var info = new FileInfo(file);
                    if (!info.Exists || info.Length > PngRaster.MaximumBytes) throw new ArgumentException("@tilesetImageLimit");
                    png = ReadTexture(file, info.Length, info.LastWriteTimeUtc);
                }
                totalBytes += png.Length; if (totalBytes > PngRaster.MaximumBytes) throw new ArgumentException("@tilesetImageLimit");
                var pixels = PngRaster.Decode(png); totalPixels += (long)pixels.Width * pixels.Height;
                if (totalPixels > PngRaster.MaximumPixels) throw new ArgumentException("@tilesetImageLimit");
                images.Add(target, pixels);
            }
            settings = settings with { source = remap.GetValueOrDefault(settings.source, settings.source),
                slots = settings.slots.Select(s => s?.asset != null ? s with { asset = remap.GetValueOrDefault(s.asset, s.asset) } : s).ToArray() };
            if (!images.TryGetValue(settings.source, out source)) throw new ArgumentException("@tilesetChooseImage");
        }
        else settings = new("template", "", []);
        var atlas = TilesetComposer.Compose(asset, color, settings, source, images);
        settings = settings with { atlasHash = Convert.ToHexString(SHA256.HashData(atlas.Png)) };
        var next = JsonNode.Parse(Data.GetRawText())!;
        if (placedGroups != null) next["editorPaletteGroups"] = JsonSerializer.SerializeToNode(placedGroups);
        var material = next["materials"]!.AsArray().First(m => m!["id"]!.GetValue<string>() == id)!;
        material["name"] = name; material["color"] = color;
        if (string.IsNullOrWhiteSpace(material["themeId"]?.GetValue<string>())) material["themeId"] = id;
        material["sprites"] = JsonSerializer.SerializeToNode(atlas.Sprites, Json);
        material["editorTileset"] = JsonSerializer.SerializeToNode(settings, Json);
        string serialized = next.ToJsonString();
        if (Utf8.GetByteCount(serialized) > MaximumCatalogBytes) throw new InvalidDataException("@tilesetCatalogFull");
        ValidateComplexity(ParseClone(serialized));
        files.PublishPalette(asset, atlas.Png, serialized, expected, additional);
        Refresh();
    }
    private static void ValidateTileset(PaletteTileset settings)
    {
        if (settings == null || settings.source == null || settings.slots == null || settings.mode is not ("template" or "four" or "blob47")
            || settings.slots.Length != (settings.mode == "template" ? 0 : settings.mode == "four" ? 8 : 51)
            || settings.source.Length > 1024 || settings.slots.Any(s => s != null && (s.x < 0 || s.y < 0 || s.x > 16384 || s.y > 16384 || s.asset?.Length > 1024)))
            throw new ArgumentException("@tilesetInvalidSettings");
    }
    private static IEnumerable<string> Sources(PaletteTileset settings) => new[] { settings.source }
        .Concat(settings.slots.Select(s => s?.asset).OfType<string>()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal);

    // Runs on the texture worker, never on pointer input. Atlas paths are unique per edit,
    // so an older worker cannot overwrite a newer palette's output.
    private void RefreshDerivedTilesets(JsonElement source, Dictionary<string, TextureStamp> previous, Dictionary<string, TextureStamp> result)
    {
        foreach (var material in source.GetProperty("materials").EnumerateArray())
        {
            if (!material.TryGetProperty("editorTileset", out var metadata) || metadata.ValueKind != JsonValueKind.Object) continue;
            PaletteTileset? settings = null;
            try
            {
                settings = JsonSerializer.Deserialize<PaletteTileset>(metadata, Json); ValidateTileset(settings!);
                if (settings!.mode == "template") continue;
                string[] inputNames = Sources(settings).ToArray();
                if (inputNames.All(input => result.TryGetValue(input, out var next) && previous.TryGetValue(input, out var oldInput) && oldInput.Hash == next.Hash)) continue;
                string atlasAsset = material.GetProperty("sprites")[0].GetProperty("asset").GetString()!;
                var images = new Dictionary<string, PngRaster>(StringComparer.Ordinal); long totalPixels = 0, totalBytes = 0;
                foreach (string name in inputNames)
                {
                    if (!result.TryGetValue(name, out var input)) throw new IOException("Source image is not ready.");
                    var png = ReadTexture(input.Path, input.Length, input.Modified); totalBytes += png.Length;
                    if (totalBytes > PngRaster.MaximumBytes || Convert.ToHexString(SHA256.HashData(png)) != input.Hash) throw new IOException("Source changed during atlas composition.");
                    var pixels = PngRaster.Decode(png); totalPixels += (long)pixels.Width * pixels.Height;
                    if (totalPixels > PngRaster.MaximumPixels) throw new ArgumentException("@tilesetImageLimit");
                    images.Add(name, pixels);
                }
                var atlas = TilesetComposer.Compose(atlasAsset, material.GetProperty("color").GetString()!, settings, images[settings.source], images);
                string expected = result.TryGetValue(atlasAsset, out var oldAtlas) ? oldAtlas.Hash : settings.atlasHash;
                files.PublishDerivedTexture(atlasAsset, atlas.Png, expected);
                var info = new FileInfo(files.Asset(atlasAsset));
                result[atlasAsset] = new(info.FullName, info.Length, info.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(atlas.Png)), Environment.TickCount64);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                if (settings?.source != null && settings.slots != null) foreach (string name in Sources(settings))
                {
                    if (previous.TryGetValue(name, out var old)) result[name] = old;
                    else result.Remove(name);
                }
            }
        }
    }
}
