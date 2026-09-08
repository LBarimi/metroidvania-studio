using MetroidvaniaStudio;
using System.Text;
using System.Globalization;
using MetroidvaniaStudio.Primitives;

var tests = new (string name, Action run)[]
{
    ("numeric object ID generation and collision resolution", ObjectIdentityTests.Generation),
    ("numeric object IDs survive placement, all clipboard paths and JSON", ObjectIdentityTests.Editing),
    ("explicit trigger requests, stable events, one-shot portals and reload state", TriggerTests.Requests),
    ("v2 fields and Unicode round trip", RoundTrip),
    ("compact UTF-8 saves and exports preserve numbers across cultures", CompactCultureRoundTrip),
    ("published v2 wire field compatibility", WireCompatibility),
    ("typed JSON accessors preserve public map field semantics", JsonFieldAccess),
    ("large tile JSON avoids per-field boxing allocations", JsonTileAllocations),
    ("v1 migration and strict unknown schema rejection", Schema),
    ("snapshot isolation and atomic rollback", Rollback),
    ("oversized transaction rolls back before commit", DocumentSizeRollback),
    ("undo history obeys its aggregate byte budget", HistoryByteBudget),
    ("large gesture history is incremental and reversible", IncrementalGestureHistory),
    ("object gesture history is incremental and ordered", IncrementalObjectHistory),
    ("paint stroke, theme and one Undo", Paint),
    ("erase indexed cells and redo", Erase),
    ("foreign edit and document replacement protect gestures", Gestures),
    ("save/undo/redo dirty state and malformed load preservation", SaveLoad),
    ("map files require UTF-8 and allow its BOM", MapFileEncoding),
    ("all-layer clear preserves protected groups and other rooms", Clear),
    ("room resize moves only attached directional chains and preserves local data", Resize),
    ("detached rooms and corner contacts stay fixed across all resize handles", ResizeDetached),
    ("resize cannot push disconnected obstacles and remains atomic", ResizeObstacles),
    ("room shrink clamps all eight handles to both terrain layers", ShrinkToTerrain),
    ("room shrink preserves crop, empty and local-coordinate behavior", ShrinkOptions),
    ("room creation preserves free coordinates and avoids occupied space in one Undo", RoomCreation),
    ("group movement preserves gaps and resolves collisions in every direction", RoomGroupCollision),
    ("room clipboard, transforms and collision-only joins", RoomWorkflow),
    ("locked neighbor blocks resize atomically", LockedResize),
    ("node selection transforms and node-only deletion", Nodes),
    ("clipboard deep copy and slope transform", Clipboard),
    ("clipboard paste is bulk, incremental and bounded", ClipboardBulkAndLimits),
    ("maximum object paste selection and redo remain responsive", ClipboardObjectStress),
    ("maximum object erase undo and redo remain responsive", ObjectEraseStress),
    ("47 canonical masks and slope connectivity", Masks),
    ("maximum room bucket and geometry limits", Geometry),
    ("document complexity and resize work are bounded", ComplexityLimits),
    ("minimap connection output is bounded", MiniMapConnectionLimit),
    ("map geometry arithmetic conventions", Values),
    ("minimap negative-coordinate connection geometry", MiniMap),
    ("room export filename collision, UTF-8 and source preservation", RoomExport),
    ("room export aggregate output is bounded before I/O", RoomExportAggregateLimit),
    ("resource metadata preserves identity and source geometry", Resources),
    ("styleground opaque fields and effective parent alpha", Stylegrounds),
    ("shared locale CSV quoted parsing", Locale),
    ("web enum and five-theme localization remains complete", LocaleEnums)
};
int failed = 0;
foreach (var test in tests)
{
    try { test.run(); Console.WriteLine("PASS " + test.name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + test.name + "\n" + error); }
}
Console.WriteLine($"Core regression: {tests.Length - failed}/{tests.Length} passed.");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static T Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T error) { return error; }
    throw new Exception("Expected " + typeof(T).Name);
}
static string Json(MapDocument value) => MapDocumentStore.Serialize(value);
static byte[] WithPreamble(Encoding encoding, string text)
    => encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
static MapRoom Room(string id, int x = 0, int y = 0, int width = 10, int height = 10) =>
    new MapRoom { id = id, name = id, x = x, y = y, width = width, height = height };
static MapDocument Doc(params MapRoom[] rooms) => new MapDocument { rooms = rooms.ToList() };
static void InFiles(Action<string> action)
{
    string project = Path.GetFullPath("../../..", AppContext.BaseDirectory);
    if (!File.Exists(Path.Combine(project, "MetroidvaniaStudio.Core.Tests.csproj"))) throw new Exception("Test output must remain under Core.Tests.");
    string path = Path.Combine(project, ".test-output", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    try { action(path); } finally { Directory.Delete(path, true); }
}
static void CompactCultureRoundTrip() => InFiles(directory =>
{
    var document = Doc(Room("room", -12, 3)); document.name = "한글 日本語 中文 🌿";
    document.rooms[0].objects.Add(new MapObject { id = "object", x = 1.25f, y = -0.5f, width = 2.75f,
        rotation = -45.25f, scaleX = -1.5f, nodes = new List<Vector2> { new(3.5f, -7.25f) } });
    document.stylegrounds.Add(new MapStyleground { id = "style", scrollX = 0.125f, scrollY = -2.5f });
    document.properties.Add(new MapProperty { key = "note", value = "keep  two spaces\nnext line\t1,25" });
    CultureInfo culture = CultureInfo.CurrentCulture, uiCulture = CultureInfo.CurrentUICulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string expected = Json(document), legacy = MapDocumentStore.Serialize(document, true);
        byte[] expectedBytes = new UTF8Encoding(false, true).GetBytes(expected);
        Check(!expected.Contains('\n') && expected.Contains("\"x\":1.25") && expected.Contains("\"y\":-0.5"),
            "Compact JSON must preserve standard decimal points and signs without formatting whitespace.");
        foreach (string name in new[] { "ko-KR", "en-US", "de-DE", "fr-FR", "ar-SA", "tr-TR", "ja-JP", "zh-CN" })
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(name);
            Check(Json(MapDocumentStore.Deserialize(legacy)) == expected, "Pretty legacy JSON must load with identical values in " + name);
            string savedPath = Path.Combine(directory, "saved.json");
            Check(MapDocumentStore.SaveSnapshot(savedPath, document) == expected && File.ReadAllBytes(savedPath).SequenceEqual(expectedBytes),
                "Direct saves must contain the same compact UTF-8 bytes in " + name);
            var session = new MapEditSession(document);
            string sessionPath = Path.Combine(directory, "session.json");
            Check(session.SaveAndGetPersistedJson(sessionPath) == expected && !session.IsDirty,
                "Session saves must retain compact bytes and saved-state semantics in " + name);
            string exportedPath = MapRoomJsonExporter.Export(document, Path.Combine(directory, "rooms"), overwrite: true).Single();
            Check(File.ReadAllBytes(exportedPath).SequenceEqual(expectedBytes), "Room exports must share the compact save format in " + name);
            Check(Json(MapDocumentStore.Load(exportedPath)) == expected && Json(MapDocumentStore.Load(savedPath)) == expected,
                "Every culture must load the numeric and Unicode values without changing user text.");
        }
    }
    finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = uiCulture; }
});

