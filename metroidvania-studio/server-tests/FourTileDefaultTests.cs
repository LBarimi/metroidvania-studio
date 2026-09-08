using System.Text.Json;
using MetroidvaniaStudio.Server;

internal static class FourTileDefaultTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Bindings()
    {
        string studio = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(studio, "samples", "catalog.json"))) studio = Path.GetDirectoryName(studio)!;
        string root = Path.Combine(Path.GetTempPath(), "studio-four-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var files = new ProjectFiles(root, studioRoot: studio);
            BundledTilesets.Prepare(files, studio);
            var catalog = new Catalog(files); catalog.Refresh();
            var groups = JsonSerializer.Serialize(catalog.PaletteGroups);
            foreach (var material in catalog.Data.GetProperty("materials").EnumerateArray())
            {
                var settings = JsonSerializer.Deserialize<PaletteTileset>(material.GetProperty("editorTileset"), Catalog.Json)!;
                Check(settings.mode == "four" && settings.source == BundledTilesets.Source(material.GetProperty("id").GetString()!, "four"), "Initial palettes reference their matching four-source PNG.");
                Check(settings.slots.Length == 8 && settings.slots.All(s => s != null), "Edges, corners, fill and four slopes are assigned.");
            }
            var rock = catalog.Material("biome-rock");
            byte[] copy = File.ReadAllBytes(files.Asset(BundledTilesets.Source("biome-rock", "four")!));
            catalog.ConfigurePalette("biome-rock", "Rock", rock.GetProperty("color").GetString()!, BundledTilesets.Four("upload"), Convert.ToBase64String(copy), rock);
            BundledTilesets.Prepare(files, studio); catalog.Refresh(); rock = catalog.Material("biome-rock");
            Check(rock.GetProperty("editorTileset").GetProperty("source").GetString() == "Textures/biomes/4-tiles/rock.png", "Identical imported copies reconnect to the shared source folder.");
            var placeholder = TilesetComposer.Template("four", rock.GetProperty("color").GetString()!);
            catalog.ConfigurePalette("biome-rock", "Rock", rock.GetProperty("color").GetString()!, BundledTilesets.Four("upload"), Convert.ToBase64String(placeholder), rock);
            BundledTilesets.Prepare(files, studio); catalog.Refresh();
            Check(catalog.Material("biome-rock").GetProperty("editorTileset").GetProperty("source").GetString() == "Textures/biomes/4-tiles/rock.png", "Old color placeholders are repaired to the actual rock sheet.");
            string unchanged = File.ReadAllText(files.CatalogWritePath); BundledTilesets.Prepare(files, studio);
            Check(File.ReadAllText(files.CatalogWritePath) == unchanged, "Reopening keeps stable atlas paths and settings.");
            var custom = PngRaster.Decode(placeholder); custom.Pixels[0] ^= 0x7f;
            rock = catalog.Material("biome-rock");
            catalog.ConfigurePalette("biome-rock", "My rock", rock.GetProperty("color").GetString()!, BundledTilesets.Four("upload"), Convert.ToBase64String(custom.Encode()), rock);
            string customized = File.ReadAllText(files.CatalogWritePath); BundledTilesets.Prepare(files, studio);
            Check(File.ReadAllText(files.CatalogWritePath) == customized, "Custom image edits are never replaced with bundled art.");
            var ice = catalog.Material("biome-ice-cavern");
            var settings47 = new PaletteTileset("blob47", BundledTilesets.Source("biome-ice-cavern", "blob47")!, Enumerable.Range(0, 51).Select(i => (TilesetSlot?)new TilesetSlot(i % 8 * 16, i / 8 * 16)).ToArray());
            catalog.ConfigurePalette("biome-ice-cavern", "Ice cavern", ice.GetProperty("color").GetString()!, settings47, null, ice);
            string explicit47 = File.ReadAllText(files.CatalogWritePath); BundledTilesets.Prepare(files, studio);
            Check(File.ReadAllText(files.CatalogWritePath) == explicit47, "An explicitly chosen 47-tile mode stays selected.");
            Check(JsonSerializer.Serialize(catalog.PaletteGroups) == groups, "Mode initialization and repair keep palette group placement.");
            string id = catalog.AddPalette("New palette", "#123456");
            Check(catalog.Material(id).GetProperty("editorTileset").GetProperty("mode").GetString() == "four", "New custom palettes also start in four-tile mode.");
        }
        finally { Directory.Delete(root, true); }
    }
}
