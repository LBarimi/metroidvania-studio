using System.Diagnostics;
using System.Text.Json;
using MetroidvaniaStudio;
using MetroidvaniaStudio.Automation;
using MetroidvaniaStudio.Primitives;

if (args.Length == 1 && args[0] == "--print-schema")
{
    Console.WriteLine(JsonSerializer.Serialize(AutomationEngine.Describe().GetProperty("requestSchema"), new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

int passed = 0, failed = 0;
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
}
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Reject(Action action, string text = "")
{
    try { action(); } catch (Exception error) when (error is ArgumentException or InvalidDataException)
    { Check(error.Message.Contains(text, StringComparison.OrdinalIgnoreCase), error.Message); return; }
    throw new Exception("Expected a validation error.");
}
string Doc(int width = 16, int height = 12) => MapDocumentStore.Serialize(new MapDocument
{ rooms = new List<MapRoom> { new() { id = "a", name = "room-a", width = width, height = height } } });
JsonElement Request(params object[] operations) => JsonSerializer.SerializeToElement(new { apiVersion = 1, operations });
AutomationResult Apply(string doc, params object[] operations) => AutomationEngine.Apply(doc, Request(operations));
MapDocument Read(AutomationResult result) => MapDocumentStore.Deserialize(result.DocumentJson);
MapRoom First(AutomationResult result) => Read(result).rooms[0];

Test("batch creates, paints, and reports explicit identities", () =>
{
    var result = Apply(Doc(), new { op = "room.add", id = "b", name = "room-b", x = 16, y = 0, width = 8, height = 12 },
        new { op = "tiles.rectangle", roomId = "b", layer = "foreground", x = 0, y = 0, width = 8, height = 2, materialId = "green" });
    Check(result.Changed && result.OperationCount == 2 && result.CreatedIds.SequenceEqual(new[] { "b" }));
    Check(Read(result).rooms[1].foreground.Count == 16);
});
Test("no-op batch and repaint report no change", () =>
{
    Check(!Apply(Doc()).Changed);
    var painted = Apply(Doc(), new { op = "tiles.paint", roomId = "a", layer = "foreground", cells = new[] { new { x = 2, y = 3 } } });
    Check(!Apply(painted.DocumentJson, new { op = "tiles.paint", roomId = "a", layer = "foreground", cells = new[] { new { x = 2, y = 3 } } }).Changed);
});
Test("separate layer indexes and last write wins", () =>
{
    var result = Apply(Doc(),
        new { op = "tiles.paint", roomId = "a", layer = "foreground", materialId = "blue", cells = new[] { new { x = 1, y = 1 } } },
        new { op = "tiles.paint", roomId = "a", layer = "foreground", materialId = "orange", cells = new[] { new { x = 1, y = 1 } } },
        new { op = "tiles.paint", roomId = "a", layer = "background", cells = new[] { new { x = 1, y = 1 } } });
    Check(First(result).foreground.Single().material == "orange" && First(result).background.Count == 1);
});
Test("cell shapes and erase preserve other cells", () =>
{
    var result = Apply(Doc(), new { op = "tiles.paint", roomId = "a", layer = "foreground", cells = new[] { new { x = 1, y = 1, shape = "bottomLeft" }, new { x = 2, y = 1, shape = "topRight" } } },
        new { op = "tiles.erase", roomId = "a", layer = "foreground", cells = new[] { new { x = 1, y = 1 } } });
    Check(First(result).foreground.Single().shape == TileShape.TopRight);
});
Test("flood fill respects material and group boundaries", () =>
{
    var result = Apply(Doc(6, 4), new { op = "tiles.rectangle", roomId = "a", layer = "foreground", x = 2, y = 0, width = 1, height = 4, materialId = "wall" },
        new { op = "tiles.fill", roomId = "a", layer = "foreground", x = 0, y = 0, materialId = "green" });
    Check(First(result).foreground.Count == 12 && First(result).foreground.Count(cell => cell.material == "green") == 8);
    var erased = Apply(result.DocumentJson, new { op = "tiles.fill", roomId = "a", layer = "foreground", x = 0, y = 0, erase = true });
    Check(First(erased).foreground.Count == 4);
});
Test("unknown operations and unsupported API fail clearly", () =>
{
    Reject(() => Apply(Doc(), new { op = "tiles.unknown" }), "Unknown operation");
    Reject(() => AutomationEngine.Apply(Doc(), JsonSerializer.SerializeToElement(new { apiVersion = 2, operations = Array.Empty<object>() })), "version");
});
Test("typos, duplicate fields and malformed cells are rejected", () =>
{
    Reject(() => Apply(Doc(), new { op = "room.update", roomId = "a", naem = "wrong" }), "Unknown");
    using var duplicate = JsonDocument.Parse("{\"apiVersion\":1,\"apiVersion\":1,\"operations\":[]}");
    Reject(() => AutomationEngine.Apply(Doc(), duplicate.RootElement), "duplicate");
    Reject(() => Apply(Doc(), new { op = "tiles.paint", roomId = "a", layer = "foreground", cells = "bad" }), "array");
});
Test("failure after a valid operation never alters the input", () =>
{
    string original = Doc();
    Reject(() => Apply(original, new { op = "document.update", name = "changed" },
        new { op = "tiles.paint", roomId = "a", layer = "foreground", cells = new[] { new { x = -1, y = 0 } } }), "Operation 1");
    Check(MapDocumentStore.Deserialize(original).name == "Untitled");
    Check(!Apply(original).Changed);
});
Test("tile bounds and coordinate types are strict", () =>
{
    Reject(() => Apply(Doc(), new { op = "tiles.rectangle", roomId = "a", layer = "foreground", x = 15, y = 0, width = 2, height = 1 }), "fit");
    Reject(() => Apply(Doc(), new { op = "room.move", roomId = "a", x = 0.5, y = 0 }), "integer");
    Reject(() => Apply(Doc(), new { op = "tiles.paint", roomId = "a", layer = "entities", cells = new[] { new { x = 0, y = 0 } } }), "layer");
});
Test("creation and movement resolve overlap without moving neighbors", () =>
{
    var created = Apply(Doc(), new { op = "room.add", id = "b", x = 8, y = 0, width = 8, height = 12 });
    var map = Read(created);
    Check(map.rooms[0].x == 0 && map.rooms[1].x == 16);
    var moved = Apply(created.DocumentJson, new { op = "room.move", roomId = "b", x = 3, y = 0 });
    var room = Read(moved).rooms[1];
    Check(room.x + room.width <= 0 || room.x >= 16 || room.y + room.height <= 0 || room.y >= 12);
});
Test("resize moves attached rooms but keeps gaps fixed", () =>
{
    var map = MapDocumentStore.Deserialize(Doc());
    map.rooms.Add(new MapRoom { id = "b", x = 16, y = 0, width = 8, height = 12 });
    map.rooms.Add(new MapRoom { id = "c", x = 30, y = 0, width = 8, height = 12 });
    var result = Apply(MapDocumentStore.Serialize(map), new { op = "room.resize", roomId = "a", width = 18, height = 12 });
    Check(Read(result).rooms[1].x == 18 && Read(result).rooms[2].x == 30);
});
Test("detached overlap rejects an entire resize", () =>
{
    var map = MapDocumentStore.Deserialize(Doc());
    map.rooms.Add(new MapRoom { id = "b", x = 18, y = 0, width = 8, height = 12 });
    Reject(() => Apply(MapDocumentStore.Serialize(map), new { op = "room.resize", roomId = "a", width = 20, height = 12 }), "overlap");
});
Test("terrain-clamped resizing and explicit crop reuse core semantics", () =>
{
    var painted = Apply(Doc(), new { op = "tiles.paint", roomId = "a", layer = "foreground", cells = new[] { new { x = 10, y = 5 } } });
    Check(First(Apply(painted.DocumentJson, new { op = "room.resize", roomId = "a", width = 4, height = 4 })).width == 11);
    Check(First(Apply(painted.DocumentJson, new { op = "room.resize", roomId = "a", width = 4, height = 4, crop = true })).foreground.Count == 0);
});
Test("rotation transforms slopes and preserves custom properties", () =>
{
    var result = Apply(Doc(6, 4), new { op = "tiles.paint", roomId = "a", layer = "foreground", cells = new[] { new { x = 1, y = 2, shape = "bottomLeft" } } },
        new { op = "properties.set", roomId = "a", values = new Dictionary<string, string> { ["custom.future"] = "{\"value\":1}" } },
        new { op = "room.rotate", roomId = "a", clockwise = true });
    MapRoom room = First(result);
    Check(room.width == 4 && room.height == 6 && room.foreground.Single().x == 2 && room.foreground.Single().y == 4);
    Check(room.foreground.Single().shape == MapBrushGeometry.RotateShape(TileShape.BottomLeft, true));
    Check(room.properties.Single().value == "{\"value\":1}");
});
Test("four rotations restore tiles, objects and nodes", () =>
{
    var initial = Apply(Doc(8, 6), new { op = "tiles.paint", roomId = "a", layer = "foreground", cells = new[] { new { x = 1, y = 2, shape = "topRight" } } },
        new { op = "object.add", roomId = "a", id = "obj", definitionId = "marker", layer = "entities", x = 2, y = 2, nodes = new[] { new { x = 3, y = 3 } } });
    var result = Apply(initial.DocumentJson, Enumerable.Range(0, 4).Select(_ => (object)new { op = "room.rotate", roomId = "a" }).ToArray());
    var before = First(initial); var after = First(result);
    Check(after.width == before.width && after.height == before.height && after.foreground.Single().x == 1 && after.foreground.Single().y == 2);
    Check(after.objects.Single().x == 2 && after.objects.Single().nodes.Single().x == 3);
});
Test("duplicate creates deterministic object IDs for an explicit room ID", () =>
{
    var initial = Apply(Doc(), new { op = "object.add", roomId = "a", id = "obj", definitionId = "marker", layer = "entities", x = 1, y = 1 });
    object op = new { op = "room.duplicate", roomId = "a", id = "copy", x = 40, y = 0 };
    var one = Apply(initial.DocumentJson, op); var two = Apply(initial.DocumentJson, op);
    Check(one.DocumentJson == two.DocumentJson && one.CreatedIds.Length == 2 && Read(one).rooms[1].objects.Single().id != "obj");
});
Test("locked rooms reject all mutations", () =>
{
    var map = MapDocumentStore.Deserialize(Doc()); map.rooms[0].locked = true;
    string json = MapDocumentStore.Serialize(map);
    Reject(() => Apply(json, new { op = "room.update", roomId = "a", name = "changed" }), "locked");
    Reject(() => Apply(json, new { op = "room.delete", roomId = "a" }), "locked");
    Reject(() => Apply(json, new { op = "tiles.paint", roomId = "a", layer = "foreground", cells = new[] { new { x = 0, y = 0 } } }), "locked");
});
Test("locked ancestor groups cannot be painted, erased, or cropped", () =>
{
    var map = MapDocumentStore.Deserialize(Doc());
    map.layerGroups.Add(new MapLayerGroup { id = "parent", locked = true });
    map.layerGroups.Add(new MapLayerGroup { id = "child", parentId = "parent" });
    map.rooms[0].foreground.Add(new MapCell { x = 0, y = 0, groupId = "child" });
    string json = MapDocumentStore.Serialize(map);
    Reject(() => Apply(json, new { op = "tiles.erase", roomId = "a", layer = "foreground", cells = new[] { new { x = 0, y = 0 } } }), "locked");
    Reject(() => Apply(json, new { op = "tiles.paint", roomId = "a", layer = "foreground", groupId = "child", cells = new[] { new { x = 1, y = 1 } } }), "locked");
    Reject(() => Apply(json, new { op = "room.resize", roomId = "a", width = 4, height = 4, crop = true }), "locked");
});
Test("group references cannot cross tile layers", () =>
{
    var map = MapDocumentStore.Deserialize(Doc()); map.layerGroups.Add(new MapLayerGroup { id = "group", layer = MapLayer.BackgroundTiles });
    Reject(() => Apply(MapDocumentStore.Serialize(map), new { op = "tiles.paint", roomId = "a", layer = "foreground", groupId = "group", cells = new[] { new { x = 0, y = 0 } } }), "group");
});
Test("object IDs, transforms, nodes and custom data round-trip", () =>
{
    var result = Apply(Doc(), new { op = "object.add", roomId = "a", id = "trigger", definitionId = "zone", layer = "triggers", x = 2, y = 2, width = 4, height = 3,
            nodes = new[] { new { x = 4, y = 5 } }, properties = new Dictionary<string, string> { ["event"] = "door" } },
        new { op = "object.update", roomId = "a", objectId = "trigger", x = 3, properties = new Dictionary<string, string> { ["mode"] = "open" } });
    MapObject item = First(result).objects.Single();
    Check(item.x == 3 && item.width == 4 && item.properties.Count == 2 && item.nodes.Single().y == 5);
    Check(First(Apply(result.DocumentJson, new { op = "object.delete", roomId = "a", objectId = "trigger" })).objects.Count == 0);
});
Test("object geometry and room identity are validated", () =>
{
    Reject(() => Apply(Doc(), new { op = "object.add", roomId = "a", definitionId = "marker", layer = "entities", x = 15, y = 0, width = 2 }), "fit");
    Reject(() => Apply(Doc(), new { op = "object.add", roomId = "a", definitionId = "marker", layer = "entities", x = 1, y = 1, scaleX = 0 }), "nonzero");
    Reject(() => Apply(Doc(), new { op = "object.delete", roomId = "a", objectId = "missing" }), "Unknown object");
    Reject(() => Apply(Doc(), new { op = "room.add", id = "a", x = 40, y = 0, width = 4, height = 4 }), "already in use");
});
Test("properties merge and null removal preserve unrelated data", () =>
{
    var result = Apply(Doc(), new { op = "properties.set", values = new Dictionary<string, string?> { ["alpha"] = "one", ["beta"] = "two" } },
        new { op = "properties.set", values = new Dictionary<string, string?> { ["alpha"] = null } });
    Check(Read(result).properties.Single().key == "beta");
});
Test("document and camera settings are queryable", () =>
{
    var result = Apply(Doc(), new { op = "document.update", name = "world" }, new { op = "camera.set", ppu = 32, width = 640, height = 360 });
    var query = AutomationEngine.Inspect(result.DocumentJson);
    Check(query.GetProperty("name").GetString() == "world" && query.GetProperty("camera").GetProperty("ppu").GetInt32() == 32);
    Reject(() => Apply(Doc(), new { op = "camera.set", ppu = 0, width = 320, height = 180 }), "between");
});
Test("minimap inspection returns connected boundaries and identities", () =>
{
    var result = Apply(Doc(), new { op = "room.add", id = "b", x = 16, y = 0, width = 8, height = 12 });
    var query = AutomationEngine.Inspect(result.DocumentJson);
    Check(query.GetProperty("rooms")[1].GetProperty("roomId").GetString() == "b");
    Check(query.GetProperty("minimap").GetProperty("bounds").GetProperty("width").GetDouble() == 24);
    Check(query.GetProperty("minimap").GetProperty("connections").GetArrayLength() > 0);
});
Test("unknown map fields are rejected instead of silently discarded", () =>
{
    using var json = JsonDocument.Parse(Doc());
    string extended = Doc()[..^1] + ",\"customExtension\":42}";
    Reject(() => Apply(extended), "Unknown field");
});
Test("schema exposes every operation and bounded atomic request", () =>
{
    var description = AutomationEngine.Describe();
    var operations = description.GetProperty("operations");
    Check(operations.GetArrayLength() == 17 && description.GetProperty("requestSchema").GetProperty("additionalProperties").ValueKind == JsonValueKind.False);
    foreach (JsonElement operation in operations.EnumerateArray())
        Check(operation.GetProperty("schema").GetProperty("properties").GetProperty("op").GetProperty("const").GetString() == operation.GetProperty("name").GetString());
});
Test("operations and visited cell budgets reject oversized batches", () =>
{
    Reject(() => Apply(Doc(), Enumerable.Range(0, 1025).Select(_ => (object)new { op = "room.update", roomId = "a" }).ToArray()), "1024");
    Reject(() => Apply(Doc(512, 512), Enumerable.Range(0, 5).Select(_ => (object)new { op = "tiles.rectangle", roomId = "a", layer = "foreground", x = 0, y = 0, width = 512, height = 512 }).ToArray()), "budget");
});
Test("cancellation prevents a result", () =>
{
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { AutomationEngine.Apply(Doc(), Request(), canceled.Token); throw new Exception("Cancellation was ignored."); }
    catch (OperationCanceledException) { }
});
Test("many tile operations use one linear index and retain exact cells", () =>
{
    object[] operations = Enumerable.Range(0, 256).Select(row => (object)new
    { op = "tiles.rectangle", roomId = "a", layer = "foreground", x = 0, y = row, width = 256, height = 1, materialId = "green" }).ToArray();
    var timer = Stopwatch.StartNew();
    var result = Apply(Doc(256, 256), operations);
    timer.Stop();
    Check(First(result).foreground.Count == 65536);
    Console.WriteLine($"  65,536 tiles / 256 operations: {timer.ElapsedMilliseconds} ms");
    Check(timer.Elapsed < TimeSpan.FromSeconds(15), "Dense tile batching unexpectedly slow.");
});
Test("large repeated transforms hit bounded snapshot work", () =>
{
    var dense = Apply(Doc(128, 128), new { op = "tiles.rectangle", roomId = "a", layer = "foreground", x = 0, y = 0, width = 128, height = 128 });
    Reject(() => Apply(dense.DocumentJson, Enumerable.Range(0, 140).Select(_ => (object)new { op = "room.rotate", roomId = "a" }).ToArray()), "work limit");
});

Console.WriteLine($"Automation tests: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
