using System.Text.Json;
using System.Text.Json.Nodes;

namespace MetroidvaniaStudio.Server;

/// <summary>Adds bundled themes without replacing workspace palettes or their placement.</summary>
public static class BuiltInPalettes
{
    public static bool IsBundledGroup(string? id) => id is "default-themes" or "traversal";
    public static void Prepare(ProjectFiles files, string studioRoot)
    {
        string sample = Path.Combine(studioRoot, "samples", "catalog.json");
        if (!File.Exists(sample)) return;
        var bundled = JsonNode.Parse(File.ReadAllText(sample));
        if (bundled?["materials"] is not JsonArray definitions || bundled["editorPaletteGroups"] is not JsonArray groups) return;
        foreach (var group in groups.OfType<JsonObject>())
            if (IsBundledGroup(group["id"]?.GetValue<string>()))
                PrepareGroup(files, definitions, group);
    }

    private static void PrepareGroup(ProjectFiles files, JsonArray definitions, JsonObject group)
    {
        string groupId = group["id"]!.GetValue<string>(), groupName = group["name"]!.GetValue<string>();
        var ids = group["materials"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var themes = definitions.OfType<JsonObject>().Where(m => ids.Contains(m["id"]!.GetValue<string>())).ToArray();
        // Resolve every source and destination through the existing resource boundary.
        foreach (var material in themes)
        {
            foreach (string asset in material["sprites"]!.AsArray().Select(s => s!["asset"]!.GetValue<string>()).Distinct())
            {
                string destination = files.TextureDestination(asset);
                if (File.Exists(destination)) continue;
                string source = files.Asset(asset);
                if (!File.Exists(source)) throw new FileNotFoundException("A bundled theme texture is missing.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.Copy(source, temporary); File.Move(temporary, destination); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        if (!File.Exists(files.CatalogWritePath)) return; // A new workspace reads the complete sample catalog.
        var expected = ProjectFiles.Fingerprint(files.CatalogWritePath);
        var catalog = new Catalog(files); catalog.Refresh();
        var existingGroups = catalog.PaletteGroups;
        var missing = themes.Where(t => !catalog.Materials.ContainsKey(t["id"]!.GetValue<string>())).ToArray();
        if (missing.Length == 0 || catalog.Materials.Count + missing.Length > Catalog.MaximumMaterialDefinitions) return;
        var targetGroup = existingGroups.FirstOrDefault(g => g.id == groupId);
        if (targetGroup == null && existingGroups.Length >= 128) return;
        var next = JsonNode.Parse(catalog.Data.GetRawText())!;
        var materials = next["materials"]!.AsArray();
        foreach (var item in missing) materials.Add(item.DeepClone());
        var nextGroups = existingGroups.ToList();
        var additions = missing.Select(t => t["id"]!.GetValue<string>()).ToArray();
        if (targetGroup == null)
        {
            string name = groupName;
            for (int i = 2; nextGroups.Any(g => g.name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++) name = groupName + " " + i;
            nextGroups.Add(new(groupId, name, additions));
        }
        else nextGroups[nextGroups.IndexOf(targetGroup)] = targetGroup with { materials = targetGroup.materials.Concat(additions).ToArray() };
        next["editorPaletteGroups"] = JsonSerializer.SerializeToNode(nextGroups);
        string serialized = next.ToJsonString();
        if (System.Text.Encoding.UTF8.GetByteCount(serialized) > Catalog.MaximumCatalogBytes) return;
        files.PublishTextureCatalog(serialized, expected);
    }
}
