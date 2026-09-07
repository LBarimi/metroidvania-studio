using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MetroidvaniaStudio.Server;

public sealed partial class ProjectFiles
{
    internal string PaletteDirectory(string name, string? current = null)
    {
        if (current?.StartsWith("Textures/palettes/", StringComparison.Ordinal) == true)
        {
            string[] parts = current.Split('/');
            if (parts.Length == 4 && !Regex.IsMatch(parts[2], "^[a-f0-9]{32}$")) return string.Join('/', parts[..3]) + "/";
        }
        string slug = Regex.Replace(name.ToLowerInvariant(), "[^\\p{L}\\p{Nd}]+", "-").Trim('-');
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        if (slug.Length > 0 && char.IsHighSurrogate(slug[^1])) slug = slug[..^1];
        if (slug.Length == 0) slug = "palette";
        if (Regex.IsMatch(slug, "^[a-f0-9]{32}$")) slug = "palette-" + slug;
        if (Regex.IsMatch(slug, "^(con|prn|aux|nul|com[1-9]|lpt[1-9])$")) slug = "palette-" + slug;
        for (int index = 1; ; index++)
        {
            string directory = "Textures/palettes/" + slug + (index == 1 ? "" : "-" + index) + "/";
            string absolute = Resolve(texturesRelative, directory[9..]);
            if (!Directory.Exists(absolute) && !File.Exists(absolute)) return directory;
        }
    }
    internal string NextPaletteAsset(string directory, string prefix, IEnumerable<string>? reserved = null)
    {
        var used = new HashSet<string>(reserved ?? [], StringComparer.OrdinalIgnoreCase);
        for (int index = 1; ; index++)
        {
            string asset = directory + prefix + "-" + index + ".png";
            if (!used.Contains(asset) && !File.Exists(Resolve(texturesRelative, asset[9..])) && !Directory.Exists(Resolve(texturesRelative, asset[9..]))) return asset;
        }
    }
    internal string TextureDestination(string asset) => Resolve(texturesRelative, asset[9..]);
    internal string TextureBackup(string relative) => Resolve(".studio/texture-backup", relative);
    internal void PublishTextureCatalog(string text, DiskFingerprint? expected)
    {
        string destination = CatalogWritePath, staged = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(staged, text, StrictUtf8);
            if (expected.HasValue) PublishIfUnchanged(staged, destination, expected.Value, FingerprintUtf8(text));
            else File.Move(staged, destination);
        }
        finally { DeleteBestEffort(staged); }
    }
}

