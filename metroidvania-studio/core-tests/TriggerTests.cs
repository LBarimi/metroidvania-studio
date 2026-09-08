using MetroidvaniaStudio;

internal static class TriggerTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Requests()
    {
        foreach (int i in Enumerable.Range(0, 201))
        {
            string name = i == 0 ? "None" : "Trigger" + i.ToString("D3");
            Check(StudioTriggerEvents.TryParse(name, out var parsed) && (int)parsed == i, "Stable event number: " + name);
        }
        foreach (string invalid in new[] { "old-event", "Trigger201", "Trigger1", "-1", "+1", "1.0", "999999999999999999999" })
            Check(!StudioTriggerEvents.TryParse(invalid, out _), "Reject unsupported event: " + invalid);
        MapObject Object(string id, bool once, string definition = "Area", string triggerEvent = "Trigger200") => new()
        {
            id = id, definition = definition, layer = definition == "Area" ? MapLayer.Triggers : MapLayer.Entities,
            properties = new() { new() { key = "event", value = triggerEvent }, new() { key = "once", value = once.ToString() }, new() { key = "desc", value = "Open gate / 문 열기" } }
        };
        var room = new MapRoom { id = "room-a", name = "Entry", x = -7, y = 3,
            objects = new() { Object("repeat", false), Object("once", true), Object("portal", false, "Portal"), Object("wall", false, "InvisibleWall"), Object("legacy", false, "Area", "old-event") } };
        var manager = new StudioTriggerManager(); manager.RegisterRoom(room);
        var deliveries = new List<StudioTriggerInfo>();
        manager.TriggerRequested += info =>
        {
            deliveries.Add(info);
            if (info.ObjectId == "once") Check(!manager.TryRequest(info.ObjectId, out _), "One-shot callback cannot reenter.");
        };
        Check(manager.TryGet("once", out var before) && before.Once, "Query does not consume.");
        Check(manager.TryRequest("once", out var info) && !manager.TryRequest("once", out _), "Once delivers once.");
        Check(info.RoomX == -7 && info.RoomY == 3 && info.Event == StudioTriggerEvent.Trigger200 && info.Description == "Open gate / 문 열기", "Authored coordinates, enum, ID and description survive.");
        Check(manager.TryRequest("repeat", out _) && manager.TryRequest("repeat", out _), "Repeat events repeat.");
        Check(!manager.TryGet("portal", out _) && !manager.TryRequest("portal", out _) && !manager.TryRequest("wall", out _), "Portals and invisible walls never dispatch numbered events, including legacy properties.");
        Check(!manager.TryRequest("legacy", out _) && !manager.TryRequest("missing", out _), "No accidental dispatch for unassigned or missing events.");
        room.x = 100; room.objects[1].properties[2].value = "Changed";
        Check(manager.TryGet("once", out info) && info.RoomX == -7, "Registration stores a snapshot.");
        manager.RegisterRoom(room);
        Check(!manager.TryRequest("once", out _) && manager.TryGet("once", out info) && info.RoomX == 100, "Reload updates metadata but retains once state.");
        manager.UnregisterRoom(room.id); manager.RegisterRoom(room);
        Check(!manager.TryRequest("once", out _), "Reentering a room retains once state.");
        var duplicate = new MapRoom { id = "room-b", objects = new() { Object("repeat", false) } };
        bool rejected = false; try { manager.RegisterRoom(duplicate); } catch (ArgumentException) { rejected = true; }
        Check(rejected && manager.TryGet("repeat", out info) && info.RoomId == room.id, "Duplicate IDs reject atomically.");
        duplicate.objects[0].id = ""; rejected = false;
        try { manager.RegisterRoom(duplicate); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Empty IDs reject.");
        Check(manager.ResetOnce("once") && manager.TryRequest("once", out _), "Explicit reset reactivates.");
        manager.ResetAllOnce(); Check(manager.TryRequest("once", out _) && !manager.TryRequest("portal", out _), "Reset only reactivates event triggers.");
        manager.Clear(); Check(!manager.TryGet("once", out _), "Clear removes data and session state.");
        manager.RegisterRoom(room); Check(manager.TryRequest("once", out _), "New world starts unconsumed.");
    }
}
