using System.Text.Json;
using MetroidvaniaStudio;
using MetroidvaniaStudio.Server;

internal static class RoomRestructureTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Send(EditorWorkspace workspace, string action, params (string key, object value)[] values)
    {
        var command = new Dictionary<string, object> { ["action"] = action, ["clientId"] = "room-restructure",
            ["commandId"] = Guid.NewGuid().ToString("N"), ["expectedRevision"] = workspace.Revision, ["expectedInstanceId"] = workspace.InstanceId };
        foreach (var value in values) command[value.key] = value.value;
        workspace.Command(JsonSerializer.SerializeToElement(command));
    }
    public static void Workflow(EditorWorkspace workspace)
    {
        var document = new MapDocument { name = "Room edits", rooms = new() };
        for (int i = 0; i < 2; i++) document.rooms.Add(new()
        {
            id = i == 0 ? "A" : "B", name = i == 0 ? "A" : "B", x = i * 8, width = 8, height = 8,
            foreground = new() { new() { x = 1, y = 1 } }, background = new() { new() { x = 3, y = 5 } },
            objects = new() { new() { id = (100 + i).ToString(), definition = "Portal", layer = MapLayer.Entities, x = 2, y = 2, width = 1, height = 3 } }
        });
        Send(workspace, "import", ("document", JsonDocument.Parse(MapDocumentStore.Serialize(document)).RootElement), ("discard", true));
        string baseline = MapDocumentStore.Serialize(workspace.Session.Document);
        MapDocument[] Exports()
        {
            workspace.FlushAutoExports();
            string directory = Path.Combine(workspace.Files.MapsPath, "AutoExport", AutoRoomExporter.MapKey(null));
            return Directory.GetFiles(directory, "*.json").Select(MapDocumentStore.Load).ToArray();
        }
        Check(Exports().Length == 2, "Initial rooms export separately.");
        Send(workspace, "selectRoom", ("id", "A")); Send(workspace, "selectRoom", ("id", "B"), ("toggle", true));
        Send(workspace, "options", ("layer", 1));
        // Options can reset the room group when choosing a tool; explicitly restore it.
        Send(workspace, "selectRoom", ("id", "A")); Send(workspace, "selectRoom", ("id", "B"), ("toggle", true));
        Send(workspace, "roomMerge");
        Check(workspace.Canvas.Room.id == "B" && workspace.Canvas.Room.width == 16 && workspace.Canvas.RoomEditor.SelectedIds.Count == 1, "Merge result is the only active selected room.");
        Check(Exports().Length == 1 && Exports().Single().rooms.Single().objects.Count == 2, "Auto-export removes the old room and includes every placement.");
        Send(workspace, "options", ("tool", 2), ("layer", 1));
        Send(workspace, "selectArea", ("x", 0), ("y", 0), ("width", 8), ("height", 8));
        Send(workspace, "roomSplit");
        Check(workspace.Session.Document.rooms.Count == 2 && workspace.Canvas.Room.id != "B" && workspace.Canvas.Selection == null, "Split selects the extracted room and clears the old area.");
        var exports = Exports();
        Check(exports.Length == 2 && exports.Sum(map => map.rooms.Single().foreground.Count) == 2 && exports.Sum(map => map.rooms.Single().background.Count) == 2, "Splitting from a background selection includes both tile layers.");
        Check(exports.SelectMany(map => map.rooms.Single().objects).Select(item => item.id).Order().SequenceEqual(new[] { "100", "101" }), "Numeric placement IDs survive exports.");
        string split = MapDocumentStore.Serialize(workspace.Session.Document);
        Send(workspace, "undo"); Check(Exports().Length == 1, "Undo removes split exports.");
        Send(workspace, "undo"); Check(MapDocumentStore.Serialize(workspace.Session.Document) == baseline && Exports().Length == 2, "Undo merge restores original room JSON.");
        Send(workspace, "redo"); Send(workspace, "redo");
        Check(MapDocumentStore.Serialize(workspace.Session.Document) == split, "Redo keeps room IDs and all contents.");
        long revision = workspace.Revision;
        try { Send(workspace, "roomSplit"); throw new Exception("Expected missing-area rejection."); }
        catch (InvalidOperationException error) { Check(error.Message == "@roomSplitNeedArea", error.Message); }
        Check(MapDocumentStore.Serialize(workspace.Session.Document) == split && workspace.Revision == revision, "Rejected split preserves map and revision.");
    }
}