/// <summary>Readable resource names with retained original files and stable material identities.</summary>
public static class TextureLibrary
{
    public static void Prepare(ProjectFiles files, string studioRoot)
    {
        string defaults = Path.Combine(studioRoot, "samples", "textures", "default");
        if (Directory.Exists(defaults)) foreach (string source in Directory.EnumerateFiles(defaults, "*.png", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) continue;
            string target = files.TextureDestination("Textures/default/" + Path.GetRelativePath(defaults, source).Replace('\\', '/'));
            CopyNew(source, target);
        }
        if (!File.Exists(files.CatalogWritePath)) return;
        var expected = ProjectFiles.Fingerprint(files.CatalogWritePath);
        if (!expected.HasValue || expected.Value.Length > Catalog.MaximumCatalogBytes) return;
        string before = File.ReadAllText(files.CatalogWritePath, new System.Text.UTF8Encoding(false, true));
        var catalog = JsonNode.Parse(before);
        if (catalog?["materials"] is not JsonArray materials) return;
        var oldAssets = new HashSet<string>(StringComparer.Ordinal);
        bool changed = false;
        var defaultsByName = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DefaultTerrain.png"] = "green", ["TerrainBlue.png"] = "blue", ["TerrainDarkGray.png"] = "gray",
            ["TerrainOrange.png"] = "orange", ["TerrainYellow.png"] = "yellow"
        };
        foreach (var material in materials.OfType<JsonObject>())
        {
            if (material["sprites"] is not JsonArray sprites || sprites.Count == 0) continue;
            string? first = sprites[0]?["asset"]?.GetValue<string>();
            if (first == null) continue;
            if (material["editorTileset"] == null && first.StartsWith("Textures/", StringComparison.Ordinal)
                && defaultsByName.TryGetValue(first[9..], out string? color)
                && sprites.All(s => s?["asset"]?.GetValue<string>() == first))
            {
                string original = Path.Combine(studioRoot, "samples", "legacy-textures", first[9..]);
                string source = files.Asset(first), next = "Textures/default/47-tiles/" + color + ".png";
                if (File.Exists(original) && File.Exists(source) && File.Exists(files.Asset(next))
                    && File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(original))
                    && File.ReadAllBytes(files.Asset(next)).SequenceEqual(File.ReadAllBytes(Path.Combine(defaults, "47-tiles", color + ".png"))))
                {
                    foreach (var sprite in sprites.OfType<JsonObject>()) { sprite["asset"] = next; sprite["y"] = 96 - sprite["y"]!.GetValue<int>(); }
                    oldAssets.Add(first); changed = true; continue;
                }
            }
            if (!first.StartsWith("Textures/palettes/", StringComparison.Ordinal)) continue;
            string[] parts = first.Split('/');
            bool legacy = parts.Length == 3 || parts.Length == 4 && Regex.IsMatch(parts[2], "^[a-f0-9]{32}$");
            if (!legacy) continue;
            string folder = files.PaletteDirectory(material["name"]?.GetValue<string>() ?? "palette");
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            void Visit(JsonNode? node)
            {
                if (node is JsonArray array) { foreach (var item in array) Visit(item); return; }
                if (node is not JsonObject obj) return;
                foreach (var pair in obj.ToArray())
                {
                    if (pair.Key is "asset" or "source" && pair.Value is JsonValue value && value.TryGetValue<string>(out var asset)
                        && asset.StartsWith("Textures/palettes/", StringComparison.Ordinal))
                    {
                        if (!replacements.TryGetValue(asset, out string? target))
                        {
                            target = files.NextPaletteAsset(folder, asset == first ? "atlas" : "source", replacements.Values);
                            CopyNew(files.Asset(asset), files.TextureDestination(target)); replacements.Add(asset, target); oldAssets.Add(asset);
                        }
                        obj[pair.Key] = target; changed = true;
                    }
                    else Visit(pair.Value);
                }
            }
            Visit(material);
        }
        foreach (string name in defaultsByName.Keys)
        {
            string asset = "Textures/" + name, source = files.TextureDestination(asset);
            string legacy = Path.Combine(studioRoot, "samples", "legacy-textures", name);
            if (File.Exists(source) && File.Exists(legacy) && File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(legacy))) oldAssets.Add(asset);
        }
        string paletteRoot = files.TextureDestination("Textures/palettes/");
        if (Directory.Exists(paletteRoot)) foreach (string directory in Directory.EnumerateDirectories(paletteRoot))
        {
            if (!Regex.IsMatch(Path.GetFileName(directory), "^[a-f0-9]{32}$") || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (string image in Directory.EnumerateFiles(directory, "*.png"))
                if (Regex.IsMatch(Path.GetFileName(image), "^(atlas-[a-f0-9]{32}|source-[0-9]+)\\.png$") && (File.GetAttributes(image) & FileAttributes.ReparsePoint) == 0)
                    oldAssets.Add("Textures/palettes/" + Path.GetFileName(directory) + "/" + Path.GetFileName(image));
        }
        if (!changed && oldAssets.Count == 0) return;
        string backup = files.TextureBackup("catalog.json"); CopyNew(files.CatalogWritePath, backup);
        foreach (string asset in oldAssets)
        {
            string original = files.TextureDestination(asset);
            if (File.Exists(original)) CopyNew(original, files.TextureBackup(asset[9..]));
        }
        string after = catalog!.ToJsonString();
        if (changed) files.PublishTextureCatalog(after, expected!.Value);
        // Only retire files whose exact original bytes were retained and are no longer referenced.
        foreach (string asset in oldAssets)
        {
            if (after.Contains(asset, StringComparison.Ordinal)) continue;
            string original = files.TextureDestination(asset), retained = files.TextureBackup(asset[9..]);
            if (File.Exists(original) && File.Exists(retained) && File.ReadAllBytes(original).SequenceEqual(File.ReadAllBytes(retained)))
            {
                File.Delete(original);
                string parent = Path.GetDirectoryName(original)!;
                if (parent != files.TexturesPath && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
            }
        }
    }
    private static void CopyNew(string source, string target)
    {
        if (File.Exists(target)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(source, temporary); File.Move(temporary, target); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