static void WireCompatibility()
{
    const string documentJson = """
        {"formatVersion":2,"tileSize":16,"name":"Contract","rooms":[{"id":"room-a","name":"A","x":-12,"y":7,"width":40,"height":24,"visible":true,"locked":false,"foreground":[{"x":1,"y":2,"shape":4,"material":"terrain","groupId":""}],"background":[],"objects":[{"id":"object-a","definition":"marker","layer":2,"groupId":"","x":1.25,"y":2.5,"width":1,"height":2,"rotation":90,"scaleX":-1,"scaleY":1,"nodes":[{"x":3.5,"y":-7.25}],"properties":[{"key":"label","value":"test"}]}],"properties":[]}],"properties":[],"stylegrounds":[{"id":"style-a","name":"Background","type":"custom.effect","layer":5,"texture":"Textures/background.png","color":"#FFFFFF","scrollX":0.5,"scrollY":1,"properties":[{"key":"speedX","value":"2"}]}],"layerGroups":[]}
        """;
    MapDocument document = MapDocumentStore.Deserialize(documentJson);
    Check(MapDocumentStore.Serialize(document) == documentJson,
        "Published v2 fields, enum numbers, coordinates, opaque resource IDs and custom properties must retain their exact wire representation.");
    Check(document.rooms[0].foreground[0].shape == TileShape.TopRight
        && document.rooms[0].objects[0].nodes[0].Equals(new Vector2(3.5f, -7.25f)),
        "Wire shape numbers and positive-Y-up coordinates must retain their meaning.");
}
static void RoundTrip()
{
    var doc = Doc(Room("room", -19, 42)); doc.name = "한글 맵 🌲";
    var fg = new MapLayerGroup { id = "fg", layer = MapLayer.ForegroundTiles, name = "바닥" };
    doc.layerGroups.Add(fg);
    doc.rooms[0].foreground.Add(new MapCell { x = 1, y = 2, shape = TileShape.TopRight, material = "GUID-재료", groupId = fg.id });
    doc.rooms[0].background.Add(new MapCell { x = 3, y = 4, shape = TileShape.BottomLeft });
    foreach (MapLayer layer in new[] { MapLayer.Entities, MapLayer.Triggers, MapLayer.ForegroundDecals, MapLayer.BackgroundDecals })
        doc.rooms[0].objects.Add(new MapObject { definition = "CaseSensitive.Type", layer = layer, x = 1.25f, y = -0.5f,
            rotation = 90, scaleX = -1, nodes = new List<Vector2> { new(3.5f, -7.25f), new(9, 2) },
            properties = new List<MapProperty> { new() { key = "arbitrary", value = "원문,\"line\"\n둘" } } });
    doc.properties.Add(new MapProperty { key = "metadata", value = "preserved" });
    string json = Json(doc);
    Check(!json.Contains("sqrMagnitude") && !json.Contains("normalized"), "Calculated vector properties must never enter the map schema.");
    var clone = MapDocumentStore.Deserialize(json);
    Check(Json(clone) == json && clone.name == doc.name, "All fields and identities must round trip.");
    clone.rooms[0].objects[0].nodes[0] = Vector2.zero;
    Check(doc.rooms[0].objects[0].nodes[0].x == 3.5f, "Clone must own node collections.");
}
static void Schema()
{
    string old = Json(MapDocument.CreateDefault()).Replace("\"formatVersion\":2", "\"formatVersion\":1").Replace(",\"layerGroups\":[]", "");
    Check(MapDocumentStore.Deserialize(old).formatVersion == 2, "v1 document migration");
    Throws<InvalidDataException>(() => MapDocumentStore.Deserialize(old.Replace("\"formatVersion\":1", "\"formatVersion\":999")));
    Throws<InvalidDataException>(() => MapDocumentStore.Deserialize(old.Replace("\"tileSize\":16", "\"tileSize\":32")));
    Throws<InvalidDataException>(() => MapDocumentStore.Deserialize(old.Replace("\"tileSize\":16", "\"tileSize\":16,\"futureSecret\":42")));
}
static void Rollback()
{
    var source = MapDocument.CreateDefault(); var session = new MapEditSession(source); string before = Json(session.Document);
    Throws<InvalidOperationException>(() => session.Execute("invalid", d => d.rooms[0].width = 0));
    Check(Json(session.Document) == before && !session.CanUndo && !session.IsEditing, "Invalid transaction must leave no edit or history.");
    session.Execute("no-op", _ => { }); Check(!session.CanUndo, "No-op must not create undo.");
    session.Execute("change", d => d.name = "after");
    Check(source.name != "after", "Session owns its clone."); session.Undo(); Check(Json(session.Document) == before, "Undo snapshot");
}
static void DocumentSizeRollback()
{
    var session = new MapEditSession(MapDocument.CreateDefault());
    string before = Json(session.Document);
    Throws<InvalidDataException>(() => session.Execute("oversized", document => document.properties.Add(new MapProperty
    {
        key = "oversized",
        value = new string('x', checked((int)MapDocumentStore.MaximumFileBytes))
    })));
    Check(Json(session.Document) == before && !session.IsEditing && !session.CanUndo,
        "A document above the durable size limit must restore its pending snapshot and leave no Undo entry.");
    var oversized = MapDocument.CreateDefault();
    oversized.properties.Add(new MapProperty { key = "oversized", value = new string('y', checked((int)MapDocumentStore.MaximumFileBytes)) });
    Throws<InvalidDataException>(() => session.New(oversized));
    Check(Json(session.Document) == before && !session.CanUndo,
        "An oversized New replacement must be rejected before changing the live document.");
}
static void HistoryByteBudget()
{
    const long budget = 4096;
    var session = new MapEditSession(MapDocument.CreateDefault(), historyByteLimit: budget);
    for (int index = 0; index < 24; index++)
    {
        int captured = index;
        session.Execute("rename", document => document.name = captured + "-" + new string('x', 384));
        Check(session.HistoryByteCount <= budget, "History exceeded its aggregate byte budget after a commit.");
    }
    int undoCount = 0;
    while (session.CanUndo && undoCount < 24)
    {
        session.Undo(); undoCount++;
        Check(session.HistoryByteCount <= budget, "History exceeded its aggregate byte budget while moving revisions to Redo.");
    }
    Check(undoCount > 0 && undoCount < 24, "The byte budget must retain recent history and evict older snapshots.");

    var branch = new MapEditSession(MapDocument.CreateDefault(), historyByteLimit: 6000);
    string initial = Json(branch.Document);
    branch.Execute("A", document => document.name = "A" + new string('a', 4000));
    branch.Execute("B", document => document.name = "B" + new string('b', 4000));
    branch.Undo();
    branch.Execute("new branch", document => document.name = "C");
    branch.Undo(); branch.Undo();
    Check(Json(branch.Document) == initial,
        "Discarded Redo bytes must be released before a branched commit trims valid Undo history.");
}
static MapDocument LargeGestureDocument()
{
    var room = Room("large", width: 1024, height: 1024);
    for (int index = 0; index < 20000; index++)
        room.foreground.Add(new MapCell { x = index % 1024, y = index / 1024, material = "terrain" });
    return Doc(room);
}
static void IncrementalGestureHistory()
{
    var session = new MapEditSession(LargeGestureDocument());
    var controller = new MapCanvasController(session);
    try
    {
        string before = Json(session.Document);
        long initialSerializations = session.SnapshotSerializationCount;
        controller.BeginGesture(new Vector2Int(1000, 1000));
        Check(session.SnapshotSerializationCount == initialSerializations,
            "Starting and painting a tile gesture must not serialize the large document for Undo.");
        controller.EndGesture(new Vector2Int(1000, 1000));
        Check(session.SnapshotSerializationCount == initialSerializations + 1,
            "Committing a tile gesture needs only the validated published snapshot.");
        Check(session.HistoryByteCount < 4096 && session.HistoryByteCount * 100 < before.Length,
            "A one-cell Undo record must scale with the changed cell rather than the document JSON.");
        string painted = Json(session.Document);
        session.Undo();
        Check(Json(session.Document) == before, "Incremental tile Undo must restore exact list order and values.");
        session.Redo();
        Check(Json(session.Document) == painted, "Incremental tile Redo must restore the committed snapshot exactly.");

        long cancelSerializations = session.SnapshotSerializationCount;
        controller.BeginGesture(new Vector2Int(999, 999));
        controller.CancelGesture();
        Check(session.SnapshotSerializationCount == cancelSerializations && Json(session.Document) == painted,
            "Cancelling an incomplete delta must restore it without serializing the large document.");

        controller.Material = null;
        controller.BeginGesture(new Vector2Int(998, 998));
        Throws<InvalidOperationException>(() => controller.EndGesture(new Vector2Int(998, 998)));
        Check(Json(session.Document) == painted && !session.IsEditing,
            "Failed final validation must roll an incremental gesture back atomically.");
    }
    finally { controller.Dispose(); }
}
static void IncrementalObjectHistory()
{
    var session = new MapEditSession(LargeGestureDocument());
    var controller = new MapCanvasController(session) { Layer = MapLayer.Entities };
    var definition = new MapObjectDefinition();
    definition.id = "marker"; definition.displayName = "Marker"; definition.layer = MapLayer.Entities;
    controller.ResolveObjectDefinition = _ => definition;
    try
    {
        string before = Json(session.Document);
        long serializations = session.SnapshotSerializationCount;
        string id = controller.ObjectEditor.Place(definition, new Vector2(2, 2), Vector2.one);
        Check(id != null && session.SnapshotSerializationCount == serializations + 1,
            "Object placement must skip the pre-edit document snapshot.");
        Check(session.HistoryByteCount < 4096,
            "Object placement history must contain the object delta instead of the large document JSON.");
        string placed = Json(session.Document);

        serializations = session.SnapshotSerializationCount;
        controller.ObjectEditor.BeginMove();
        controller.ObjectEditor.DragMove(new Vector2(3, 1));
        Check(session.SnapshotSerializationCount == serializations,
            "Dragging an object transform must not serialize the document.");
        controller.ObjectEditor.EndMove();
        Check(session.SnapshotSerializationCount == serializations + 1, "Object transform commit publishes one snapshot.");
        string moved = Json(session.Document);
        session.Undo(); Check(Json(session.Document) == placed, "Object transform Undo");
        session.Redo(); Check(Json(session.Document) == moved, "Object transform Redo");

        using (var eraser = new MapObjectErasing(controller))
        {
            serializations = session.SnapshotSerializationCount;
            Check(eraser.Begin(new Vector2(5.5f, 3.5f)), "Object erase must hit the moved object.");
            Check(session.SnapshotSerializationCount == serializations && controller.Room.objects.Count == 0,
                "Object erase must journal the removal without a pre-edit snapshot.");
            eraser.End();
            Check(session.SnapshotSerializationCount == serializations + 1, "Object erase commit publishes one snapshot.");
        }
        session.Undo(); Check(Json(session.Document) == moved, "Object erase Undo must restore object order and data.");

        // Delta and legacy snapshot revisions must remain interoperable.
        session.Execute("rename", document => document.name = "after-delta");
        session.Undo(); Check(Json(session.Document) == moved, "Snapshot Undo after an incremental revision");
        session.Undo(); Check(Json(session.Document) == placed, "Incremental Undo after snapshot history");
        session.Undo(); Check(Json(session.Document) == before && !session.CanUndo,
            "Mixed history must return to the exact initial document.");
    }
    finally { controller.Dispose(); }
}
static void Paint()
{
    var session = new MapEditSession(Doc(Room("room"))); var c = new MapCanvasController(session);
    try
    {
        var terrain = MapResourceFactory.CreateTerrainMaterial("blue-guid", "Blue", "blue", "#287FC4");
        c.Material = "blue-guid"; c.Shape = TileShape.TopLeft; c.ResolveTerrainTileSet = _ => terrain;
        c.BeginGesture(new(1, 1)); c.DragGesture(new(4, 1)); c.EndGesture(new(4, 1));
        Check(c.Room.foreground.Count == 4 && c.Room.foreground.All(cell => cell.shape == TileShape.TopLeft), "Continuous slope stroke");
        Check(MapRoomTheme.TryGetColor(c.Room, out Color color) && ColorText.ToHtmlStringRGB(color) == "287FC4", "Theme follows actual paint.");
        session.Undo(); Check(c.Room.foreground.Count == 0 && c.Room.properties.Count == 0 && !session.CanUndo, "Tiles and theme are one undo.");
        session.Redo(); Check(c.Room.foreground.Count == 4, "Stroke redo");
    }
    finally { c.Dispose(); }
}
static void Erase()
{
    var d = Doc(Room("room")); for (int x = 0; x < 5; x++) d.rooms[0].foreground.Add(new MapCell { x = x, y = 1 });
    var session = new MapEditSession(d); var c = new MapCanvasController(session);
    try
    {
        c.BeginEraseGesture(new(1, 1)); c.DragGesture(new(3, 1)); c.EndGesture(new(3, 1));
        Check(c.Room.foreground.Select(cell => cell.x).Order().SequenceEqual(new[] { 0, 4 }), "Swap removal must retain unselected cells.");
        session.Undo(); Check(c.Room.foreground.Count == 5, "Erase undo"); session.Redo(); Check(c.Room.foreground.Count == 2, "Erase redo");
    }
    finally { c.Dispose(); }
}
static void Gestures()
{
    var session = new MapEditSession(Doc(Room("room"))); var c = new MapCanvasController(session);
    try
    {
        session.BeginEdit("external"); session.Document.name = "pending";
        Throws<InvalidOperationException>(() => c.BeginGesture(new(1, 1)));
        Check(session.IsEditing && session.Document.name == "pending", "Foreign gesture must not cancel someone else's edit."); session.CancelEdit();
        c.BeginGesture(new(1, 1)); session.New(Doc(Room("fresh"))); c.EndGesture(new(4, 1));
        Check(c.Room.id == "fresh" && c.Room.foreground.Count == 0, "Stale gesture must not paint a replacement document.");
    }
    finally { c.Dispose(); }
}
static void SaveLoad() => InFiles(dir =>
{
    var session = new MapEditSession(Doc(Room("room"))); string path = Path.Combine(dir, "한글.json"); session.Save(path);
    Check(!session.IsDirty, "Save must reset dirty."); session.Execute("rename", d => d.name = "second"); Check(session.IsDirty, "Edit dirty");
    session.Undo(); Check(!session.IsDirty, "Undo to saved snapshot must clear dirty."); session.Redo(); Check(session.IsDirty, "Redo dirty");
    string before = Json(session.Document); string invalid = Path.Combine(dir, "invalid.json"); File.WriteAllText(invalid, "{broken");
    Throws<InvalidDataException>(() => session.Load(invalid));
    string invalidUtf8 = Path.Combine(dir, "invalid-utf8.json"); File.WriteAllBytes(invalidUtf8, new byte[] { 0x7b, 0xff, 0x7d });
    Throws<InvalidDataException>(() => session.Load(invalidUtf8));
    Check(Json(session.Document) == before && session.FilePath == path && session.CanUndo, "Malformed load preserves current history/path.");
    string saved = File.ReadAllText(path); session.Document.rooms[0].width = 0;
    Throws<InvalidOperationException>(() => session.Save(path)); Check(File.ReadAllText(path) == saved, "Invalid save preserves file.");
});
static void MapFileEncoding() => InFiles(dir =>
{
    var document = Doc(Room("room")); document.name = "UTF-8 한글";
    string json = Json(document), path = Path.Combine(dir, "encoding.json");
    File.WriteAllBytes(path, new UTF8Encoding(false, true).GetBytes(json));
    Check(MapDocumentStore.Load(path).name == document.name, "Ordinary UTF-8 must load.");
    File.WriteAllBytes(path, WithPreamble(new UTF8Encoding(true, true), json));
    Check(MapDocumentStore.Load(path).name == document.name, "A UTF-8 BOM must load without becoming JSON content.");

    Encoding[] forbidden =
    {
        new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
        new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true)
    };
    foreach (Encoding encoding in forbidden)
    {
        File.WriteAllBytes(path, WithPreamble(encoding, json));
        Throws<InvalidDataException>(() => MapDocumentStore.Load(path));
    }
});
static void Clear()
{
    var doc = Doc(Room("current"), Room("other", 20));
    doc.layerGroups.Add(new MapLayerGroup { id = "protected", layer = MapLayer.Entities, locked = true });
    doc.rooms[0].objects.Add(new MapObject { id = "editable" });
    doc.rooms[0].objects.Add(new MapObject { id = "locked", groupId = "protected" });
    doc.rooms[0].objects.Add(new MapObject { id = "hidden", layer = MapLayer.Triggers });
    doc.rooms[1].objects.Add(new MapObject { id = "other-object" });
    var s = new MapEditSession(doc); var c = new MapCanvasController(s);
    try
    {
        c.HiddenLayers.Add(MapLayer.Triggers); var result = c.ClearRoomObjects();
        Check(result.Removed == 1 && result.Protected == 2, "Protected count and editable removal");
        Check(c.Room.objects.Count == 2 && s.Document.rooms[1].objects.Count == 1, "Other rooms preserved");
        s.Undo(); Check(c.Room.objects.Count == 3 && !s.CanUndo, "Clear is one undo");
    }
    finally { c.Dispose(); }
}
static void Resize()
{
    foreach (bool horizontal in new[] { true, false }) foreach (int direction in new[] { -1, 1 })
        foreach (int delta in new[] { -2, 4 }) foreach (bool reversed in new[] { false, true })
    {
        MapRoom Neighbor(string id, int distance, int cross, int span) => horizontal
            ? Room(id, direction * distance - (direction < 0 ? 10 : 0), cross, 10, span)
            : Room(id, cross, direction * distance - (direction < 0 ? 10 : 0), span, 10);
        int edge = direction > 0 ? 10 : 0;
        var attached = Neighbor("attached", edge, 2, 6);
        var chain = Neighbor("chain", edge + 10, 3, 3);
        var detached = Neighbor("detached", edge + 25, 3, 3);
        var doc = Doc(Room("target"), attached, chain, detached);
        attached.foreground.Add(new MapCell { x = 2, y = 1 });
        attached.objects.Add(new MapObject { id = "item", x = 1, y = 2, nodes = new List<Vector2> { new(2, 1) } });
        chain.visible = false; // Visibility does not alter physical room contacts.
        detached.locked = true;
        if (reversed) doc.rooms.Reverse();
        var session = new MapEditSession(doc); using var edit = new MapRoomEditing(session);
        int offset = direction * delta;
        var bounds = new RectInt(horizontal && direction < 0 ? offset : 0,
            !horizontal && direction < 0 ? offset : 0, horizontal ? 10 + delta : 10, horizontal ? 10 : 10 + delta);
        edit.Resize("target", bounds);
        foreach (var before in new[] { attached, chain, detached })
        {
            var after = session.Document.rooms.Single(r => r.id == before.id);
            int move = before == detached ? 0 : offset;
            Check(after.x == before.x + (horizontal ? move : 0) && after.y == before.y + (horizontal ? 0 : move),
                "Only edge-connected rooms follow expansion and contraction; a gap ends the chain.");
            Check(MapJson.ToJson(after.foreground) == MapJson.ToJson(before.foreground)
                && MapJson.ToJson(after.objects) == MapJson.ToJson(before.objects), "Neighbor contents remain at their local coordinates.");
        }
        string afterLayout = Json(session.Document);
        session.Undo(); Check(Json(session.Document) == Json(doc) && !session.CanUndo, "Whole connected layout is one Undo.");
        session.Redo(); Check(Json(session.Document) == afterLayout, "Redo restores the same contact layout.");
    }
}
static void ResizeDetached()
{
    foreach (int hx in new[] { -1, 0, 1 }) foreach (int hy in new[] { -1, 0, 1 })
        foreach (int delta in new[] { -2, 4 })
    {
        if (hx == 0 && hy == 0) continue;
        var doc = Doc(Room("target"), Room("left", -15), Room("right", 15), Room("bottom", 0, -15),
            Room("top", 0, 15), Room("diagonal", 20, 20));
        foreach (var room in doc.rooms.Skip(1)) { room.locked = true; room.visible = false; }
        var session = new MapEditSession(doc); using var edit = new MapRoomEditing(session);
        edit.Resize("target", new RectInt(hx < 0 ? -delta : 0, hy < 0 ? -delta : 0,
            hx == 0 ? 10 : 10 + delta, hy == 0 ? 10 : 10 + delta));
        Check(MapJson.ToJson(session.Document.rooms.Skip(1).ToList()) == MapJson.ToJson(doc.rooms.Skip(1).ToList()),
            "All detached rooms remain byte-for-byte unchanged, even when locked and hidden.");
    }
    var cornerDoc = Doc(Room("target"), Room("corner", 10, 10));
    cornerDoc.rooms[1].locked = true;
    var cornerSession = new MapEditSession(cornerDoc); using var cornerEdit = new MapRoomEditing(cornerSession);
    cornerEdit.Resize("target", new RectInt(0, 0, 14, 10));
    Check(cornerSession.Document.rooms[1].x == 10 && cornerSession.Document.rooms[1].y == 10,
        "Touching only at a point must not transmit movement.");
}
static void ResizeObstacles()
{
    foreach (bool chain in new[] { false, true })
    {
        var doc = chain ? Doc(Room("target"), Room("attached", 10), Room("obstacle", 25))
            : Doc(Room("target"), Room("obstacle", 15));
        var session = new MapEditSession(doc); using var edit = new MapRoomEditing(session);
        Throws<InvalidOperationException>(() => edit.Resize("target", new RectInt(0, 0, 16, 10)));
        Check(Json(session.Document) == Json(doc) && !session.CanUndo && !session.IsEditing,
            "An overlap must reject the complete change without moving the disconnected obstacle.");
        edit.Resize("target", new RectInt(0, 0, 15, 10));
        Check(session.Document.rooms.Last().x == doc.rooms.Last().x, "An exact fit is allowed and does not push the obstacle.");
    }
    var corner = Doc(Room("target"), Room("corner", 10, 10));
    var cornerSession = new MapEditSession(corner); using var cornerEdit = new MapRoomEditing(cornerSession);
    Throws<InvalidOperationException>(() => cornerEdit.Resize("target", new RectInt(0, 0, 12, 12)));
    Check(Json(cornerSession.Document) == Json(corner) && !cornerSession.CanUndo, "Diagonal expansion cannot move a corner-only room.");
}
static void ShrinkToTerrain()
{
    foreach (int hx in new[] { -1, 0, 1 }) foreach (int hy in new[] { -1, 0, 1 })
    {
        if (hx == 0 && hy == 0) continue;
        var target = Room("target", 20, -30);
        target.foreground.Add(new MapCell { x = 2, y = 3, shape = TileShape.TopRight });
        target.background.Add(new MapCell { x = 6, y = 7 });
        target.objects.Add(new MapObject { id = "marker", x = 4, y = 5, nodes = new List<Vector2> { new(6, 6) } });
        var doc = Doc(target, Room("left", 5, -30), Room("right", 35, -30), Room("bottom", 20, -45), Room("top", 20, -15));
        var session = new MapEditSession(doc); using var edit = new MapRoomEditing(session);
        int left = hx < 0 ? 29 : 20, right = hx > 0 ? 21 : 30;
        int bottom = hy < 0 ? -21 : -30, top = hy > 0 ? -29 : -20;
        edit.Resize("target", new RectInt(left, bottom, right - left, top - bottom));
        var result = session.Document.rooms[0];
        int expectedLeft = hx < 0 ? 22 : 20, expectedRight = hx > 0 ? 27 : 30;
        int expectedBottom = hy < 0 ? -27 : -30, expectedTop = hy > 0 ? -22 : -20;
        Check(result.x == expectedLeft && result.y == expectedBottom
            && result.width == expectedRight - expectedLeft && result.height == expectedTop - expectedBottom,
            "Dragged edges must stop exactly on terrain; fixed edges must not move.");
        var foreground = result.foreground.Single(); var background = result.background.Single();
        Check(result.x + foreground.x == 22 && result.y + foreground.y == -27 && foreground.shape == TileShape.TopRight
            && result.x + background.x == 26 && result.y + background.y == -23,
            "Foreground, background and slope tiles must retain their world positions and shapes.");
        Check(result.x + result.objects[0].x == 24 && result.y + result.objects[0].y == -25
            && result.x + result.objects[0].nodes[0].x == 26 && result.y + result.objects[0].nodes[0].y == -24,
            "Object bodies and nodes must retain their world positions.");
        Check(session.Document.rooms[1].x == 5 && session.Document.rooms[2].x == 35
            && session.Document.rooms[3].y == -45 && session.Document.rooms[4].y == -15,
            "Terrain-clamped shrinking must leave detached neighbors in place.");
        string after = Json(session.Document);
        session.Undo(); Check(Json(session.Document) == Json(doc) && !session.CanUndo, "Clamped resize and all neighbors must undo together.");
        session.Redo(); Check(Json(session.Document) == after, "Redo must restore the exact clamped layout.");
    }
}
static void ShrinkOptions()
{
    var target = Room("target");
    target.foreground.Add(new MapCell { x = 2, y = 3 });
    target.background.Add(new MapCell { x = 6, y = 7 });
    var doc = Doc(target, Room("neighbor", 15));
    var session = new MapEditSession(doc); using var edit = new MapRoomEditing(session);
    edit.ResizeKeepingLocalContents("target", new RectInt(-10, -20, 1, 1));
    var result = session.Document.rooms[0];
    Check(result.x == -10 && result.y == -20 && result.width == 7 && result.height == 8
        && result.foreground.Single().x == 2 && result.background.Single().y == 7,
        "Inspector position and size must clamp dimensions while preserving local terrain.");
    Check(session.Document.rooms[1].x == 15, "Inspector terrain clamping must not move detached rooms.");
    session.Undo(); Check(Json(session.Document) == Json(doc), "Inspector clamp must undo atomically.");
    edit.Resize("target", new RectInt(0, 0, 1, 1), true);
    result = session.Document.rooms[0];
    Check(result.width == 1 && result.height == 1 && result.foreground.Count == 0 && result.background.Count == 0,
        "Explicit crop must still remove terrain instead of clamping.");
    session.Undo(); Check(Json(session.Document) == Json(doc), "Crop must remain undoable.");
    var empty = new MapEditSession(Doc(Room("empty"))); using var emptyEdit = new MapRoomEditing(empty);
    emptyEdit.Resize("empty", new RectInt(9, 9, 1, 1));
    Check(empty.Document.rooms[0].width == 1 && empty.Document.rooms[0].x == 9, "Empty rooms may shrink to one tile.");
    var full = Room("full"); full.foreground.Add(new MapCell { x = 9, y = 9 });
    var fullSession = new MapEditSession(Doc(full)); using var fullEdit = new MapRoomEditing(fullSession);
    fullEdit.Resize("full", new RectInt(0, 0, 1, 1));
    Check(Json(fullSession.Document) == Json(Doc(full)) && !fullSession.CanUndo,
        "A room already tight to its terrain must not change or create an undo entry.");
}
static void LockedResize()
{
    var doc = Doc(Room("target"), Room("right", 10)); doc.rooms[1].locked = true;
    var s = new MapEditSession(doc); using var edit = new MapRoomEditing(s);
    Throws<InvalidOperationException>(() => edit.Resize("target", new RectInt(0, 0, 14, 10)));
    Check(Json(s.Document) == Json(doc) && !s.CanUndo, "Locked propagation rollback");
}
static MapObjectDefinition PathDefinition()
{
    var def = new MapObjectDefinition(); def.id = "path"; def.displayName = "Path";
    def.layer = MapLayer.Entities; def.placement = MapPlacementKind.Nodes; def.minimumNodes = 1; def.maximumNodes = 8;
    return def;
}
static void Nodes()
{
    var d = Doc(Room("room")); var def = PathDefinition(); var obj = def.Create(new(1, 1), Vector2.one, MapLayer.Entities, "");
    obj.nodes = new List<Vector2> { new(2, 3), new(4, 5), new(6, 7) }; d.rooms[0].objects.Add(obj);
    var s = new MapEditSession(d); var c = new MapCanvasController(s) { Layer = MapLayer.Entities, ResolveObjectDefinition = _ => def };
    try
    {
        c.ObjectEditor.SelectNode(obj.id, 0); c.ObjectEditor.SelectNode(obj.id, 1, true);
        c.ObjectEditor.BeginMove(); c.ObjectEditor.DragMove(new Vector2(2, -1)); c.ObjectEditor.EndMove();
        var current = c.Room.objects[0]; Check(current.x == 1 && current.y == 1 && current.nodes[0] == new Vector2(4, 2) && current.nodes[1] == new Vector2(6, 4), "Move nodes without moving body.");
        s.Undo(); Check(c.Room.objects[0].nodes[0] == new Vector2(2, 3), "Nodes one undo");
        c.ObjectEditor.SelectNode(obj.id, 1); c.ObjectEditor.Delete();
        Check(c.Room.objects.Count == 1 && c.Room.objects[0].nodes.Count == 2, "Node-only delete must not remove body.");
    }
    finally { c.Dispose(); }
}
static void Clipboard()
{
    var d = Doc(Room("room")); d.rooms[0].foreground.Add(new MapCell { x = 1, y = 1, shape = TileShape.BottomLeft });
    var s = new MapEditSession(d); var c = new MapCanvasController(s);
    try
    {
        c.SelectArea(new RectInt(1, 1, 1, 1)); var copy = c.CaptureSelection(); c.PasteSelection(copy, new Vector2(5, 5));
        Check(c.Room.GetCell(MapLayer.ForegroundTiles, 5, 5)?.shape == TileShape.BottomLeft, "Paste cell geometry/material");
        c.SelectArea(new RectInt(5, 5, 1, 1)); c.FlipSelection(true);
        Check(c.Room.GetCell(MapLayer.ForegroundTiles, 5, 5)?.shape == TileShape.BottomRight && c.Room.GetCell(MapLayer.ForegroundTiles, 1, 1)?.shape == TileShape.BottomLeft, "Slope transforms without mutating source");
    }
    finally { c.Dispose(); }
}
static void ClipboardBulkAndLimits()
{
    var sourceRoom = Room("selection", width: 64, height: 64);
    for (int y = 0; y < sourceRoom.height; y++)
        for (int x = 0; x < sourceRoom.width; x++)
            sourceRoom.foreground.Add(new MapCell { x = x, y = y, material = "bulk", shape = TileShape.TopRight });
    sourceRoom.objects.Add(new MapObject { id = "source-object", definition = "marker", layer = MapLayer.Entities, x = 2, y = 3 });
    var selection = new MapSelectionClipboard(Doc(sourceRoom), MapLayer.All, false, new Vector2(64, 64));
    var session = new MapEditSession(LargeGestureDocument());
    var controller = new MapCanvasController(session) { Layer = MapLayer.All };
    try
    {
        long serializations = session.SnapshotSerializationCount;
        controller.PasteSelection(selection, new Vector2(100, 100));
        Check(controller.Room.foreground.Count == 24096 && controller.Room.objects.Count == 1
            && controller.Room.GetCell(MapLayer.ForegroundTiles, 163, 163)?.shape == TileShape.TopRight,
            "Bulk paste must preserve mixed source cells, shapes, and objects.");
        Check(session.SnapshotSerializationCount == serializations + 1,
            "Paste must publish one validated snapshot without serializing a pre-edit copy of the target map.");
        Check(session.HistoryByteCount < session.CurrentJson.Length,
            "Paste Undo must retain the changed members rather than another complete document snapshot.");
        session.Undo();
        Check(controller.Room.foreground.Count == 20000 && controller.Room.objects.Count == 0,
            "Bulk paste Undo must remove mixed appended members in exact order.");
        session.Redo();
        Check(controller.Room.foreground.Count == 24096 && controller.Room.objects.Count == 1,
            "Bulk paste Redo must restore every appended member.");

        var oneCellRoom = Room("one", width: 1, height: 1);
        oneCellRoom.foreground.Add(new MapCell { material = "cross-layer", shape = TileShape.BottomRight });
        var oneCell = new MapSelectionClipboard(Doc(oneCellRoom), MapLayer.ForegroundTiles, false, Vector2.one);
        controller.Layer = MapLayer.BackgroundTiles;
        controller.PasteSelection(oneCell, new Vector2(900, 900));
        Check(controller.Room.GetCell(MapLayer.BackgroundTiles, 900, 900)?.material == "cross-layer",
            "A tile clipboard must still remap to the selected target tile layer.");
    }
    finally { controller.Dispose(); }

    var tooManyRoom = Room("too-many", width: 257, height: 256);
    for (int index = 0; index <= MapCanvasController.MaximumPasteMemberCount; index++)
        tooManyRoom.foreground.Add(new MapCell { x = index % 257, y = index / 257 });
    var tooMany = new MapSelectionClipboard(Doc(tooManyRoom), MapLayer.ForegroundTiles, false, new Vector2(257, 256));
    var limitSession = new MapEditSession(MapDocument.CreateDefault());
    var limitController = new MapCanvasController(limitSession);
    try
    {
        Throws<InvalidOperationException>(() => limitController.PasteSelection(tooMany, Vector2.zero));
        Check(limitController.Room.foreground.Count == 0 && !limitSession.CanUndo && !limitSession.IsEditing,
            "The paste member cap must reject atomically before starting a transaction.");

        var expensiveRoom = Room("expensive", width: 1, height: 1);
        int objectCount = MapCanvasController.MaximumPasteWork / MapDocument.MaximumNodesPerObject + 1;
        for (int index = 0; index < objectCount; index++)
            expensiveRoom.objects.Add(new MapObject
            {
                id = "expensive-" + index,
                definition = "path",
                layer = MapLayer.Entities,
                nodes = Enumerable.Repeat(Vector2.zero, MapDocument.MaximumNodesPerObject).ToList()
            });
        var expensive = new MapSelectionClipboard(Doc(expensiveRoom), MapLayer.Entities, true, Vector2.one);
        limitController.Layer = MapLayer.Entities;
        Throws<InvalidOperationException>(() => limitController.PasteSelection(expensive, Vector2.zero));
        Check(limitController.Room.objects.Count == 0 && !limitSession.CanUndo && !limitSession.IsEditing,
            "The paste work cap must bound node-heavy selections before starting a transaction.");
    }
    finally { limitController.Dispose(); }
}
static void ClipboardObjectStress()
{
    var sourceRoom = Room("object-selection", width: 1, height: 1);
    for (int index = 0; index < MapDocument.MaximumObjectsPerRoom; index++)
        sourceRoom.objects.Add(new MapObject
        {
            id = "bulk-object-" + index,
            definition = "marker",
            layer = MapLayer.Entities
        });
    var selection = new MapSelectionClipboard(Doc(sourceRoom), MapLayer.Entities, true, Vector2.one);
    var session = new MapEditSession(MapDocument.CreateDefault());
    var controller = new MapCanvasController(session) { Layer = MapLayer.Entities };
    try
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        controller.PasteSelection(selection, Vector2.zero);
        string[] pastedOrder = controller.Room.objects.Select(item => item.id).ToArray();
        Check(pastedOrder.Length == MapDocument.MaximumObjectsPerRoom
            && controller.ObjectEditor.SelectedBodyIds.Count() == MapDocument.MaximumObjectsPerRoom,
            "A maximum-size object paste must restore every pasted body selection.");
        session.Undo();
        Check(controller.Room.objects.Count == 0, "Bulk object Undo must remove the contiguous insertion.");
        session.Redo();
        Check(controller.Room.objects.Select(item => item.id).SequenceEqual(pastedOrder),
            "Bulk object Redo must preserve exact object order and identity.");
        timer.Stop();
        Check(timer.Elapsed < TimeSpan.FromSeconds(20),
            "Maximum-size object paste/selection/Undo/Redo exceeded the linear-time responsiveness budget: " + timer.Elapsed);
    }
    finally { controller.Dispose(); }
}
static void ObjectEraseStress()
{
    var room = Room("object-erase-stress", width: 4, height: 2);
    for (int index = 0; index < MapDocument.MaximumObjectsPerRoom; index++)
        room.objects.Add(new MapObject
        {
            id = "erase-object-" + index,
            definition = "marker",
            layer = MapLayer.Entities,
            x = 1,
            y = 0,
            width = 1,
            height = 1
        });
    string[] originalOrder = room.objects.Select(item => item.id).ToArray();
    var session = new MapEditSession(Doc(room));
    var controller = new MapCanvasController(session) { Layer = MapLayer.Entities };
    try
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using (var eraser = new MapObjectErasing(controller))
        {
            Check(eraser.Begin(new Vector2(0.25f, 0.5f)), "The maximum-size erase gesture must begin inside its room.");
            eraser.DragPath(new[] { new Vector2(2.25f, 0.5f) });
            eraser.End();
        }
        Check(controller.Room.objects.Count == 0, "The batched erase must remove every crossed object.");
        session.Undo();
        Check(controller.Room.objects.Select(item => item.id).SequenceEqual(originalOrder),
            "Bulk object erase Undo must restore exact object order and identity.");
        session.Redo();
        Check(controller.Room.objects.Count == 0,
            "Bulk object erase Redo must remove the same objects again.");
        timer.Stop();
        Check(timer.Elapsed < TimeSpan.FromSeconds(20),
            "Maximum-size object erase/Undo/Redo exceeded the non-quadratic responsiveness budget: " + timer.Elapsed);
    }
    finally { controller.Dispose(); }
}
static void Masks()
{
    var normalized = Enumerable.Range(0, 256).Select(TileMask.Normalize).ToHashSet();
    Check(normalized.Count == 47 && TileMask.GetValidMasks().ToHashSet().SetEquals(normalized), "Canonical 47 masks");
    Check(TileMask.Normalize(TileMask.NE) == 0 && TileMask.Normalize(TileMask.N | TileMask.E | TileMask.NE) == 7, "Diagonal gating");
    Check(TileMask.Connects(TileShape.BottomLeft, TileShape.Solid, new Vector2Int(-1, 0)), "Filled left edge connects");
    Check(!TileMask.Connects(TileShape.BottomLeft, TileShape.Solid, new Vector2Int(1, 0)), "Empty right edge disconnects");
    foreach (TileShape shape in Enum.GetValues<TileShape>())
    {
        Vector2[] p = TileMask.GetPolygon(shape); float area = 0;
        for (int i = 0; i < p.Length; i++) area += p[i].x * p[(i + 1) % p.Length].y - p[(i + 1) % p.Length].x * p[i].y;
        Check(area == (shape == TileShape.Solid ? 2 : 1), "Counter-clockwise cell collider area");
    }
}
static void Geometry()
{
    Check(MapBrushGeometry.FloodFill(new(512, 512), new RectInt(0, 0, 1024, 1024), p => p == new Vector2Int(512, 512)).Count() == 1, "Largest room supports tiny reachable bucket.");
    Check(MapBrushGeometry.Rectangle(Vector2Int.zero, new(1023, 1023), true).Count() == 1048576, "Largest valid rectangle");
    Check(MapBrushGeometry.Line(new(-2, -1), new(2, 1)).First() == new Vector2Int(-2, -1) && MapBrushGeometry.Line(new(-2, -1), new(2, 1)).Last() == new Vector2Int(2, 1), "Negative-coordinate line endpoints");
    var continuousPath = new[] { new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(2, 1),
        new Vector2Int(2, 2), new Vector2Int(1, 1), new Vector2Int(0, 0) };
    var expandedPath = MapBrushGeometry.ExpandContinuousPath(continuousPath, 7).ToArray();
    Check(expandedPath.ToHashSet().SetEquals(MapBrushGeometry.Expand(continuousPath, 7)),
        "Perimeter-only continuous expansion must paint the same union as full square expansion across turns and revisits.");
    Check(expandedPath.Length <= 7 * 7 + (continuousPath.Length - 1) * (2 * 7 - 1),
        "Continuous expansion work must stay bounded by the brush perimeter.");
}
static void Values()
{
    Check(MathEx.RoundToInt(2.5f) == 2 && MathEx.RoundToInt(3.5f) == 4 && MathEx.RoundToInt(-2.5f) == -2, "Ties-to-even rounding");
    var a = new Vector2(1, 1); var b = new Vector2(1.000001f, 1);
    Check(a == b && !a.Equals(b), "Geometry comparison / exact dictionary key distinction");
    Check(new RectInt(0, 0, 4, 4).Contains(new Vector2Int(3, 3)) && !new RectInt(0, 0, 4, 4).Contains(new Vector2Int(4, 3)), "Half-open tile boundaries");
    Check(ColorText.TryParseHtmlString("#287FC480", out var c) && ColorText.ToHtmlStringRGBA(c) == "287FC480", "Color channel byte conversion");
    string vector = MapJson.ToJson(new Vector2(-2.25f, 3.5f));
    Check(vector == "{\"x\":-2.25,\"y\":3.5}" && MapJson.FromJson<Vector2>(vector).Equals(new Vector2(-2.25f, 3.5f)), "Vector fields exact JSON");
}
static void ComplexityLimits()
{
    var tooManyRooms = new MapDocument();
    tooManyRooms.rooms = Enumerable.Range(0, MapDocument.MaximumRoomCount + 1)
        .Select(index => new MapRoom
        {
            id = "room-limit-" + index,
            name = "room-limit-" + index,
            width = 1,
            height = 1
        }).ToList();
    Throws<InvalidOperationException>(() => tooManyRooms.Validate());

    var document = MapDocument.CreateDefault();
    document.rooms[0].objects.Add(new MapObject
    {
        id = "path", definition = "path", layer = MapLayer.Entities,
        nodes = Enumerable.Range(0, MapDocument.MaximumNodesPerObject + 1)
            .Select(index => new Vector2(index, 0)).ToList()
    });
    Throws<InvalidOperationException>(() => document.Validate());

    var rooms = Enumerable.Range(0, 2002).Select(index => new MapRoom
    {
        id = "room-" + index, name = "room-" + index, x = index * 2,
        width = 1, height = 1
    }).ToList();
    var error = Throws<InvalidOperationException>(() =>
        MapRoomResizeLayout.Plan(rooms, rooms[0].id, new RectInt(0, 0, 1, 1)));
    Check(error.Message.Contains("too complex"), "Quadratic resize work must stop at its explicit interactive budget.");

    var grouped = MapDocument.CreateDefault();
    string parent = "";
    for (int index = 0; index <= MapLayerGroups.MaximumGroupDepth; index++)
    {
        string id = "group-" + index;
        grouped.layerGroups.Add(new MapLayerGroup { id = id, name = id, parentId = parent });
        parent = id;
    }
    Throws<InvalidOperationException>(() => grouped.Validate());
}
static void MiniMapConnectionLimit()
{
    var document = new MapDocument();
    for (int index = 0; index < 512; index++)
    {
        document.rooms.Add(new MapRoom { id = "left-" + index, name = "left", x = -1, width = 1, height = 10 });
        document.rooms.Add(new MapRoom { id = "right-" + index, name = "right", x = 0, width = 1, height = 10 });
    }
    MiniMapConnection[] connections = MiniMapGeometry.GetConnections(document, 100);
    Check(connections.Length == 100 && connections.All(connection => connection.Vertical),
        "Pathological shared edges must stop at the requested output budget.");
    MiniMapConnection[] defaults = MiniMapGeometry.GetConnections(document,
        MiniMapGeometry.DefaultMaximumConnections, out bool truncated);
    Check(defaults.Length == MiniMapGeometry.DefaultMaximumConnections && truncated,
        "The 1024-room default path must cap output and report omitted connections.");
    MiniMapConnection[] zero = MiniMapGeometry.GetConnections(document, 0, out bool zeroTruncated);
    Check(zero.Length == 0 && zeroTruncated,
        "A zero-sized budget must still report that valid connections were omitted.");

    var exact = Doc(Room("left", -1, 0, 1, 10), Room("right", 0, 0, 1, 10));
    MiniMapConnection[] one = MiniMapGeometry.GetConnections(exact, 1, out bool exactTruncated);
    Check(one.Length == 1 && !exactTruncated,
        "Reaching a budget exactly must not claim the result was truncated.");
}
static void MiniMap()
{
    var d = Doc(Room("left", -10, -5, 10, 10), Room("right", 0, -2, 7, 4), Room("corner", 7, 2, 3, 3));
    var links = MiniMapGeometry.GetConnections(d);
    Check(links.Length == 1 && links[0].Vertical && links[0].Coordinate == 0 && links[0].Start == -2 && links[0].End == 2, "Shared edge excludes corner-only adjacency");
    var view = new MiniMapViewState(); Rect viewport = new(0, 0, 600, 400); view.Fit(d, viewport);
    var point = view.WorldToView(-5, 0, viewport);
    Check(MiniMapGeometry.HitTest(d, view, viewport, point) == "left", "Same minimap geometry and picking");
}
static void RoomExport() => InFiles(dir =>
{
    var d = Doc(Room("a"), Room("b", 20)); d.rooms[0].name = "동굴"; d.rooms[1].name = "동굴";
    d.properties.Add(new MapProperty { key = "global", value = "공유" }); string before = Json(d);
    var plan = MapRoomJsonExporter.Plan(d); Check(plan.Count == 2 && !string.Equals(plan[0].FileName, plan[1].FileName, StringComparison.OrdinalIgnoreCase), "Collision-free filenames");
    var paths = MapRoomJsonExporter.Export(d, Path.Combine(dir, "export"));
    foreach (string path in paths)
    {
        byte[] bytes = File.ReadAllBytes(path); Check(bytes[0] == (byte)'{', "UTF-8 without BOM");
        var loaded = MapDocumentStore.Load(path); Check(loaded.rooms.Count == 1 && loaded.properties.Single().value == "공유", "Independent room file preserves global metadata");
    }
    string first = File.ReadAllText(paths[0]); Throws<IOException>(() => MapRoomJsonExporter.Export(d, Path.GetDirectoryName(paths[0])));
    Check(File.ReadAllText(paths[0]) == first && Json(d) == before, "Collision preserves published/source data");
});
static void RoomExportAggregateLimit() => InFiles(dir =>
{
    var document = new MapDocument { name = "bounded export" };
    int sharedLength = checked((int)(MapRoomJsonExporter.MaximumAggregateUtf8Bytes
        / MapDocument.MaximumRoomCount + 1024));
    document.properties.Add(new MapProperty { key = "shared", value = new string('x', sharedLength) });
    for (int index = 0; index < MapDocument.MaximumRoomCount; index++)
        document.rooms.Add(Room("room-" + index, index));
    string before = Json(document);
    InvalidOperationException error = Throws<InvalidOperationException>(() => MapRoomJsonExporter.Plan(document));
    Check(error.Message.Contains("32", StringComparison.Ordinal), "Aggregate rejection must explain the bounded output size.");
    Check(MapRoomJsonExporter.Plan(document, new[] { "room-0" }).Count == 1,
        "Selected-room export must budget and serialize only selected rooms, even when all-room output is too large.");
    string output = Path.Combine(dir, "aggregate-export");
    Throws<InvalidOperationException>(() => MapRoomJsonExporter.Export(document, output));
    Check(!Directory.Exists(output) && Json(document) == before,
        "An aggregate overflow must be rejected before filesystem changes and preserve the source document.");
});
static void Resources()
{
    var set = MapResourceFactory.CreateTerrainMaterial("guid", "Terrain", "green", "#2E9B50");
    var tex = new TextureResource { AssetId = "atlas", width = 128, height = 112, SourcePath = "atlas.png" };
    var sprite = MapResourceFactory.CreateSprite("atlas", 21300002, "solid_0", tex, new Rect(0, 96, 16, 16), new Vector2(8, 8));
    set.SetSprite(0, sprite);
    Check(set.ThemeId == "green" && set.GetSprite(0, TileShape.Solid).RegionId == 21300002, "Stable resource identity and original tile lookup");
    Throws<ArgumentException>(() => MapResourceFactory.CreateSprite("atlas", 1, "invalid", tex, new Rect(127, 0, 16, 16), Vector2.zero));
}
static void Stylegrounds()
{
    var d = Doc(Room("room"));
    d.stylegrounds.Add(new MapStyleground { id = "group", type = "group", properties = new List<MapProperty> { new() { key = "alpha", value = "0.5" } } });
    d.stylegrounds.Add(new MapStyleground { id = "layer", type = "parallax", properties = new List<MapProperty> { new() { key = "parentId", value = "group" }, new() { key = "alpha", value = "0.4" } } });
    d.stylegrounds.Add(new MapStyleground { id = "external", type = "custom.vendor.effect", properties = new List<MapProperty> { new() { key = "future", value = "opaque" } } });
    var clone = MapDocumentStore.Deserialize(Json(d));
    Check(Math.Abs(MapStylegroundEditing.GetEffectiveSettings(clone, "layer").Alpha - 0.2f) < 0.00001f, "Ancestor alpha multiplication");
    Check(clone.stylegrounds[2].type == "custom.vendor.effect" && clone.stylegrounds[2].properties[0].value == "opaque", "Unknown runtime effect data retained");
}
static void Locale()
{
    MetroidvaniaStudioLocale.Column = "EN"; string en = MetroidvaniaStudioLocale.Text("roomJson.empty_map");
    MetroidvaniaStudioLocale.Column = "KR"; string kr = MetroidvaniaStudioLocale.Text("roomJson.empty_map");
    Check(en != kr && !en.StartsWith('[') && !kr.StartsWith('['), "Shared CSV available in both languages");
    foreach (string column in new[] { "JA", "ZH_CN", "ZH_TW", "RU" })
    {
        MetroidvaniaStudioLocale.Column = column;
        Check(MetroidvaniaStudioLocale.Text("roomJson.empty_map") != en, "Every supported language has exporter text.");
        Check(MetroidvaniaStudioLocale.Format("external.exportedRooms", 3).Contains('3'), "Translated placeholders format correctly.");
    }
    MetroidvaniaStudioLocale.Column = "KR";
}
static void LocaleEnums()
{
    var keys = new List<string>();
    foreach (Type type in new[] { typeof(MetroidvaniaStudioTool), typeof(MapLayer), typeof(TileShape) })
        keys.AddRange(Enum.GetNames(type).Select(name => "enum." + type.Name + "." + name));
    keys.AddRange(new[] { "green", "blue", "darkGray", "orange", "yellow" }.Select(name => "stageThemes." + name));
    string previous = MetroidvaniaStudioLocale.Column;
    try
    {
        foreach (string key in keys)
        {
            MetroidvaniaStudioLocale.Column = "EN";
            string en = MetroidvaniaStudioLocale.Text(key);
            MetroidvaniaStudioLocale.Column = "KR";
            string kr = MetroidvaniaStudioLocale.Text(key);
            Check(!en.StartsWith('[') && !kr.StartsWith('[') && !string.IsNullOrWhiteSpace(en)
                && kr.Any(character => character >= '\uAC00' && character <= '\uD7A3'),
                "Every web tool, layer, shape and supplied theme needs KR and EN locale entries: " + key);
        }
    }
    finally { MetroidvaniaStudioLocale.Column = previous; }
}
static void JsonFieldAccess()
{
    var value = JsonFieldProbe.Create();
    const string expected = "{\"x\":1,\"label\":\"한글\",\"shape\":0,\"renamed\":2,\"inherited\":3}";
    Check(MapJson.ToJson(value) == expected, "Public fields, numeric enums and explicit JSON attributes define the wire format.");
    var restored = MapJson.FromJson<JsonFieldProbe>(expected.Replace("\"한글\"", "\"변경\""));
    Check(restored.Label == "변경" && restored.inherited == 3 && restored.rawName == 2,
        "Public field accessors must deserialize through a private constructor.");
    Check(restored.transient == 99 && restored.unchanged == 8 && restored.ignored == 4,
        "Excluded fields retain constructor defaults.");
    Check(MapJson.ToJson(MapJson.FromJson<JsonFieldProbe>(MapJson.ToJson(value, true))) == expected,
        "Pretty and compact options must share field semantics.");
}
static void JsonTileAllocations()
{
    var document = Doc(Room("allocation", width: 128, height: 64));
    for (int i = 0; i < 4096; i++) document.rooms[0].foreground.Add(new MapCell { x = i % 128, y = i / 128, material = "terrain" });
    for (int warmup = 0; warmup < 3; warmup++) MapJson.ToJson(document);
    long before = GC.GetAllocatedBytesForCurrentThread();
    string json = MapJson.ToJson(document);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Check(allocated < json.Length * 2L + 32768,
        $"Tile serialization must allocate primarily its result string, not boxed coordinates for every field ({allocated} bytes for {json.Length} characters).");
}

