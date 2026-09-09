using MetroidvaniaStudio;
using MetroidvaniaStudio.Primitives;

internal static class RoomResolutionFitTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Fit()
    {
        MapCell Cell(int x, int y) => new() { x = x, y = y, material = "stone" };
        MapObject Object(string id, float x, float y, float width = 1, float height = 1, float rotation = 0) =>
            new() { id = id, definition = "Portal", x = x, y = y, width = width, height = height, rotation = rotation, layer = MapLayer.Entities };
        var room = new MapRoom { id = "fit", name = "fit", x = -10, y = 4, width = 32, height = 20,
            foreground = new() { Cell(0, 0), Cell(19, 11), Cell(20, 5), Cell(2, 12) },
            background = new() { Cell(19, 0), Cell(21, 13) },
            objects = new() { Object("inside", 5, 5), Object("edge", 19, 11), Object("rotated-inside", 17, 2, 4, 1, 90),
                Object("node-edge", 2, 2), Object("partial", 19, 6, 2), Object("rotated-outside", 19, 2, 1, 1, 45),
                Object("node-outside", 2, 2), Object("negative", -.5f, 2) } };
        room.objects[3].nodes.Add(new Vector2(20, 12));
        room.objects[6].nodes.Add(new Vector2(21, 2));
        room.objects[4].layer = MapLayer.BackgroundDecals;
        room.objects[5].layer = MapLayer.Triggers;
        var right = new MapRoom { id = "right", name = "right", x = 22, y = 4, width = 8, height = 10 };
        var top = new MapRoom { id = "top", name = "top", x = -10, y = 24, width = 10, height = 8 };
        var detached = new MapRoom { id = "detached", name = "detached", x = 60, y = 4, width = 8, height = 8 };
        var session = new MapEditSession(new MapDocument { rooms = new() { room, right, top, detached } });
        using var editor = new MapRoomEditing(session);
        string before = session.CurrentJson;
        var plan = editor.PlanResolutionFit("fit", 320, 180);
        Check(plan.Width == 20 && plan.Height == 12, "Round up partial source-pixel tiles.");
        Check(plan.RemovedForeground == 2 && plan.RemovedBackground == 1 && plan.RemovedObjects == 4, "Count both tile layers, transformed objects and outside nodes.");
        Check(session.CurrentJson == before && !session.CanUndo, "Preview does not change data or history.");
        try { editor.FitResolution("fit", 320, 180); throw new Exception("Expected confirmation requirement."); }
        catch (InvalidOperationException error) { Check(error.Message == "@roomFitResolutionNeedsConfirm", error.Message); }
        Check(session.CurrentJson == before && !session.CanUndo, "Unconfirmed cropping is atomic.");
        editor.FitResolution("fit", 320, 180, true);
        room = session.Document.rooms[0];
        Check(room.x == -10 && room.y == 4 && room.width == 20 && room.height == 12, "Room origin is preserved.");
        Check(room.foreground.Count == 2 && room.background.Count == 1 && room.objects.Select(o => o.id).SequenceEqual(new[] { "inside", "edge", "rotated-inside", "node-edge" }), "Keep wholly contained contents and IDs.");
        Check(session.Document.rooms[1].x == 10 && session.Document.rooms[2].y == 16 && session.Document.rooms[3].x == 60, "Attached rooms follow; detached rooms stay fixed.");
        string after = session.CurrentJson;
        editor.FitResolution("fit", 320, 180);
        Check(session.CurrentJson == after, "Fitting an already fitted room is a no-op.");
        session.Undo(); Check(session.CurrentJson == before && !session.CanUndo, "One Undo restores deleted contents and neighbor positions.");
        session.Redo(); Check(session.CurrentJson == after, "Redo restores the exact fitted room.");

        var empty = new MapRoom { id = "empty", name = "empty", width = 8, height = 8 };
        var cleanSession = new MapEditSession(new MapDocument { rooms = new() { empty } });
        using var clean = new MapRoomEditing(cleanSession);
        clean.FitResolution("empty", 640, 360);
        Check(cleanSession.Document.rooms[0].width == 40 && cleanSession.Document.rooms[0].height == 23, "No confirmation is needed for an empty resize.");
        cleanSession.Document.rooms[0].locked = true;
        try { clean.PlanResolutionFit("empty", 320, 180); throw new Exception("Expected locked-room rejection."); }
        catch (InvalidOperationException) { }
    }
}
