using MetroidvaniaStudio;
using MetroidvaniaStudio.Primitives;

internal static class RoomRestructureTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Snapshot(MapEditSession session) => MapDocumentStore.Serialize(session.Document);
    private static void Reject(MapEditSession session, Action action, string key)
    {
        string before = Snapshot(session);
        try { action(); throw new Exception("Expected rejection: " + key); }
        catch (InvalidOperationException error) { Check(error.Message == key, error.Message); }
        Check(Snapshot(session) == before && !session.CanUndo, "A rejected operation must leave data and history intact.");
    }
    private static MapRoom Room(string id, int x = 0, int y = 0, int width = 8, int height = 8) => new()
    {
        id = id, name = id, x = x, y = y, width = width, height = height,
        foreground = new() { new() { x = 1, y = 1, shape = TileShape.BottomLeft, material = "rock" } },
        background = new() { new() { x = 2, y = 2, material = "ice" } },
        objects = new() { new() { id = id + "-object", x = 2, y = 3, width = 2, height = 1, layer = MapLayer.Triggers,
            nodes = new() { new(3, 5) }, properties = new() { new() { key = "desc", value = "Door" }, new() { key = "once", value = "true" } } } }
    };
    private static string Contents(MapDocument document)
    {
        var values = new List<string>();
        foreach (MapRoom room in document.rooms)
        {
            for (int layer = 0; layer < 2; layer++)
                foreach (MapCell cell in layer == 0 ? room.foreground : room.background)
                    values.Add($"{layer}:{(long)room.x + cell.x}:{(long)room.y + cell.y}:{cell.shape}:{cell.material}:{cell.groupId}");
            foreach (MapObject item in room.objects)
            {
                var copy = MapJson.FromJson<MapObject>(MapJson.ToJson(item));
                copy.x += room.x; copy.y += room.y;
                copy.nodes = copy.nodes.Select(node => new Vector2(node.x + room.x, node.y + room.y)).ToList();
                values.Add(MapJson.ToJson(copy));
            }
        }
        return string.Join("\n", values.Order(StringComparer.Ordinal));
    }
    private static void Coverage(MapDocument document, RectInt bounds)
    {
        Check(document.rooms.Sum(room => (long)room.width * room.height) == (long)bounds.width * bounds.height, "Partition preserves total area.");
        foreach (MapRoom room in document.rooms)
            Check(room.x >= bounds.x && room.y >= bounds.y && (long)room.x + room.width <= (long)bounds.x + bounds.width
                && (long)room.y + room.height <= (long)bounds.y + bounds.height, "All parts stay inside the source bounds.");
        for (int i = 0; i < document.rooms.Count; i++) for (int j = i + 1; j < document.rooms.Count; j++)
        {
            MapRoom a = document.rooms[i], b = document.rooms[j];
            Check(a.x + a.width <= b.x || b.x + b.width <= a.x || a.y + a.height <= b.y || b.y + b.height <= a.y, "Parts must not overlap.");
        }
        Check(document.rooms.Select(room => room.name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == document.rooms.Count, "Unique room names.");
    }
    public static void Merge()
    {
        var document = new MapDocument { rooms = new() { Room("A", -12, -4), Room("B", -2, -4) } };
        document.rooms[0].properties.Add(new() { key = "mapMaker.minimapColor", value = "#00ff00" });
        document.rooms[1].properties.Add(new() { key = "mapMaker.minimapColor", value = "#0000ff" });
        document.rooms[0].properties.Add(new() { key = "note", value = "Keep this" });
        document.layerGroups.Add(new() { id = "hidden", name = "Hidden", layer = MapLayer.BackgroundTiles, visible = false, locked = true });
        document.rooms[0].background[0].groupId = "hidden";
        var session = new MapEditSession(document); using var edit = new MapRoomEditing(session);
        edit.Select("A"); edit.Select("B", additive: true);
        string before = Snapshot(session), contents = Contents(session.Document);
        string id = edit.MergeSelected(); MapRoom merged = session.Document.rooms.Single();
        Check(id == "B" && merged.name == "B" && merged.x == -12 && merged.width == 18 && merged.y == -4 && merged.height == 8, "Primary identity and enclosing bounds.");
        Check(merged.properties.Single(p => p.key == "mapMaker.minimapColor").value == "#0000ff", "Active room color.");
        Check(merged.properties.Any(p => p.key == "note" && p.value == "Keep this"), "Distinct custom properties survive.");
        Check(Contents(session.Document) == contents, "All shapes, layers, nodes, IDs and properties retain world positions.");
        Check(edit.SelectedIds.SetEquals(new[] { "B" }), "Merged room is selected.");
        string after = Snapshot(session); session.Undo(); Check(Snapshot(session) == before, "Merge is one Undo.");
        session.Redo(); Check(Snapshot(session) == after, "Merge Redo is exact.");
        Check(Contents(MapDocumentStore.Deserialize(after)) == contents, "Merged JSON round-trips.");
    }
    public static void MergeValidation()
    {
        void Case(MapDocument document, string key, Action<MapRoomEditing> select = null)
        {
            var session = new MapEditSession(document); using var edit = new MapRoomEditing(session);
            edit.Select("A"); if (select != null) select(edit); else edit.Select("B", additive: true);
            Reject(session, () => edit.MergeSelected(), key);
        }
        Case(new() { rooms = new() { Room("A") } }, "@roomMergeNeedSelection", _ => { });
        Case(new() { rooms = new() { Room("A"), Room("B", 7) } }, "@roomMergeOverlap");
        Case(new() { rooms = new() { Room("A"), Room("B", 1024) } }, "@roomMergeTooLarge");
        Case(new() { rooms = new() { Room("A"), Room("B", 16), Room("C", 8) } }, "@roomMergeObstacle");
        var conflict = new MapDocument { rooms = new() { Room("A"), Room("B", 8) } };
        conflict.rooms[0].properties.Add(new() { key = "note", value = "A" }); conflict.rooms[1].properties.Add(new() { key = "note", value = "B" });
        Case(conflict, "@roomMergeProperties");
        var styles = new MapDocument { rooms = new() { Room("A"), Room("B", 8) }, stylegrounds = new() { new() { id = "style", properties = new() { new() { key = "roomFilter", value = "A" } } } } };
        Case(styles, "@roomRestructureStyle");
    }
    public static void Split()
    {
        RectInt[] selections = { new(3, 2, 4, 4), new(0, 0, 4, 4), new(6, 4, 4, 4), new(0, 4, 4, 4), new(6, 0, 4, 4),
            new(0, 0, 10, 3), new(0, 5, 10, 3), new(0, 0, 3, 8), new(7, 0, 3, 8), new(0, 3, 10, 2), new(4, 0, 2, 8) };
        foreach (RectInt area in selections)
        {
            MapRoom source = Room("original", -7, -5, 10, 8); source.objects.Clear(); source.foreground.Clear(); source.background.Clear();
            for (int y = 0; y < source.height; y++) for (int x = 0; x < source.width; x++)
                (x % 2 == 0 ? source.foreground : source.background).Add(new() { x = x, y = y, shape = (TileShape)((x + y) % 5), material = "theme" });
            source.properties.Add(new() { key = "note", value = "Shared settings" });
            var session = new MapEditSession(new() { rooms = new() { source } }); using var edit = new MapRoomEditing(session);
            string before = Snapshot(session), contents = Contents(session.Document);
            string id = edit.SplitArea("original", area);
            var selected = session.Document.rooms.Single(room => room.id == id);
            Check(selected.x == source.x + area.x && selected.y == source.y + area.y && selected.width == area.width && selected.height == area.height, "Selected part gets exact world bounds.");
            Check(id != "original" && session.Document.rooms[0].id == "original", "Original identity stays in the remainder.");
            Coverage(session.Document, new(source.x, source.y, source.width, source.height));
            Check(Contents(session.Document) == contents, "Every tile survives exactly once at the same world coordinate.");
            Check(session.Document.rooms.All(room => room.properties.Single().value == "Shared settings"), "Room settings copied.");
            string after = Snapshot(session); session.Undo(); Check(Snapshot(session) == before, "Split Undo exact.");
            session.Redo(); Check(Snapshot(session) == after, "Split Redo keeps generated IDs.");
            Check(Contents(MapDocumentStore.Deserialize(after)) == contents, "Split JSON round-trips.");
        }
    }
    public static void SplitObjects()
    {
        var source = Room("source", 100, -20, 12, 10);
        source.objects = new() { new() { id = "123", x = 4, y = 3, width = 2, height = 2, layer = MapLayer.Triggers, nodes = new() { new(5, 4) } },
            new() { id = "legacy-id", x = 1, y = 0, width = 10, height = 1, layer = MapLayer.ForegroundDecals } };
        var style = new MapStyleground { id = "style", properties = new() { new() { key = "roomFilter", value = "source" } } };
        var session = new MapEditSession(new() { rooms = new() { source }, stylegrounds = new() { style } });
        using var edit = new MapRoomEditing(session);
        string before = Snapshot(session), contents = Contents(session.Document);
        string id = edit.SplitArea(source.id, new(3, 2, 5, 5));
        Check(session.Document.rooms.Single(room => room.id == id).objects.Single().id == "123", "Contained trigger goes to selected room.");
        Check(Contents(session.Document) == contents, "Horizontal fallback cut retains the wide decal and all nodes and IDs.");
        Check(session.Document.rooms.All(room => MapStylegroundEditing.MatchesRoom(session.Document, "style", room.name)), "Explicit backdrop filter follows all parts.");
        session.Undo(); Check(Snapshot(session) == before, "Style filter changes share the split Undo.");
        session.Document.rooms[0].objects[0].width = 8;
        Reject(session, () => edit.SplitArea(source.id, new(3, 2, 5, 5)), "@roomSplitCrossingObject");
    }
    public static void SplitValidation()
    {
        var session = new MapEditSession(new() { rooms = new() { Room("A") } }); using var edit = new MapRoomEditing(session);
        Reject(session, () => edit.SplitArea("A", new(0, 0, 8, 8)), "@roomSplitWholeRoom");
        Reject(session, () => edit.SplitArea("A", new(7, 0, 2, 1)), "@roomSplitNeedArea");
        Reject(session, () => edit.SplitArea("A", new(0, 0, 0, 1)), "@roomSplitNeedArea");
        session.Document.rooms[0].objects[0].nodes.Add(new(7, 7));
        Reject(session, () => edit.SplitArea("A", new(0, 0, 5, 8)), "@roomSplitCrossingObject");
        session.Document.rooms[0].objects.Clear();
        for (int i = 1; i < MapDocument.MaximumRoomCount; i++) session.Document.rooms.Add(new() { id = "other" + i, name = "other" + i, x = 100 + i * 2, width = 1, height = 1 });
        Reject(session, () => edit.SplitArea("A", new(0, 0, 4, 8)), "@roomSplitLimit");
    }
}