static void RoomCreation()
{
    static bool Overlaps(MapRoom a, MapRoom b) => (long)a.x < (long)b.x + b.width && (long)a.x + a.width > b.x
        && (long)a.y < (long)b.y + b.height && (long)a.y + a.height > b.y;
    var a = Room("A"); var b = Room("B", 18); b.visible = false; b.locked = true;
    var session = new MapEditSession(Doc(a, b)); using var edit = new MapRoomEditing(session);
    string before = Json(session.Document);
    foreach (var bounds in new[] { new RectInt(-30, -20, 16, 10), new RectInt(10, 1, 8, 6),
        new RectInt(int.MinValue, 0, 1, 1), new RectInt(int.MaxValue - 1, 0, 1, 1) })
    {
        string id = edit.Create(bounds, "Free room"); var room = session.Document.rooms.Single(r => r.id == id);
        Check(room.x == bounds.x && room.y == bounds.y && room.width == bounds.width && room.height == bounds.height,
            "Free coordinates, negative positions, exact fits and valid world limits must remain unchanged.");
        session.Undo(); Check(Json(session.Document) == before, "Creation is one Undo.");
    }
    foreach (var bounds in new[] { new RectInt(9, 1, 8, 6), new RectInt(19, 2, 5, 5), new RectInt(-3, -2, 40, 20) })
    {
        string id = edit.Create(bounds, "New room"); var room = session.Document.rooms.Single(r => r.id == id);
        Check(session.Document.rooms.Where(r => r.id != id).All(r => !Overlaps(r, room)),
            "Creation must avoid every existing room, including hidden and locked rooms and whole clusters.");
        Check(Json(Doc(session.Document.rooms.Take(2).ToArray())) == before, "Existing rooms must not move or change.");
        if (bounds.width == 8) Check(room.x == 10 && room.y == 1, "The nearest eight-tile gap is an exact fit.");
        string after = Json(session.Document);
        session.Undo(); Check(Json(session.Document) == before, "One Undo removes both creation and placement adjustment.");
        session.Redo(); Check(Json(session.Document) == after, "Redo restores the same identity and adjusted bounds."); session.Undo();
    }
    foreach (var bounds in new[] { new RectInt(0, 0, 0, 10), new RectInt(0, 0, 1025, 1), new RectInt(int.MaxValue, 0, 2, 2) })
    {
        Throws<ArgumentOutOfRangeException>(() => edit.Create(bounds));
        Check(Json(session.Document) == before && !session.CanUndo, "Invalid creation must be rejected atomically.");
    }
}

