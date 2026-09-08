using System.Text.Json;
using MetroidvaniaStudio;
using MetroidvaniaStudio.Server;

internal static class TextureWorkflowTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action run) { try { run(); } catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or InvalidDataException) { return; } throw new Exception("Expected rejection."); }
    public static void NativeFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "studio-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new ProjectFiles(root); string path = Path.Combine(files.MapsPath, "맵-example.json");
            using var bridge = new NativeMapFiles(files, (mode, folder, name, _) =>
            {
                Check(folder == files.MapsPath, "Native open and save must start in the actual Maps folder.");
                return Task.FromResult<string?>(mode == "save" && name == "cancel.json" ? null : path);
            });
            var selection = bridge.Pick("save", "맵-example.json", default).GetAwaiter().GetResult()!;
            Check(!File.Exists(path), "Selecting a save location must not create or truncate the target.");
            Check(bridge.Read(selection.token).Length == 0, "A new save target starts empty.");
            var workspace = new EditorWorkspace(files); string json = MapDocumentStore.Serialize(workspace.Session.Document);
            bridge.Write(selection.token, json, ProjectFiles.FingerprintUtf8("").Hash);
            Check(File.ReadAllText(path) == json, "Save must retain compact UTF-8 JSON.");
            var opened = bridge.Pick("open", "", default).GetAwaiter().GetResult()!;
            Check(bridge.Read(opened.token).SequenceEqual(File.ReadAllBytes(path)), "Open must read the chosen file.");
            File.WriteAllText(path, json + " ");
            Reject(() => bridge.Write(selection.token, json, ProjectFiles.FingerprintUtf8(json).Hash));
            Check(File.ReadAllText(path) == json + " ", "An external edit must survive a conflicting save.");
            Reject(() => bridge.Read(path)); Reject(() => bridge.Write("unknown", json, ""));
            Reject(() => bridge.Write(selection.token, "{}", ProjectFiles.FingerprintUtf8(json + " ").Hash));
            Check(bridge.Pick("save", "cancel.json", default).GetAwaiter().GetResult() == null, "Cancel must have no save side effect.");
        }
        finally { Directory.Delete(root, true); }
    }
    public static void Library()
    {
        string root = Path.Combine(Path.GetTempPath(), "studio-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string studio = Path.Combine(root, "installation"), project = Path.Combine(root, "workspace"); Directory.CreateDirectory(project);
            var files = new ProjectFiles(project); var atlas = PaletteAtlas.Create("Textures/DefaultTerrain.png", "#234567");
            string legacy = Path.Combine(studio, "samples", "legacy-textures"); Directory.CreateDirectory(legacy);
            File.WriteAllBytes(Path.Combine(legacy, "DefaultTerrain.png"), atlas.Png);
            Directory.CreateDirectory(files.TexturesPath); File.WriteAllBytes(Path.Combine(files.TexturesPath, "DefaultTerrain.png"), atlas.Png);
            string defaults = Path.Combine(studio, "samples", "textures", "default", "47-tiles"); Directory.CreateDirectory(defaults);
            File.WriteAllBytes(Path.Combine(defaults, "green.png"), TilesetComposer.Template("blob47", "#234567"));
            string oldAsset = "Textures/palettes/0123456789abcdef0123456789abcdef/atlas-old.png";
            var second = PaletteAtlas.Create(oldAsset, "#874523"); string old = files.Asset(oldAsset); Directory.CreateDirectory(Path.GetDirectoryName(old)!); File.WriteAllBytes(old, second.Png);
            Directory.CreateDirectory(Path.GetDirectoryName(files.CatalogWritePath)!);
            File.WriteAllText(files.CatalogWritePath, JsonSerializer.Serialize(new {materials=new[]{
                new {id="terrain",name="Green",color="#234567",sprites=atlas.Sprites}, new {id="custom",name="Stage 2",color="#874523",sprites=second.Sprites}
            }, camera=new { ppu=16,referenceWidth=320,referenceHeight=180,orthographicSize=5.625,x=0,y=0 }, objects=Array.Empty<object>()}, Catalog.Json));
            TextureLibrary.Prepare(files, studio);
            using var catalog = JsonDocument.Parse(File.ReadAllText(files.CatalogWritePath)); var materials = catalog.RootElement.GetProperty("materials");
            var first = materials[0].GetProperty("sprites"); var ordered = PngRaster.Decode(File.ReadAllBytes(files.Asset(first[0].GetProperty("asset").GetString()!)));
            var original = PngRaster.Decode(atlas.Png);
            for(int i=0;i<51;i++)for(int y=0;y<16;y++)for(int x=0;x<16;x++)
            {
                var before=atlas.Sprites[i]; var after=first[i];
                int a=((original.Height-before.y-16+y)*original.Width+before.x+x)*4;
                int b=((ordered.Height-after.GetProperty("y").GetInt32()-16+y)*ordered.Width+after.GetProperty("x").GetInt32()+x)*4;
                Check(original.Pixels.AsSpan(a,4).SequenceEqual(ordered.Pixels.AsSpan(b,4)),"Folder migration must preserve every tile pixel and orientation.");
            }
            string next = materials[1].GetProperty("sprites")[0].GetProperty("asset").GetString()!;
            Check(next == "Textures/palettes/stage-2/atlas-1.png", "Custom folder names should be short and readable.");
            Check(File.ReadAllBytes(files.Asset(next)).SequenceEqual(second.Png), "Custom source bytes must remain intact.");
            Check(File.Exists(Path.Combine(project,".studio","texture-backup","DefaultTerrain.png")), "Original texture must be retained.");
            string saved=File.ReadAllText(files.CatalogWritePath); TextureLibrary.Prepare(files,studio); Check(saved==File.ReadAllText(files.CatalogWritePath),"Migration must be idempotent.");
            var active = new Catalog(files); active.Refresh(); string id=active.AddPalette("Stage 2!", "#223344");
            Check(active.Material(id).GetProperty("sprites")[0].GetProperty("asset").GetString()=="Textures/palettes/stage-2-2/atlas-1.png","Slug collisions must not overwrite a palette.");
        }
        finally
        {
            // Match the server fixtures: Windows file replacement may finish its
            // temporary-file cleanup just after the synchronous write returns.
            for (int attempt = 0; ; attempt++)
            {
                try { Directory.Delete(root, true); break; }
                catch (IOException) when (attempt < 5) { Thread.Sleep((attempt + 1) * 10); }
            }
        }
    }
}
