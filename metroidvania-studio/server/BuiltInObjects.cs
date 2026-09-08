using System.Text.Json.Nodes;

namespace MetroidvaniaStudio.Server;

/// <summary>Updates standard object fields without changing placed objects or custom definitions.</summary>
public static class BuiltInObjects
{
    public static void Prepare(ProjectFiles files, string studioRoot)
    {
        if (!File.Exists(files.CatalogWritePath)) return;
        string bundledPath = Path.Combine(studioRoot, "samples", "catalog.json");
        if (!File.Exists(bundledPath)) return;
        var source = JsonNode.Parse(File.ReadAllText(bundledPath))?["objects"] as JsonArray;
        if (source == null) return;
        var expected = ProjectFiles.Fingerprint(files.CatalogWritePath);
        var catalog = JsonNode.Parse(File.ReadAllText(files.CatalogWritePath))!;
        if (catalog["objects"] is not JsonArray objects) return;
        bool changed = false;
        foreach (var name in new[] { "Area", "Spawn", "Portal", "Path", "Respawn" })
        {
            var bundled = source.OfType<JsonObject>().FirstOrDefault(o => o["id"]?.GetValue<string>() == name);
            if (bundled == null) continue;
            var existing = objects.OfType<JsonObject>().FirstOrDefault(o => string.Equals(o["id"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                if (name is "Portal" or "Respawn") { objects.Add(bundled.DeepClone()); changed = true; }
                continue;
            }
            if (existing["name"]?.GetValue<string>() != existing["id"]?.GetValue<string>() || existing["properties"] is not JsonArray fields) continue;
            foreach (var field in bundled["properties"]!.AsArray().OfType<JsonObject>().Where(f => f["key"]?.GetValue<string>() is "desc" or "event" or "once"))
            {
                string key = field["key"]!.GetValue<string>();
                var old = fields.OfType<JsonObject>().FirstOrDefault(f => f["key"]?.GetValue<string>() == key);
                if (old == null) { fields.Add(field.DeepClone()); changed = true; }
                else if (key == "event" && name == "Area" && old["kind"]?.GetValue<int>() == 0 && old["defaultValue"]?.GetValue<string>() == ""
                    && old["choices"] is JsonArray choices && choices.Count == 0)
                { fields[fields.IndexOf(old)] = field.DeepClone(); changed = true; }
            }
        }
        if (!changed) return;
        string serialized = catalog.ToJsonString();
        if (System.Text.Encoding.UTF8.GetByteCount(serialized) > Catalog.MaximumCatalogBytes) throw new InvalidDataException("Updated object catalog is too large.");
        files.PublishTextureCatalog(serialized, expected);
    }
}
