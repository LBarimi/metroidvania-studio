using System.Text.Json;
using MetroidvaniaStudio.Storage;
using MetroidvaniaStudio.Server;

internal static class PortableWorkspaceTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Expected preserved storage conflict."); }
    private static void WithFolder(Action<string> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "studio-storage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { test(root); } finally { Directory.Delete(root, true); }
    }
    private static void Put(string root, string name, string value)
    { string file = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, value); }
    public static void Migration() => WithFolder(temp =>
    {
        string legacy = Path.Combine(temp, "legacy"), root = Path.Combine(temp, "portable");
        Put(legacy, ".studio/workspace.json", "{\"MapsRoot\":\"documents\",\"TexturesRoot\":\"images\",\"CatalogPath\":\"custom/catalog.json\"}");
        var files = new Dictionary<string, string> { ["documents/room.json"] = "{\"name\":\"방\"}", ["documents/AutoExport/world/room.json"] = "{\"id\":\"room\"}",
            ["documents/.Recovery/Workspace.map.json"] = "recovery", ["images/palettes/source.png"] = "png", ["custom/catalog.json"] = "{\"asset\":\"Textures/palettes/source.png\"}", ["Scripts/build.lua"] = "script" };
        foreach (var pair in files) Put(legacy, pair.Key, pair.Value);
        PortableWorkspace.Prepare(root, legacy);
        Check(new ProjectFiles(root).CatalogWritePath == Path.Combine(root, ".studio", "catalog.json"), "The portable catalog belongs to internal workspace metadata.");
        foreach (var pair in files)
        {
            string destination = pair.Key.Replace("documents/", "Maps/").Replace("images/", "Textures/").Replace("custom/catalog.json", ".studio/catalog.json");
            Check(File.ReadAllText(Path.Combine(root, destination)) == pair.Value, "Migration keeps exact bytes: " + destination);
            Check(File.ReadAllText(Path.Combine(legacy, pair.Key)) == pair.Value, "Original storage remains available.");
        }
        Put(root, "Maps/room.json", "new edits"); PortableWorkspace.Prepare(root, legacy);
        Check(File.ReadAllText(Path.Combine(root, "Maps/room.json")) == "new edits", "Migration runs once, not on every launch.");
        string sample = Path.Combine(temp, "application"); Put(sample, "samples/textures/default.png", "default png"); Put(sample, "samples/catalog.json", "default catalog");
        PortableWorkspace.SeedResources(root, sample);
        Check(File.ReadAllText(Path.Combine(root, "Textures/default.png")) == "default png", "Bundled images travel with exported maps.");
        Check(File.ReadAllText(Path.Combine(root, ".studio", "catalog.json")) == files["custom/catalog.json"], "Default catalog never overwrites a migrated catalog.");
    });
    public static void Conflicts() => WithFolder(temp =>
    {
        string legacy = Path.Combine(temp, "legacy"), root = Path.Combine(temp, "portable");
        Put(legacy, "Maps/first.json", "first"); Put(legacy, "Maps/conflict.json", "old"); Put(root, "Maps/conflict.json", "new");
        Reject(() => PortableWorkspace.Prepare(root, legacy));
        Check(!File.Exists(Path.Combine(root, "Maps/first.json")), "Preflight rejects all copies before a collision.");
        Check(File.ReadAllText(Path.Combine(root, "Maps/conflict.json")) == "new", "Existing target is preserved.");
        Put(root, "Maps/conflict.json", "old");
        using (var locked = new FileStream(Path.Combine(legacy, ".studio/server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Reject(() => PortableWorkspace.Prepare(root, legacy));
        Check(!PortableWorkspace.IsPortable(root), "An active legacy workspace is never marked as migrated.");
        PortableWorkspace.Prepare(root, legacy);
        Check(File.Exists(Path.Combine(root, "Maps/first.json")), "A verified interrupted migration can resume.");
        string bad = Path.Combine(temp, "bad"), destination = Path.Combine(temp, "another");
        Put(bad, ".studio/workspace.json", "{\"MapsRoot\":\"../outside\"}");
        Reject(() => PortableWorkspace.Prepare(destination, bad));
        Check(!PortableWorkspace.IsPortable(destination), "External configured paths are rejected.");
    });
}
