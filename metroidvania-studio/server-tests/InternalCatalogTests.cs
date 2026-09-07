using System.Text.Json;
using MetroidvaniaStudio.Server;
using MetroidvaniaStudio.Storage;

internal static class InternalCatalogTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Expected a preserved catalog conflict."); }
    private static void WithFolder(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "studio-catalog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, true); }
    }
    private static void Put(string root, string relative, string text)
    {
        string file = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text);
    }
    private const string RootSettings = "{\"MapsRoot\":\"Maps\",\"TexturesRoot\":\"Textures\",\"CatalogPath\":\"catalog.json\",\"customValue\":{\"keep\":true}}";
    private static void Portable(string root, string settings = RootSettings)
    {
        Put(root, ".studio/portable-storage.json", "{\"version\":1}"); Put(root, ".studio/workspace.json", settings);
    }
    public static void Upgrade() => WithFolder(root =>
    {
        Portable(root); var catalog = new Catalog(new ProjectFiles(root)); catalog.Refresh();
        string id = catalog.AddPalette("Terrain", "#247856"), group = catalog.AddPaletteGroup("Caves"); catalog.MovePalette(id, group, null);
        string before = File.ReadAllText(Path.Combine(root, "catalog.json"));
        var textures = Directory.GetFiles(Path.Combine(root, "Textures"), "*.png", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        Put(root, "Maps/world.json", "{\"name\":\"방\"}"); Put(root, "Maps/AutoExport/world/room.json", "{\"id\":\"room\"}");
        PortableWorkspace.SeedResources(root, Path.Combine(root, "application"));
        var files = new ProjectFiles(root);
        Check(files.CatalogWritePath == Path.Combine(root, ".studio", "catalog.json"), "Readers and writers use the internal catalog after migration.");
        Check(!File.Exists(Path.Combine(root, "catalog.json")), "The application root must no longer contain the catalog.");
        Check(File.ReadAllText(files.CatalogWritePath) == before, "Palette data and group metadata must retain their exact bytes.");
        string[] backups = Directory.GetFiles(Path.Combine(root, ".studio/catalog-backup"));
        Check(backups.Length == 1 && File.ReadAllText(backups[0]) == before, "Keep the original as an internal backup.");
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".studio/workspace.json")));
        Check(settings.RootElement.GetProperty("customValue").GetProperty("keep").GetBoolean(), "Unknown workspace settings are retained.");
        Check(textures.All(t => File.ReadAllBytes(t.Key).SequenceEqual(t.Value)), "Moving a catalog cannot rewrite textures.");
        Check(File.ReadAllText(Path.Combine(root, "Maps/world.json")) == "{\"name\":\"방\"}", "Named maps are unchanged.");
        Check(File.ReadAllText(Path.Combine(root, "Maps/AutoExport/world/room.json")) == "{\"id\":\"room\"}", "Automatic room exports are unchanged.");
        var reopened = new Catalog(files); reopened.Refresh(); Check(reopened.PaletteGroups.Single(g => g.id == group).materials.Single() == id, "Palette groups reload from the relocated catalog.");
        reopened.AddPaletteGroup("New group"); string edited = File.ReadAllText(files.CatalogWritePath);
        PortableWorkspace.SeedResources(root, Path.Combine(root, "application"));
        Check(File.ReadAllText(files.CatalogWritePath) == edited && Directory.GetFiles(Path.Combine(root, ".studio/catalog-backup")).Length == 1, "Restart cannot restore old settings or duplicate backups.");
        Check(!File.Exists(Path.Combine(root, "catalog.json")), "New palette edits cannot recreate a root catalog.");
    });
    public static void Recovery() => WithFolder(parent =>
    {
        for (int state = 0; state < 3; state++)
        {
            string root = Path.Combine(parent, "resume-" + state); Portable(root, state == 1 ? RootSettings.Replace("\"catalog.json\"", "\".studio/catalog.json\"") : RootSettings);
            if (state != 2) Put(root, "catalog.json", "same data"); Put(root, ".studio/catalog.json", "same data");
            PortableWorkspace.SeedResources(root, Path.Combine(parent, "application"));
            Check(!File.Exists(Path.Combine(root, "catalog.json")) && File.ReadAllText(new ProjectFiles(root).CatalogWritePath) == "same data", "Resume after a copied catalog, published settings or relocated source.");
        }
        string conflict = Path.Combine(parent, "conflict"); Portable(conflict); Put(conflict, "catalog.json", "root data"); Put(conflict, ".studio/catalog.json", "internal data");
        Reject(() => PortableWorkspace.SeedResources(conflict, Path.Combine(parent, "application")));
        Check(File.ReadAllText(Path.Combine(conflict, "catalog.json")) == "root data" && File.ReadAllText(Path.Combine(conflict, ".studio/catalog.json")) == "internal data", "Different copies must both survive.");
        Check(File.ReadAllText(Path.Combine(conflict, ".studio/workspace.json")) == RootSettings, "Conflicting migration cannot repoint settings.");
    });
    public static void CustomPaths() => WithFolder(root =>
    {
        Portable(root, RootSettings.Replace("\"catalog.json\"", "\"resources/custom.json\""));
        Put(root, "resources/custom.json", "custom catalog"); Put(root, "catalog.json", "unrelated file");
        string app = Path.Combine(root, "application"); Put(app, "samples/catalog.json", "sample catalog");
        PortableWorkspace.SeedResources(root, app);
        Check(File.ReadAllText(new ProjectFiles(root).CatalogWritePath) == "custom catalog", "Explicit catalog locations must be preserved.");
        Check(File.ReadAllText(Path.Combine(root, "catalog.json")) == "unrelated file" && !File.Exists(Path.Combine(root, ".studio/catalog.json")), "Custom workspaces cannot gain or lose another catalog.");
    });
}
