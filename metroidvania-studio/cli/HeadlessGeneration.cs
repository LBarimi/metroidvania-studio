using System.Security.Cryptography;
using System.Text.Json;
using MetroidvaniaStudio.Server;

namespace MetroidvaniaStudio.Cli;

internal static class HeadlessGeneration
{
    // npm, a desktop release, and the source build place the CLI at different depths.
    internal static string InstallationRoot()
    {
        foreach (string layout in new[] { "..", "../..", "../../../../.." })
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, layout));
            if (File.Exists(Path.Combine(root, "samples", "catalog.json")) && File.Exists(Path.Combine(root, "docs", "examples", "connected-rooms.lua"))) return root;
        }
        throw new CliException("io_error", "The CLI could not find its bundled samples and script examples. Use the complete installation.");
    }

    internal static object Generate(Options options, WorkspaceFiles files, CancellationToken cancellation)
    {
        options.Only("workspace", "map", "rooms", "seed", "tiles", "theme", "difficulty", "chips", "exclude-chips", "colors", "name", "force", "expected-revision", "dry-run", "read-only");
        string path = options.Required("map");
        bool dryRun = options.Flag("dry-run");
        using var edit = files.BeginEdit(dryRun);
        bool exists = files.Exists(path);
        if (exists && !options.Flag("force")) throw new CliException("conflict", "The map already exists. Use --force to replace it.");
        string? previous = exists ? files.Read(path).Revision : null;
        if (options.Value("expected-revision") is string expected && expected != previous)
            throw new CliException("conflict", "The map revision does not match.");

        string studio = InstallationRoot();
        var project = new ProjectFiles(files.Root, studioRoot: studio, createMapsDirectory: false);
        var catalog = new Catalog(project);
        catalog.Refresh();
        var definitions = catalog.Data.GetProperty("materials").EnumerateArray().ToList();
        if (File.Exists(project.CatalogWritePath))
        {
            // Older projects acquire these two groups on editor startup. Apply the
            // same additions in memory so generation and dry runs leave the catalog alone.
            var bundled = new Catalog(new ProjectFiles(Path.Combine(studio, "samples"), studioRoot: studio, createMapsDirectory: false));
            bundled.Refresh();
            var existingGroups = catalog.PaletteGroups.Select(group => group.id).ToHashSet();
            foreach (var group in bundled.PaletteGroups.Where(g => BuiltInPalettes.IsBundledGroup(g.id)))
            {
                var missing = group.materials.Where(id => !catalog.Materials.ContainsKey(id)).ToHashSet();
                if (catalog.Materials.Count + missing.Count > Catalog.MaximumMaterialDefinitions ||
                    (!existingGroups.Contains(group.id) && existingGroups.Count >= 128)) continue;
                foreach (string id in missing) catalog.Materials.Add(id, bundled.Materials[id]);
                definitions.AddRange(bundled.Data.GetProperty("materials").EnumerateArray().Where(m => missing.Contains(m.GetProperty("id").GetString()!)));
                if (missing.Count > 0) existingGroups.Add(group.id);
            }
        }
        int count = options.Int("rooms", 64);
        int seed = options.Value("seed") == null ? BitConverter.ToInt32(RandomNumberGenerator.GetBytes(4)) : options.Int("seed", 0);
        string[] all = MapMinimapChips.ById.Keys.ToArray();
        string[] List(string value) => value.Split(',', StringSplitOptions.TrimEntries);
        var chips = options.Value("chips") is string include ? List(include) : all;
        if (options.Value("exclude-chips") is string exclude)
        {
            if (options.Value("chips") != null) throw new CliException("invalid_input", "Choose --chips or --exclude-chips.");
            var excluded = List(exclude);
            if (excluded.Except(all, StringComparer.Ordinal).Any()) throw new CliException("invalid_input", "Unknown excluded chip size.");
            chips = all.Except(excluded, StringComparer.Ordinal).ToArray();
        }
        string[] colors = options.Value("colors") is string selected ? List(selected) :
            definitions
                .Where(m => m.GetProperty("id").GetString() is not (MapTilemapGenerator.Ladder or MapTilemapGenerator.Floor))
                .Select(m => m.GetProperty("color").GetString()!).Where(s => s.Length >= 7).Select(s => s[..7])
                .Prepend(MapMinimapChips.DefaultColor).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var doc = MapDocument.CreateDefault();
        doc.name = options.Value("name") ?? "Untitled";
        var camera = MapCameraSettings.Resolve(doc, catalog.Data.GetProperty("camera").Deserialize<CameraProfile>(Catalog.Json));
        MapCameraSettings.Apply(doc, camera.ppu, camera.referenceWidth, camera.referenceHeight);
        bool tiles = options.Flag("tiles") || options.Value("theme") != null || options.Value("difficulty") != null;
        MapMinimapGenerator.Populate(doc, count, chips, colors, options.Value("colors") == null, seed, catalog.Materials,
            tiles, options.Value("theme") ?? "auto", options.Value("difficulty") ?? "progressive");
        string json = MapDocumentStore.Serialize(doc);
        cancellation.ThrowIfCancellationRequested();
        string? revision = dryRun ? previous : files.Write(path, json, previous, !exists, cancellation);
        return new { path, seed, roomCount = doc.rooms.Count, foregroundCells = doc.rooms.Sum(r => r.foreground.Count), dryRun, revision };
    }
}
