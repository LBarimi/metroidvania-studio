using System.Text.Json;
using System.Text.Json.Nodes;
using MetroidvaniaStudio.Server;

internal static class BuiltInPaletteTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void PreserveAndRender()
    {
        string studio = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(studio, "samples", "catalog.json"))) studio = Path.GetDirectoryName(studio) ?? throw new Exception("Sample catalog missing.");
        string root = Path.Combine(Path.GetTempPath(), "studio-themes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new ProjectFiles(root, studioRoot: studio);
            var sample = JsonNode.Parse(File.ReadAllText(files.CatalogPath))!;
            var legacy = sample.DeepClone(); var materials = legacy["materials"]!.AsArray();
            foreach (var material in materials.Where(m => m!["id"]!.GetValue<string>().StartsWith("biome-")).ToArray()) materials.Remove(material);
            materials[0]!["name"] = "My grass"; materials[0]!["color"] = "#123456";
            legacy["customMetadata"] = new JsonObject { ["keep"] = true };
            legacy["editorPaletteGroups"] = JsonSerializer.SerializeToNode(new[] {
                new EditorPaletteGroup("custom", "Default themes", ["terrain"]),
                new EditorPaletteGroup("default", "Default", materials.Skip(1).Select(m => m!["id"]!.GetValue<string>()).ToArray()) });
            Directory.CreateDirectory(Path.GetDirectoryName(files.CatalogWritePath)!);
            File.WriteAllText(files.CatalogWritePath, legacy.ToJsonString());
            BuiltInPalettes.Prepare(files, studio);
            var catalog = new Catalog(files); catalog.Refresh();
            Check(catalog.Data.GetProperty("customMetadata").GetProperty("keep").GetBoolean(), "Opaque catalog fields survive.");
            Check(JsonNode.Parse(catalog.Data.GetRawText())!["materials"]!.AsArray().Take(5).Select(n => n!.ToJsonString()).SequenceEqual(materials.Select(n => n!.ToJsonString())), "Existing names, sprites and colors survive.");
            Check(catalog.PaletteGroups.Take(2).Select(g => g.id).SequenceEqual(new[] { "custom", "default" }), "Existing group order survives.");
            Check(catalog.PaletteGroups.Single(g => g.id == "default-themes").materials.Length == 5, "Five initial themes are added in a distinct group.");
            catalog.MovePalette("biome-rock", "custom", null);
            string moved = File.ReadAllText(files.CatalogWritePath);
            BuiltInPalettes.Prepare(files, studio);
            Check(File.ReadAllText(files.CatalogWritePath) == moved, "Restart neither duplicates themes nor undoes a user's group placement.");
            foreach (var material in catalog.Data.GetProperty("materials").EnumerateArray().Where(m => m.GetProperty("id").GetString()!.StartsWith("biome-")))
            {
                string slug = material.GetProperty("id").GetString()![6..];
                var source = PngRaster.Decode(File.ReadAllBytes(Path.Combine(studio, "samples", "textures", "biomes", "4-tiles", slug + ".png")));
                var settings = new PaletteTileset("four", "source", Enumerable.Range(0, 8).Select(i => (TilesetSlot?)new TilesetSlot(i % 4 * 16, i / 4 * 16)).ToArray());
                var composed = TilesetComposer.Compose("Textures/test.png", material.GetProperty("color").GetString()!, settings, source);
                var expected = PngRaster.Decode(composed.Png);
                int index = 0;
                foreach (var sprite in material.GetProperty("sprites").EnumerateArray())
                {
                    string asset = sprite.GetProperty("asset").GetString()!;
                    Check(files.TextureFolder(asset) == Path.Combine(files.TexturesPath, "biomes", "47-tiles"), "Open folder resolves the real texture directory.");
                    var actual = PngRaster.Decode(File.ReadAllBytes(files.Asset(asset)));
                    var target = composed.Sprites[index++];
                    for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
                    {
                        int a = ((actual.Height - sprite.GetProperty("y").GetInt32() - 16 + y) * actual.Width + sprite.GetProperty("x").GetInt32() + x) * 4;
                        int b = ((expected.Height - target.y - 16 + y) * expected.Width + target.x + x) * 4;
                        Check(actual.Pixels.AsSpan(a, 4).SequenceEqual(expected.Pixels.AsSpan(b, 4)), "Every theme sprite matches its four-source composition.");
                    }
                }
                Check(index == 51, "Every theme includes 47 masks and four slopes.");
            }
            Check(files.TextureFolder(null) == files.TexturesPath, "Pending imports open Textures.");
            foreach (string invalid in new[] { "../outside.png", "Textures/../../outside.png", "Textures/missing.png" })
            {
                bool rejected = false;
                try { files.TextureFolder(invalid); } catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { rejected = true; }
                Check(rejected, "Folder opening must reject missing and external assets.");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
