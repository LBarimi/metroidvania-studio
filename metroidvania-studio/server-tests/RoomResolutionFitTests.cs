using System.Text.Json;
using MetroidvaniaStudio;
using MetroidvaniaStudio.Server;

internal static class RoomResolutionFitTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Send(EditorWorkspace w, string action, params (string key, object value)[] values)
    {
        var command = new Dictionary<string, object> { ["action"] = action, ["clientId"] = "room-fit", ["commandId"] = Guid.NewGuid().ToString("N"),
            ["expectedInstanceId"] = w.InstanceId, ["expectedRevision"] = w.Revision };
        foreach (var (key, value) in values) command[key] = value;
        w.Command(JsonSerializer.SerializeToElement(command));
    }
    public static void Defaults(EditorWorkspace w)
    {
        Send(w, "cameraSettings", ("ppu", 32), ("referenceWidth", 640), ("referenceHeight", 360));
        Send(w, "new", ("name", "Resolution defaults"), ("discard", true));
        Check(w.Canvas.Room.width == 40 && w.Canvas.Room.height == 23, "New maps use the current reference resolution.");
        var camera = MapCameraSettings.Resolve(w.Session.Document);
        Check(camera.ppu == 32 && camera.referenceWidth == 640 && camera.referenceHeight == 360, "New maps preserve camera settings.");
        Send(w, "cameraSettings", ("ppu", 16), ("referenceWidth", 320), ("referenceHeight", 180));
        string before = w.Session.CurrentJson;
        Send(w, "roomAdd", ("x", 100), ("y", 0));
        Check(w.Canvas.Room.width == 20 && w.Canvas.Room.height == 12, "Omitted API dimensions use the latest resolution.");
        Check(w.Session.Document.rooms[0].width == 40 && w.Session.Document.rooms[0].height == 23, "Existing rooms keep their dimensions.");
        Send(w, "undo"); Check(w.Session.CurrentJson == before, "One Undo restores the map before room creation.");
        Send(w, "roomAdd", ("x", 100), ("y", 0), ("width", 7), ("height", 5));
        Check(w.Canvas.Room.width == 7 && w.Canvas.Room.height == 5, "Explicit custom dimensions remain available.");
    }
    public static void Workflow(EditorWorkspace w)
    {
        var room = new MapRoom { id = "fit", name = "fit", width = 40, height = 24,
            foreground = new() { new() { x = 30, y = 18, material = "stone" } },
            objects = new() { new() { id = "outside", definition = "InvisibleWall", x = 19, y = 3, width = 2, height = 1, layer = MapLayer.Entities } } };
        w.Session.New(new MapDocument { rooms = new() { room } }); w.Canvas.SelectRoom("fit");
        Send(w, "cameraSettings", ("ppu", 16), ("referenceWidth", 320), ("referenceHeight", 180));
        string before = w.Session.CurrentJson; long revision = w.Revision;
        var preview = JsonSerializer.SerializeToElement(w.PreviewRoomResolutionFit("fit"));
        Check(preview.GetProperty("width").GetInt32() == 20 && preview.GetProperty("height").GetInt32() == 12 && preview.GetProperty("tiles").GetInt32() == 1 && preview.GetProperty("objects").GetInt32() == 1, "Authoritative preview counts.");
        Check(w.Session.CurrentJson == before && w.Revision == revision, "Preview is read-only.");
        try { Send(w, "roomFitResolution", ("id", "fit")); throw new Exception("Expected warning."); }
        catch (InvalidOperationException e) { Check(e.Message == "@roomFitResolutionNeedsConfirm", e.Message); }
        Check(w.Session.CurrentJson == before, "Missing confirmation does not crop.");
        Send(w, "cameraSettings", ("ppu", 32), ("referenceWidth", 320), ("referenceHeight", 180));
        try { Send(w, "roomFitResolution", ("id", "fit"), ("allowCrop", true), ("expectedRevision", revision)); throw new Exception("Expected stale confirmation rejection."); }
        catch (WorkspaceConflict) { }
        Check(w.Canvas.Room.width == 40 && w.Canvas.Room.objects.Count == 1, "A stale confirmation cannot delete contents.");
        before = w.Session.CurrentJson;
        Send(w, "roomFitResolution", ("id", "fit"), ("allowCrop", true));
        Check(w.Canvas.Room.width == 20 && w.Canvas.Room.height == 12 && w.Canvas.Room.objects.Count == 0 && w.Canvas.Room.foreground.Count == 0, "The current resolution is used regardless of PPU.");
        string after = w.Session.CurrentJson;
        Send(w, "undo"); Check(w.Session.CurrentJson == before, "One Undo restores removed objects and tiles.");
        Send(w, "redo"); Check(w.Session.CurrentJson == after, "Redo restores fitted JSON.");
    }
}
