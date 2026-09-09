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
        foreach (var name in new[] { "Area", "Spawn", "Portal", "InvisibleWall", "Path", "Respawn" })
        {
            var bundled = source.OfType<JsonObject>().FirstOrDefault(o => o["id"]?.GetValue<string>() == name);
            if (bundled == null) continue;
            var existing = objects.OfType<JsonObject>().FirstOrDefault(o => string.Equals(o["id"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                if (name is "Portal" or "InvisibleWall" or "Respawn") { objects.Add(bundled.DeepClone()); changed = true; }
                continue;
            }
            if (existing["name"]?.GetValue<string>() != existing["id"]?.GetValue<string>() || existing["properties"] is not JsonArray fields) continue;
            if (MapObjectEditing.IsCellBrushDefinition(name) && existing["placement"]?.GetValue<int>() == 1
                && new[] { "layer", "width", "height", "minimumWidth", "minimumHeight", "resizable", "minimumNodes", "maximumNodes" }
                    .All(key => JsonNode.DeepEquals(existing[key], bundled[key])))
            {
                existing["placement"] = bundled["placement"]!.DeepClone();
                changed = true;
            }
            if (name == "Portal")
            {
                foreach (var field in fields.OfType<JsonObject>().ToArray())
                {
                    bool oldEvent = field["key"]?.GetValue<string>() == "event" && field["kind"]?.GetValue<int>() == 5
                        && field["choices"] is JsonArray options && options.Count == 201 && options[0]?.GetValue<string>() == "None";
                    bool oldOnce = field["key"]?.GetValue<string>() == "once" && field["kind"]?.GetValue<int>() == 3;
                    if (oldEvent || oldOnce) { fields.Remove(field); changed = true; }
                }
            }
            foreach (var field in bundled["properties"]!.AsArray().OfType<JsonObject>().Where(f => f["key"]?.GetValue<string>() is "desc" or "event" or "once"))
            {
                string key = field["key"]!.GetValue<string>();
                var old = fields.OfType<JsonObject>().FirstOrDefault(f => f["key"]?.GetValue<string>() == key);
                if (old == null) { fields.Add(field.DeepClone()); changed = true; }
                else if (key == "once" && name == "Area" && old["kind"]?.GetValue<int>() == 3
                    && old["defaultValue"]?.GetValue<string>() == "false")
                { old["defaultValue"] = field["defaultValue"]!.DeepClone(); changed = true; }
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
