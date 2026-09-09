using MetroidvaniaStudio;
using MetroidvaniaStudio.Primitives;

internal static class ObjectBrushTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Paint()
    {
        foreach (string id in new[] { "Spawn", "Respawn", "Portal", "InvisibleWall" })
        {
            var room = new MapRoom { id = "room", name = "room", width = 16, height = 10 };
            var session = new MapEditSession(new MapDocument { rooms = new() { room } });
            room = session.Document.rooms[0];
            var canvas = new MapCanvasController(session);
            canvas.SelectRoom(room.id); canvas.Layer = MapLayer.Entities;
            var definition = new MapObjectDefinition { id = id, layer = MapLayer.Entities };
            string before = session.CurrentJson;
            var ids = canvas.ObjectEditor.PaintCells(definition, new[] { new Vector2(2.8f, 2.1f), new Vector2(6.2f, 2.9f), new Vector2(6.7f, 5.8f), new Vector2(2.8f, 5.2f) });
            Check(ids.Count == 12 && ids.Distinct().Count() == 12, id + " follows the bent path and owns per-cell IDs.");
            Check(room.objects.All(o => o.width == 1 && o.height == 1 && o.x == MathF.Floor(o.x) && o.y == MathF.Floor(o.y)), "Whole tile positions and dimensions.");
            Check(!room.objects.Any(o => o.x == 3 && o.y == 3), "No rectangle fill across the bend.");
            string after = session.CurrentJson;
            var secondDefinition = new MapObjectDefinition { id = id == "Spawn" ? "Portal" : "Spawn", layer = MapLayer.Entities };
            Check(canvas.ObjectEditor.Place(secondDefinition, new Vector2(2.2f, 2.3f), Vector2.one) == null, "Other brush types cannot overlap.");
            Check(session.CurrentJson == after, "An occupied click is a no-op.");
            session.Undo(); Check(session.CurrentJson == before && !session.CanUndo, "One Undo removes the whole stroke.");
            session.Redo(); Check(session.CurrentJson == after, "Redo restores IDs and cell order exactly.");
            var restored = MapDocumentStore.Deserialize(after); Check(restored.rooms[0].objects.Select(o => o.id).SequenceEqual(ids), "IDs round trip.");
            session.Undo();
            canvas.ObjectEditor.PaintCells(definition, new[] { new Vector2(14.2f, 1.8f), new Vector2(20.9f, 1.1f), new Vector2(14.2f, 1.8f) });
            Check(session.Document.rooms[0].objects.Count == 2, "Leaving and retracing the room creates no duplicates or outside cells.");
            session.Undo();
            before = session.CurrentJson;
            try { canvas.ObjectEditor.PaintCells(definition, new[] { Vector2.zero, new Vector2(20000, 0) }); throw new Exception("Expected work limit."); }
            catch (ArgumentException) { }
            Check(session.CurrentJson == before && !session.CanUndo, "Oversized paths are rejected atomically.");
        }
        var legacy = new MapRoom { id = "legacy", name = "legacy", width = 16, height = 10 };
        legacy.objects.Add(new MapObject { id = "region", definition = "Portal", layer = MapLayer.Entities, x = 2, y = 2, width = 3, height = 3, groupId = "locked" });
        var document = new MapDocument { rooms = new() { legacy }, layerGroups = new() { new MapLayerGroup { id = "locked", name = "locked", layer = MapLayer.Entities, locked = true, visible = false } } };
        var legacySession = new MapEditSession(document);
        legacy = legacySession.Document.rooms[0];
        var editor = new MapCanvasController(legacySession); editor.SelectRoom(legacy.id); editor.Layer = MapLayer.Entities;
        var wall = new MapObjectDefinition { id = "InvisibleWall", layer = MapLayer.Entities };
        editor.ObjectEditor.PaintCells(wall, new[] { new Vector2(1.5f, 3.5f), new Vector2(5.5f, 3.5f) });
        Check(legacy.objects.Count == 3 && legacy.objects[1].x == 1 && legacy.objects[2].x == 5, "Hidden, locked legacy regions block interior cells while allowing adjacent cells.");
        legacySession.Undo(); Check(legacySession.Document.rooms[0].objects.Count == 1, "Legacy region survives Undo unchanged.");
    }
}