static void RoomGroupCollision()
{
    foreach (var direction in new[] { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) })
    {
        var a = Room("A", 0, 0, 8, 8);
        var b = Room("B", 12 * direction.x, 12 * direction.y, 8, 8);
        var c = Room("C", 24 * direction.x, 24 * direction.y, 8, 8);
        var selected = new[] { a, b }; var all = new[] { a, b, c };
        Check(MapRoomCollision.Resolve(selected, new Vector2Int(3 * direction.x, 3 * direction.y), all) == new Vector2Int(3 * direction.x, 3 * direction.y), "A free group destination keeps its gap.");
        Check(MapRoomCollision.Resolve(selected, new Vector2Int(6 * direction.x, 6 * direction.y), all) == new Vector2Int(4 * direction.x, 4 * direction.y), "A group collision resolves as one rigid movement in either axis.");
    }
    var left = Room("left", 0, 0, 4, 4); var right = Room("right", 10, 0, 4, 4); var gap = Room("gap", 5, 1, 4, 4);
    Check(MapRoomCollision.Resolve(new[] { left, right }, new Vector2Int(0, 1), new[] { left, right, gap }) == new Vector2Int(0, 1), "An obstacle in the empty space between rooms does not collide with an artificial bounding box.");
    var end = Room("end", int.MaxValue - 4, 0, 4, 4);
    Throws<ArgumentOutOfRangeException>(() => MapRoomCollision.Resolve(new[] { left, end }, new Vector2Int(1, 0), new[] { left, end }));
}

