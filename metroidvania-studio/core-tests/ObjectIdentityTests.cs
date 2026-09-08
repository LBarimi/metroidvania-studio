using System.Globalization;
using MetroidvaniaStudio;
using MetroidvaniaStudio.Primitives;

internal static class ObjectIdentityTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static bool Numeric(string id) => id.Length <= 16 && id.Length > 0 && id[0] != '0'
        && id.All(c => c >= '0' && c <= '9') && ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number)
        && number <= MapObjectIds.MaximumValue && (ulong)(double)number == number;

    public static void Generation()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < 1024; i++) Check(Numeric(MapObjectIds.Create(ids)), "Generated IDs are compact, culture-independent and exactly representable.");
            Check(ids.Count == 1024, "Generated IDs reserve unique entries in the map.");
            string first = MapObjectIds.Derive("same seed", new HashSet<string>());
            var occupied = new HashSet<string> { first };
            string second = MapObjectIds.Derive("same seed", occupied);
            Check(Numeric(first) && Numeric(second) && first != second && occupied.Count == 2, "Derived ID collisions are resolved.");
            Check(MapObjectIds.Derive("same seed", new HashSet<string> { first }) == second, "Collision resolution remains deterministic.");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    public static void Editing()
    {
        const string legacyId = "aabbccddeeff00112233445566778899";
        var document = new MapDocument();
        document.rooms.Add(new MapRoom { id = "origin", name = "Origin", width = 40, height = 24,
            objects = new() { new() { id = legacyId, definition = "point", x = 1, y = 1, layer = MapLayer.Entities } } });
        var session = new MapEditSession(document);
        var canvas = new MapCanvasController(session);
        try
        {
            canvas.SelectRoom("origin"); canvas.Layer = MapLayer.Entities;
            var definition = new MapObjectDefinition { id = "point", displayName = "Point", layer = MapLayer.Entities };
            canvas.ResolveObjectDefinition = _ => definition;
            string placed = canvas.ObjectEditor.Place(definition, new Vector2(3, 3), Vector2.one);
            Check(Numeric(placed), "Placement generates a decimal ID.");
            canvas.ObjectEditor.Copy(); canvas.ObjectEditor.Paste(new Vector2(5, 5));
            var selection = canvas.CaptureSelection(); canvas.PasteSelection(selection, new Vector2(8, 8));
            var originals = session.Document.rooms.Single().objects.Select(o => o.id).ToArray();
            Check(originals.Length == 4 && originals.Where(id => id != legacyId).All(Numeric), "Both clipboard paths generate numeric object IDs.");
            string saved = MapDocumentStore.Serialize(session.Document);
            session.Undo(); session.Redo();
            Check(MapDocumentStore.Serialize(session.Document) == saved, "Undo and redo retain assigned IDs.");
            using var rooms = new MapRoomEditing(session);
            rooms.Select("origin"); rooms.DuplicateSelected();
            rooms.Select("origin"); rooms.CopySelected(); rooms.Paste(new Vector2Int(200, 0));
            var all = session.Document.rooms.SelectMany(r => r.objects).Select(o => o.id).ToArray();
            Check(all.Length == 12 && all.Distinct().Count() == all.Length, "Room duplication and room clipboard IDs are globally unique within the map.");
            Check(all.Where(id => id != legacyId).All(Numeric), "Every copied object has a compact numeric ID.");
            var restored = MapDocumentStore.Deserialize(MapDocumentStore.Serialize(session.Document));
            Check(restored.rooms.SelectMany(r => r.objects).Select(o => o.id).SequenceEqual(all), "JSON keeps numeric strings and legacy identities exactly.");
        }
        finally { canvas.Dispose(); }
    }
}
