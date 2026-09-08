using System.Text.Json.Nodes;
using MetroidvaniaStudio.Server;

internal static class BuiltInObjectTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Upgrade()
    {
        string studio = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(studio, "samples", "catalog.json"))) studio = Path.GetDirectoryName(studio)!;
        string root = Path.Combine(Path.GetTempPath(), "studio-objects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".studio"));
        try
        {
            var files = new ProjectFiles(root, studioRoot: studio);
            var source = JsonNode.Parse(File.ReadAllText(Path.Combine(studio, "samples", "catalog.json")))!;
            var objects = source["objects"]!.AsArray();
            foreach (var item in objects.ToArray())
            {
                string id = item!["id"]!.GetValue<string>();
                if (id is "Portal" or "Respawn") { objects.Remove(item); continue; }
                var fields = item["properties"]!.AsArray();
                foreach (var field in fields.ToArray())
                {
                    if (field!["key"]!.GetValue<string>() == "desc") fields.Remove(field);
                    else if (id == "Area" && field["key"]!.GetValue<string>() == "event")
                    { field["kind"] = 0; field["defaultValue"] = ""; field["choices"] = new JsonArray(); }
                }
            }
            var path = objects.Single(o => o!["id"]!.GetValue<string>() == "Path")!;
            path["name"] = "Custom path";
            string customPath = path.ToJsonString();
            File.WriteAllText(files.CatalogWritePath, source.ToJsonString());
            BuiltInObjects.Prepare(files, studio);
            var result = JsonNode.Parse(File.ReadAllText(files.CatalogWritePath))!;
            var updated = result["objects"]!.AsArray();
            var area = updated.Single(o => o!["id"]!.GetValue<string>() == "Area")!;
            var events = area["properties"]!.AsArray().Single(f => f!["key"]!.GetValue<string>() == "event")!;
            Check(events["kind"]!.GetValue<int>() == 5 && events["choices"]!.AsArray().Count == 201, "Original trigger field upgrades to 200 events and None.");
            Check(updated.Single(o => o!["id"]!.GetValue<string>() == "Path")!.ToJsonString() == customPath, "Custom definitions remain unchanged.");
            Check(updated.Any(o => o!["id"]!.GetValue<string>() == "Marker") && updated.Any(o => o!["id"]!.GetValue<string>() == "Object"), "Legacy definitions are preserved.");
            var portal = updated.Single(o => o!["id"]!.GetValue<string>() == "Portal")!;
            Check(portal["color"]!.GetValue<string>() == "#FF00FFFF", "Portal is magenta.");
            string stable = File.ReadAllText(files.CatalogWritePath);
            BuiltInObjects.Prepare(files, studio);
            Check(File.ReadAllText(files.CatalogWritePath) == stable, "Migration is idempotent.");
            var catalog = new Catalog(files); catalog.Refresh();
            Check(catalog.Data.GetProperty("objects").GetArrayLength() == updated.Count, "Migrated catalog validates and loads.");
        }
        finally { Directory.Delete(root, true); }
    }
}