static void RoomWorkflow()
{
    var a = Room("A", 0, 0, 8, 6); var b = Room("B", 12, 0, 5, 6);
    a.foreground.Add(new MapCell { x = 1, y = 2, shape = TileShape.TopRight, material = "terrain" });
    a.background.Add(new MapCell { x = 5, y = 4, shape = TileShape.BottomLeft });
    a.objects.Add(new MapObject { id = "object-a", x = 2, y = 3, width = 2, height = 1, nodes = new() { new Vector2(1, 2), new Vector2(4, 5) } });
    var session = new MapEditSession(Doc(a, b)); using var edit = new MapRoomEditing(session);
    string before = Json(session.Document);
    edit.CopySelected(); edit.Paste(new Vector2Int(-20, 10));
    var copy = session.Document.rooms.Last();
    Check(copy.id != "A" && copy.objects[0].id != "object-a" && copy.x == -20 && copy.y == 10, "Paste has fresh identities and a world anchor.");
    Check(copy.foreground[0].x == 1 && copy.objects[0].nodes[1].y == 5, "Every layer and node must be copied.");
    session.Undo(); Check(Json(session.Document) == before, "Whole-room paste is one undo.");
    session.Redo(); Check(session.Document.rooms.Count == 3, "Paste redo."); session.Undo(); edit.Select("A");
    edit.FlipSelected(true); Check(session.Document.rooms[0].foreground[0].x == 6 && session.Document.rooms[0].objects[0].nodes[0].x == 7, "Horizontal tile/node reflection.");
    edit.FlipSelected(true); Check(Json(session.Document) == before, "Two horizontal flips restore all data.");
    edit.FlipSelected(false); edit.FlipSelected(false); Check(Json(session.Document) == before, "Two vertical flips restore all data.");
    for (int i = 0; i < 4; i++) edit.RotateSelected(true);
    var rotated = session.Document.rooms[0]; rotated.objects[0].rotation += 360;
    Check(Json(session.Document) == before, "Four quarter turns restore layout, slope cells, objects and nodes.");
    edit.CutSelected(); Check(session.Document.rooms.Count == 1, "Cut removes the selected room."); session.Undo();
    Check(Json(session.Document) == before, "Cut undo restores the room.");
    edit.Select("A"); session.Document.rooms[0].locked = true; string locked = Json(session.Document);
    Throws<InvalidOperationException>(() => edit.RotateSelected(true)); Check(Json(session.Document) == locked, "Locked transforms are atomic.");
    Check(MapRoomCollision.Resolve(a, new Vector2Int(3, 0), new[] { a, b }) == new Vector2Int(3, 0), "A one-tile gap remains a one-tile gap.");
    Check(MapRoomCollision.Resolve(a, new Vector2Int(5, 0), new[] { a, b }) == new Vector2Int(4, 0), "Overlap resolves to a flush edge.");
    var c = Room("C", 16, 0, 5, 6);
    var delta = MapRoomCollision.Resolve(a, new Vector2Int(14, 0), new[] { a, b, c });
    Check(delta.y != 0 || delta.x >= 21 || delta.x <= 4, "Resolve the entire collision cluster, not just the first neighbor.");
}


abstract class JsonFieldProbeBase { public int inherited = 3; }
sealed class JsonFieldProbe : JsonFieldProbeBase
{
    private JsonFieldProbe() { }
    public static JsonFieldProbe Create() => new();
    public int x = 1;
    public string label = "한글";
    public TileShape shape = TileShape.Solid;
    [System.Text.Json.Serialization.JsonPropertyName("renamed")] public int rawName = 2;
    [System.Text.Json.Serialization.JsonIgnore] public int ignored = 4;
    [NonSerialized] public int transient = 99;
    public readonly int unchanged = 8;
    public string Label => label;
    public int Computed => throw new InvalidOperationException("Map JSON must not evaluate CLR properties.");
}
