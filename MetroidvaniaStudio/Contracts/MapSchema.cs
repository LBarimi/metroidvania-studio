using System.Reflection;
using System.Text;
using System.Text.Json;
using MetroidvaniaStudio;

/// <summary>Generates the portable map structure directly from its authoritative C# data fields.</summary>
internal static class MapSchema
{
    public static bool Write(string output, bool check)
    {
        var pending = new Queue<Type>(new[] { typeof(MapDocument) });
        var definitions = new SortedDictionary<string, object>(StringComparer.Ordinal);
        while (pending.TryDequeue(out Type? type))
        {
            if (definitions.ContainsKey(type.Name)) continue;
            var properties = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsInitOnly || field.IsDefined(typeof(NonSerializedAttribute))) continue;
                var schema = Reference(field.FieldType, pending);
                AddConstraints(type, field.Name, schema);
                properties.Add(field.Name, schema);
            }
            var definition = new Dictionary<string, object>
            {
                ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false
            };
            if (type == typeof(MapDocument)) definition["required"] = new[] { "formatVersion", "tileSize", "rooms", "layerGroups" };
            else if (type == typeof(MapRoom) || type == typeof(MapObject) || type == typeof(MapStyleground) || type == typeof(MapLayerGroup))
                definition["required"] = new[] { "id" };
            definitions.Add(type.Name, definition);
        }
        var root = new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Map document version " + MapDocument.CurrentFormatVersion,
            ["$comment"] = "Generated from Core data fields. Identity uniqueness, references, containment and aggregate limits are validated by MapDocumentStore.",
            ["$ref"] = "#/$defs/MapDocument", ["$defs"] = definitions
        };
        string generated = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
        string? current = File.Exists(output) ? File.ReadAllText(output).Replace("\r\n", "\n") : null;
        if (generated == current) return true;
        if (check) { Console.Error.WriteLine("Map JSON schema is stale. Run MetroidvaniaStudio/build-web.mjs."); return false; }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, generated, new UTF8Encoding(false));
        Console.WriteLine("Generated " + Path.GetFileName(output));
        return true;
    }

    private static Dictionary<string, object> Reference(Type type, Queue<Type> pending)
    {
        if (type == typeof(string)) return new() { ["type"] = "string" };
        if (type == typeof(bool)) return new() { ["type"] = "boolean" };
        if (type.IsEnum) return new() { ["type"] = "integer", ["enum"] = Enum.GetValues(type).Cast<object>().Select(Convert.ToInt32).ToArray() };
        if (type == typeof(int)) return new() { ["type"] = "integer", ["minimum"] = int.MinValue, ["maximum"] = int.MaxValue };
        if (type == typeof(float)) return new() { ["type"] = "number", ["minimum"] = -float.MaxValue, ["maximum"] = float.MaxValue };
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return new() { ["type"] = "array", ["items"] = Reference(type.GetGenericArguments()[0], pending) };
        if (type.Assembly != typeof(MapDocument).Assembly || type.IsAbstract || type.IsInterface)
            throw new NotSupportedException("Unsupported map schema type: " + type.FullName);
        pending.Enqueue(type);
        return new() { ["$ref"] = "#/$defs/" + type.Name };
    }

    private static void AddConstraints(Type type, string field, Dictionary<string, object> schema)
    {
        if (type == typeof(MapDocument))
        {
            if (field == "formatVersion") schema["const"] = MapDocument.CurrentFormatVersion;
            if (field == "tileSize") schema["const"] = MapDocument.RequiredTileSize;
            if (field == "rooms") schema["maxItems"] = MapDocument.MaximumRoomCount;
            if (field == "layerGroups") schema["maxItems"] = MapLayerGroups.MaximumGroupCount;
        }
        if (type == typeof(MapRoom))
        {
            if (field is "width" or "height") { schema["minimum"] = 1; schema["maximum"] = MapDocument.MaximumRoomDimension; }
            if (field == "objects") schema["maxItems"] = MapDocument.MaximumObjectsPerRoom;
        }
        if (type == typeof(MapCell) && field is "x" or "y") schema["minimum"] = 0;
        if (type == typeof(MapObject))
        {
            if (field == "layer") schema["enum"] = new[] { 2, 3, 4, 5 };
            if (field == "nodes") schema["maxItems"] = MapDocument.MaximumNodesPerObject;
            if (field is "width" or "height") schema["exclusiveMinimum"] = 0;
        }
        if (type == typeof(MapStyleground) && field == "layer") schema["enum"] = new[] { 4, 5 };
        if (type == typeof(MapLayerGroup) && field == "layer") schema["enum"] = new[] { 0, 1, 2, 3, 4, 5 };
    }
}
