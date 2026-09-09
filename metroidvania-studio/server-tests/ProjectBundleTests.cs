using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MetroidvaniaStudio;
using MetroidvaniaStudio.Server;

internal static class ProjectBundleTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Send(EditorWorkspace w, string action, params (string key, object value)[] values)
    {
        var request = new Dictionary<string, object> { ["action"] = action, ["clientId"] = "bundle-tests", ["commandId"] = Guid.NewGuid().ToString("N"),
            ["expectedRevision"] = w.Revision, ["expectedInstanceId"] = w.InstanceId };
        foreach (var (key, value) in values) request[key] = value;
        w.Command(JsonSerializer.SerializeToElement(request));
    }
    private static ProjectBundleSnapshot Capture(EditorWorkspace w) => w.CaptureProjectBundle(w.InstanceId, w.DocumentRevision, w.CatalogRevision);
    private static JsonElement Element(JsonNode value) => JsonSerializer.SerializeToElement(value);
    private static void Reject(Action action, string prefix)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException or WorkspaceConflict)
        { Check(error.Message.StartsWith(prefix, StringComparison.Ordinal), error.Message); return; }
        throw new Exception("Expected export rejection: " + prefix);
    }
    private static byte[] Bytes(ZipArchiveEntry entry) { using var read = entry.Open(); using var result = new MemoryStream(); read.CopyTo(result); return result.ToArray(); }

    public static void RoundTrip(EditorWorkspace w)
    {
        TilesetTests.Storage(w); // Authored palette with four independent source PNGs.
        string first = w.Canvas.Material;
        Send(w, "paletteAdd", ("name", "한글 47"), ("color", "#345678"));
        string second = w.Canvas.Material;
        var full = new PaletteTileset("blob47", "sheet", Enumerable.Range(0, 51).Select(i => (TilesetSlot?)new(i % 8 * 16, i / 8 * 16)).ToArray());
        Send(w, "paletteConfigure", ("id", second), ("name", "한글 47"), ("color", "#345678"), ("settings", full),
            ("uploads", new Dictionary<string, string> { ["sheet"] = Convert.ToBase64String(TilesetComposer.Template("blob47", "#345678")) }), ("expectedMaterial", w.Catalog.Material(second)));
        var document = MapDocumentStore.Deserialize(w.Session.CurrentJson); document.name = "휴대용 월드";
        document.rooms[0].foreground.Add(new MapCell { x = 2, y = 3, material = first });
        document.rooms[0].background.Add(new MapCell { x = 1, y = 2, material = second });
        document.rooms[0].properties.Add(new MapProperty { key = "note", value = "검토 🌿" });
        var png = PaletteAtlas.Create("Textures/장식.png", "#112233").Png;
        File.WriteAllBytes(Path.Combine(w.Files.TexturesPath, "장식.png"), png);
        document.stylegrounds.Add(new MapStyleground { texture = "Textures/장식.png" });
        w.Session.ApplySnapshot("Fixture terrain", MapDocumentStore.Serialize(document));
        var captured = Capture(w);
        var catalog = JsonNode.Parse(captured.Catalog.GetRawText())!;
        catalog["projectPath"] = w.Files.ProjectPath; catalog["privateSettings"] = new JsonObject { ["path"] = w.Files.ProjectPath };
        var definition = new { id = "Decoration", name = "Decoration", color = "#FFFFFF", layer = 4, placement = 0,
            width = 1, height = 1, minimumWidth = 1, minimumHeight = 1, minimumNodes = 0, maximumNodes = 0,
            resizable = true, rotatable = true, flippable = true, properties = Array.Empty<object>(),
            sprite = new { asset = "Textures/장식.png", x = 0, y = 0, width = 16, height = 16 } };
        catalog["objects"]!.AsArray().Add(JsonSerializer.SerializeToNode(definition));
        captured = captured with { Catalog = Element(catalog) };
        File.WriteAllText(Path.Combine(w.Files.TexturesPath, "unused.txt"), "not a resource");
        File.WriteAllText(Path.Combine(w.Files.MapsPath, "unrelated.json"), "unrelated map");
        long revision = w.DocumentRevision; bool dirty = w.State().dirty; string before = w.Session.CurrentJson;
        using var output = new MemoryStream(); ProjectBundleExporter.Write(captured, w.Files, output);
        Check(w.Session.CurrentJson == before && w.DocumentRevision == revision && w.State().dirty == dirty, "Export cannot mutate edits, save state or history.");
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        Check(Encoding.UTF8.GetString(Bytes(archive.GetEntry("Maps/world.map.json")!)) == before, "Unsaved foreground, background and metadata travel as exact compact UTF-8 JSON.");
        Check(archive.Entries.Count(e => e.FullName == "Textures/장식.png") == 1, "An object/backdrop shared image is copied once.");
        Check(archive.Entries.All(e => !e.FullName.Contains("unrelated") && !e.FullName.Contains("unused") && !e.FullName.Contains("Recovery")), "No directory-wide copying or unrelated maps.");
        string packedCatalog = Encoding.UTF8.GetString(Bytes(archive.GetEntry(".studio/catalog.json")!));
        var packed = JsonNode.Parse(packedCatalog)!;
        Check(packed["projectPath"]!.GetValue<string>() == "." && packed["privateSettings"] == null, "Machine-specific catalog metadata stays out of the archive.");
        Check(!packedCatalog.Contains('\n') && !packedCatalog.Contains("\\u005C"), "Catalog is compact and has no captured local path.");
        string destination = Path.Combine(w.Files.ProjectPath, "roundtrip"); Directory.CreateDirectory(destination);
        archive.ExtractToDirectory(destination);
        var freshFiles = new ProjectFiles(destination); var freshCatalog = new Catalog(freshFiles); freshCatalog.Refresh();
        var loaded = MapDocumentStore.Load(Path.Combine(freshFiles.MapsPath, "world.map.json"));
        Check(MapDocumentStore.Serialize(loaded) == before && freshCatalog.Materials.Count == 2, "A fresh workspace loads the exported data with no source workspace or installation fallback.");
        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("Textures/", StringComparison.Ordinal)))
            Check(File.ReadAllBytes(freshFiles.Asset(entry.FullName)).SequenceEqual(File.ReadAllBytes(w.Files.Asset(entry.FullName))), "Exact resource bytes: " + entry.FullName);
        foreach (var material in freshCatalog.Data.GetProperty("materials").EnumerateArray())
        {
            var settings = JsonSerializer.Deserialize<PaletteTileset>(material.GetProperty("editorTileset"), Catalog.Json)!;
            var sources = settings.slots.Select(s => s?.asset).OfType<string>().Append(settings.source).Distinct().ToDictionary(s => s, s => PngRaster.Decode(File.ReadAllBytes(freshFiles.Asset(s))));
            string asset = material.GetProperty("sprites")[0].GetProperty("asset").GetString()!;
            var composed = TilesetComposer.Compose(asset, material.GetProperty("color").GetString()!, settings, sources[settings.source], sources);
            Check(PngRaster.Decode(composed.Png).Pixels.SequenceEqual(PngRaster.Decode(File.ReadAllBytes(freshFiles.Asset(asset))).Pixels), "4-source and 47-source palettes remain editable and pixel-equivalent after extraction.");
        }
    }

    public static void Boundaries(EditorWorkspace w)
    {
        Send(w, "paletteAdd", ("name", "Bundle checks"), ("color", "#203040"));
        var snapshot = Capture(w);
        ProjectBundleSnapshot Asset(string path, bool add = false)
        {
            var catalog = JsonNode.Parse(snapshot.Catalog.GetRawText())!;
            var sprites = catalog["materials"]![0]!["sprites"]!.AsArray();
            if (add) { var extra = sprites[0]!.DeepClone(); extra["asset"] = path; sprites.Add(extra); }
            else foreach (var sprite in sprites) sprite!["asset"] = path;
            return snapshot with { Catalog = Element(catalog) };
        }
        foreach (string path in new[] { "Textures/../outside.png", "Textures/a\\b.png", "Textures/con.png", "Textures//bad.png" })
            Reject(() => ProjectBundleExporter.Write(Asset(path), w.Files, new MemoryStream()), "@bundleInvalidPath");
        string original = snapshot.Catalog.GetProperty("materials")[0].GetProperty("sprites")[0].GetProperty("asset").GetString()!;
        Reject(() => ProjectBundleExporter.Write(Asset("Textures/" + original[9..].ToUpperInvariant(), true), w.Files, new MemoryStream()), "@bundlePathCollision");
        Reject(() => ProjectBundleExporter.Write(Asset("Textures/missing.png"), w.Files, new MemoryStream()), "@bundleAssetUnavailable");
        string huge = Path.Combine(w.Files.TexturesPath, "huge.png");
        using (var file = File.Create(huge)) file.SetLength(ProjectBundleExporter.MaximumImageBytes + 1);
        Reject(() => ProjectBundleExporter.Write(Asset("Textures/huge.png"), w.Files, new MemoryStream()), "@bundleTooLarge");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { ProjectBundleExporter.Write(snapshot, w.Files, new MemoryStream(), canceled.Token); throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
        Reject(() => w.CaptureProjectBundle("other", w.DocumentRevision, w.CatalogRevision), "@bundleChanged");
        Reject(() => w.CaptureProjectBundle(w.InstanceId, w.DocumentRevision - 1, w.CatalogRevision), "@bundleChanged");
        Reject(() => w.CaptureProjectBundle(w.InstanceId, w.DocumentRevision, w.CatalogRevision - 1), "@bundleChanged");
    }

    public static void Fallback(EditorWorkspace w)
    {
        string install = Path.Combine(w.Files.ProjectPath, "installation");
        string directory = Path.Combine(install, "samples", "textures"); Directory.CreateDirectory(directory);
        var atlas = PaletteAtlas.Create("Textures/fallback.png", "#234567"); File.WriteAllBytes(Path.Combine(directory, "fallback.png"), atlas.Png);
        var catalog = new { camera = new { ppu = 16, referenceWidth = 320, referenceHeight = 180, orthographicSize = 5.625 },
            materials = new[] { new { id = "terrain", name = "Fallback", color = "#234567", sprites = atlas.Sprites } }, objects = Array.Empty<object>() };
        var snapshot = Capture(w) with { Catalog = JsonSerializer.SerializeToElement(catalog, Catalog.Json) };
        using var output = new MemoryStream();
        ProjectBundleExporter.Write(snapshot, new ProjectFiles(Path.Combine(w.Files.ProjectPath, "fallback-project"), studioRoot: install), output);
        output.Position = 0; using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        Check(Bytes(archive.GetEntry("Textures/fallback.png")!).SequenceEqual(atlas.Png), "Fallback images are materialized into the archive.");
    }

    public static void Concurrency(EditorWorkspace w)
    {
        Send(w, "paletteAdd", ("name", "Snapshot"), ("color", "#203040"));
        var snapshot = Capture(w);
        using var blocked = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        using var output = new HookStream(_ => { blocked.Set(); Check(resume.Wait(10000), "Test writer resumed."); });
        var worker = Task.Run(() => ProjectBundleExporter.Write(snapshot, w.Files, output));
        try
        {
            Check(blocked.Wait(10000), "Export entered worker IO.");
            var edit = Task.Run(() => { lock (w.Gate) Send(w, "roomAdd", ("name", "While exporting"), ("x", 100), ("y", 0)); });
            Check(edit.Wait(2000), "Compression cannot hold the editing gate.");
        }
        finally { resume.Set(); worker.GetAwaiter().GetResult(); }
        output.Position = 0; using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        Check(Encoding.UTF8.GetString(Bytes(archive.GetEntry("Maps/world.map.json")!)) == snapshot.DocumentJson && w.Session.CurrentJson != snapshot.DocumentJson, "Concurrent edits leave the captured map consistent.");
    }

    public static void ImageChanges(EditorWorkspace w)
    {
        Send(w, "paletteAdd", ("name", "Changing image"), ("color", "#203040"));
        var snapshot = Capture(w); string asset = snapshot.Catalog.GetProperty("materials")[0].GetProperty("sprites")[0].GetProperty("asset").GetString()!;
        string path = w.Files.Asset(asset); byte[] original = File.ReadAllBytes(path);
        // The image was already read when its uncompressed ZIP entry reaches the sink.
        using var output = new HookStream(bytes => File.WriteAllBytes(path, PaletteAtlas.Create(asset, "#987654").Png),
            bytes => bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        try { Reject(() => ProjectBundleExporter.Write(snapshot, w.Files, output), "@bundleImagesChanged"); }
        finally { File.WriteAllBytes(path, original); }
    }
    private delegate bool Match(ReadOnlySpan<byte> bytes);
    private delegate void Hook(ReadOnlySpan<byte> bytes);
    private sealed class HookStream(Hook hook, Match? match = null) : MemoryStream
    {
        private bool fired;
        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count); Fire(buffer.AsSpan(offset, count));
        }
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            byte[] buffer = bytes.ToArray(); base.Write(buffer, 0, buffer.Length); Fire(bytes);
        }
        private void Fire(ReadOnlySpan<byte> bytes)
        {
            if (!fired && (match == null || match(bytes))) { fired = true; hook(bytes); }
        }
    }
}
