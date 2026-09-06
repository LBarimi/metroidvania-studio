using System.Text.Json;
using System.Text;
using MetroidvaniaStudio.Server;
using MetroidvaniaStudio;

var tests = new (string name, Action run)[]
{
    ("standalone workspace loads samples without an engine installation", StandaloneWorkspace),
    ("workspace path settings preserve storage boundaries", WorkspaceConfiguration),
    ("browser JSON import validates before replacing the document", () => Fixture(JsonImport)),
    ("graceful shutdown persists changes and rejects new commands", () => Fixture(GracefulShutdown)),
    ("failed shutdown keeps the workspace available", () => Fixture(FailedShutdown)),
    ("sync status distinguishes publication from named-map saving and preserves gestures", () => Fixture(SyncPublication)),
    ("sync status follows map and selected-room source identities", () => Fixture(SyncSourceIdentity)),
    ("automatic room export debounces one second and publishes latest state", () => Fixture(AutoExportDebounce)),
    ("automatic room export rewrites only changed rooms and shared metadata", () => Fixture(AutoExportIncremental)),
    ("automatic room export names and owned rename/delete cleanup are safe", () => Fixture(AutoExportNames)),
    ("automatic room exports are isolated from named maps and file commands", () => Fixture(AutoExportIdentity)),
    ("automatic room export preserves external conflicts and retries safely", () => Fixture(AutoExportConflict)),
    ("automatic room export IO never blocks editing and newest snapshot wins", AutoExportOutsideGate),
    ("automatic room export flush terminates after IO failure", () => Fixture(AutoExportFailure)),
    ("stale view revision rejects mutation", () => Fixture(StaleView)),
    ("foreign view cannot steal a paint gesture", () => Fixture(ForeignGesture)),
    ("invalid begin releases gesture ownership", () => Fixture(InvalidBegin)),
    ("repeated begin cancels prior uncommitted stroke", () => Fixture(DuplicateBegin)),
    ("expired gesture releases ownership and cancels unfinished paint", () => Fixture(ExpiredGesture)),
    ("named room creation is one undo", () => Fixture(RoomAdd)),
    ("invalid room name never leaves a created room", () => Fixture(InvalidRoomAdd)),
    ("recovery write failure keeps committed edit and fresh revision", () => Fixture(RecoveryFailure)),
    ("invalid options preserve all current options", () => Fixture(InvalidOptions)),
    ("brush size boundary is accepted and rejected atomically", () => Fixture(BrushSizeBoundary)),
    ("external disk changes block overwrite and preserve both copies", () => Fixture(DiskConflict)),
    ("same-path save detects a change at the atomic publish boundary", SaveBoundaryRace),
    ("save conflict retains every surviving preserved backup", PreservedBackupRetention),
    ("Save As no-overwrite atomically rejects a competing create", SaveAsCreateRace),
    ("Save As overwrite detects a changed target at the publish boundary", SaveAsOverwriteRace),
    ("room export rollback preserves a destination changed after publication", () => Fixture(ExportRollbackRace)),
    ("room export overwrite detects a changed target at the publish boundary", () => Fixture(ExportOverwriteBoundaryRace)),
    ("room export overwrite rejects a target created at the publish boundary", () => Fixture(ExportCreateBoundaryRace)),
    ("room export aggregate work is rejected atomically", () => Fixture(ExportAggregateLimit)),
    ("malformed load preserves current document and history", () => Fixture(InvalidLoad)),
    ("stable and background map reads require UTF-8 with optional BOM", () => Fixture(MapFileEncoding)),
    ("catalog reads require UTF-8 with optional BOM", () => Fixture(CatalogFileEncoding)),
    ("clean workspace follows external disk changes", () => Fixture(DiskReload)),
    ("metadata reload work stays outside gate and stale results preserve edits", MetadataProbeOutsideGate),
    ("clean workspace detects equal-metadata disk rewrite", () => Fixture(EqualMetadataDiskReload)),
    ("malformed external map is reported once and retried after change", () => Fixture(InvalidDiskReload)),
    ("oversized external map is rejected without hashing its payload", () => Fixture(OversizedDiskReload)),
    ("deleted saved map is reported once and reappearance reloads", () => Fixture(DiskDeleteAndReappear)),
    ("partial state carries independent revision tokens", () => Fixture(PartialState)),
    ("clean transitions remove stale recovery snapshots", () => Fixture(CleanRecovery)),
    ("invalid recovery is quarantined before saved-map fallback", () => Fixture(CorruptRecoveryFallback)),
    ("room resizing and continuous erase call shared core", () => Fixture(ResizeAndErase)),
    ("room property position moves contents without cropping", () => Fixture(RoomPropertyPosition)),
    ("room property resize preserves neighbor gap in one Undo", () => Fixture(RoomPropertyResize)),
    ("room property move and resize plans final bounds atomically", () => Fixture(RoomPropertyMoveResize)),
    ("room property resize failure preserves metadata and bounds", () => Fixture(RoomPropertyLockedFailure)),
    ("room selection and movement validation are atomic", () => Fixture(RoomTargetAtomicity)),
    ("room command routing and selection-only revisions", () => Fixture(RoomCommands)),
    ("room import and selective JSON exports preserve identities and source", () => Fixture(RoomFiles)),
    ("camera settings persist, export and undo atomically", () => Fixture(CameraSettings)),
    ("camera profiles reject invalid catalog values atomically", () => Fixture(CameraValidation)),
    ("disk and catalog health notices survive ordinary commands", () => Fixture(PersistentHealthNotices)),
    ("completed command retry is idempotent", () => Fixture(IdempotentCommand)),
    ("reused command ID rejects a different payload", () => Fixture(IdempotentPayloadMismatch)),
    ("catalog detects equal-metadata content replacement", () => Fixture(EqualMetadataCatalogRewrite)),
    ("catalog collection and aggregate work are bounded", () => Fixture(CatalogComplexityLimits)),
    ("stale server instance rejects matching revision mutation", () => Fixture(StaleInstance)),
    ("storage rejects traversal, rooted paths and device aliases", Paths),
    ("portable paths preserve separators, extension discovery and distinct map identities", () => Fixture(PortablePaths)),
    ("restart recovers last committed document", () => Fixture(Recovery)),
    ("save intent prevents stale recovery after target publication", StaleRecoveryAfterSave),
    ("new dirty recovery supersedes a clean saved target", () => Fixture(DirtyRecoveryAfterSave)),
    ("legacy raw recovery snapshots still open", () => Fixture(LegacyRecovery)),
    ("unthemed catalog material does not recolor rooms", () => Fixture(UnthemedMaterial)),
    ("batched tile gesture preserves curved paths in one undo", () => Fixture(TileGesturePath)),
    ("same-room batched tile gesture preserves selection", () => Fixture(TileGesturePreservesSelection)),
    ("batched tile gesture enforces its raster path boundary", () => Fixture(TileGestureRasterBoundary)),
    ("bucket fill obeys paint work budget atomically", () => Fixture(BucketWorkBoundary)),
    ("invalid batched tile gesture is atomic", () => Fixture(TileGestureInvalid)),
    ("batched tile gesture targets its requested room", () => Fixture(TileGestureRoomSwitch)),
    ("batched object erase follows curves and stays in its requested room", () => Fixture(ObjectGestureErase)),
    ("batched object placement creates one rectangle and one undo", () => Fixture(ObjectGesturePlacement)),
    ("node placement keeps its dragged endpoint", () => Fixture(ObjectGestureNodePlacement)),
    ("invalid batched object gesture is atomic", () => Fixture(ObjectGestureInvalid)),
    ("object gesture intersection work is bounded atomically", () => Fixture(ObjectGestureWorkBoundary))
};
int failed = 0;
foreach (var test in tests)
{
    try { test.run(); Console.WriteLine("PASS " + test.name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + test.name + "\n" + error); }
}
Console.WriteLine($"Server regression: {tests.Length - failed}/{tests.Length} passed.");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static T Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T error) { return error; }
    throw new Exception("Expected " + typeof(T).Name);
}
static string Snapshot(EditorWorkspace workspace) => MapDocumentStore.Serialize(workspace.Session.Document);
static byte[] WithPreamble(Encoding encoding, string text)
    => encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
static JsonElement StateElement(EditorWorkspace workspace, bool includeDocument = true, bool includeCatalog = true,
    bool includeSelection = true) =>
    JsonSerializer.SerializeToElement(workspace.State(includeDocument, includeCatalog, includeSelection));
static void Send(EditorWorkspace workspace, string action, params (string key, object? value)[] values)
{
    var request = new Dictionary<string, object?> { ["action"] = action, ["clientId"] = "A", ["commandId"] = Guid.NewGuid().ToString("N"), ["expectedRevision"] = workspace.Revision,
        ["expectedInstanceId"] = workspace.InstanceId };
    foreach (var item in values) request[item.key] = item.value;
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(request)); workspace.Command(json.RootElement);
}
static void Fixture(Action<EditorWorkspace> action)
{
    string project = Path.GetFullPath("../../..", AppContext.BaseDirectory);
    if (!File.Exists(Path.Combine(project, "MetroidvaniaStudio.Server.Tests.csproj"))) throw new Exception("Fixtures must remain in Server.Tests.");
    string dir = Path.Combine(project, ".test-output", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    EditorWorkspace? workspace = null;
    try { workspace = new EditorWorkspace(new ProjectFiles(dir)); action(workspace); }
    finally { workspace?.Cancel(); workspace?.FlushRecovery(); workspace?.StopAutoExports(); workspace?.Canvas.Dispose(); DeleteFixtureDirectory(dir); }
}

static string AutoDirectory(EditorWorkspace w) => Path.Combine(w.Files.MapsPath, "AutoExport", AutoRoomExporter.MapKey(
    w.Session.FilePath == null ? null : w.Files.Relative(w.Session.FilePath)));
static string[] AutoFiles(EditorWorkspace w) => Directory.Exists(AutoDirectory(w))
    ? Directory.GetFiles(AutoDirectory(w), "*.json") : [];

static void SyncPublication(EditorWorkspace w)
{
    EditorState queued = w.State();
    Check(queued.export.phase == "queued" && queued.dirty && queued.export.hash == null,
        "Queued exports must never report persisted room bytes.");
    w.FlushAutoExports();
    Check(!w.IsStateCurrent(queued.revision, queued.instanceId, queued.documentRevision, queued.catalogRevision),
        "Publication completion must wake incremental state polling without an edit.");
    EditorState published = w.State(false, false, false);
    Check(published.export.phase == "saved" && published.dirty && published.document == null && published.catalog == null,
        "Automatic export does not mark the authoring document saved or resend large payloads.");
    string expectedHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(AutoFiles(w).Single())));
    Check(published.export.hash == expectedHash && published.export.roomId == w.Canvas.ActiveRoomId,
        "The acknowledgement identity must describe the exact published UTF-8 bytes using Base64 SHA-256.");
    using var passiveRevisionCommand = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        action = "options", brushSize = 2, clientId = "passive", commandId = "sync-status",
        expectedInstanceId = queued.instanceId, expectedRevision = queued.revision
    }));
    w.Command(passiveRevisionCommand.RootElement);
    Check(w.Canvas.BrushSize == 2, "Passive sync progress must not make a brush command stale.");
    JsonElement document = StateElement(w).GetProperty("document");
    Check(document.GetProperty("formatVersion").GetInt32() == 2 && !document.TryGetProperty("version", out _),
        "The shared document contract must retain the actual serialized formatVersion field.");
}

static void SyncSourceIdentity(EditorWorkspace w)
{
    w.FlushAutoExports(); string? workspacePath = w.State().export.path;
    Send(w, "save", ("path", "named.map.json"));
    Check(w.State().export.phase == "queued" && w.State().export.path == null,
        "Save As must clear the prior map namespace's acknowledgement target immediately.");
    w.FlushAutoExports();
    Check(w.State().export.path != workspacePath && !w.State().dirty, "Named map exports must have their own identity.");
    Send(w, "roomAdd", ("x", 60), ("y", 0), ("name", "other")); w.FlushAutoExports();
    Check(w.State().export.roomId == w.Canvas.ActiveRoomId && w.State().export.path!.EndsWith("/other.json"),
        "Room selection must select the corresponding publication identity.");
    Send(w, "roomProperties", ("id", w.Canvas.ActiveRoomId), ("name", "renamed")); w.FlushAutoExports();
    Check(w.State().export.path!.EndsWith("/renamed.json"), "Renaming must update the required source path.");
}

static void AutoExportDebounce(EditorWorkspace w)
{
    Send(w, "roomProperties", ("id", w.Canvas.Room.id), ("name", "first"));
    Thread.Sleep(600);
    Send(w, "roomProperties", ("id", w.Canvas.Room.id), ("name", "latest"));
    Thread.Sleep(550);
    Check(AutoFiles(w).Length == 0, "A newer edit resets the idle deadline; the first deadline must not publish.");
    Check(SpinWait.SpinUntil(() => File.Exists(Path.Combine(AutoDirectory(w), "latest.json")), 3000),
        "Committed room JSON must appear after one second of inactivity without a polling request.");
    w.FlushAutoExports();
    MapDocument room = MapDocumentStore.Load(AutoFiles(w).Single());
    Check(room.rooms.Count == 1 && room.rooms[0].name == "latest" && room.formatVersion == 2 && room.tileSize == 16,
        "The latest snapshot must remain a standalone, supported room document.");
}
static void AutoExportIncremental(EditorWorkspace w)
{
    string first = w.Canvas.Room.id;
    Send(w, "roomAdd", ("x", 60), ("y", 0), ("name", "second"));
    w.FlushAutoExports();
    string secondPath = Path.Combine(AutoDirectory(w), "second.json");
    DateTime sentinel = DateTime.UtcNow.AddDays(-2);
    File.SetLastWriteTimeUtc(secondPath, sentinel);
    Send(w, "selectRoom", ("id", first));
    Send(w, "begin", ("x", 2), ("y", 2)); Send(w, "end", ("x", 3), ("y", 2));
    w.FlushAutoExports();
    Check(File.GetLastWriteTimeUtc(secondPath) == sentinel,
        "Painting one room must not rewrite an unaffected room.");
    Check(MapDocumentStore.Load(Path.Combine(AutoDirectory(w), "room_00.json")).rooms[0].foreground.Count == 2,
        "Only the changed room must publish its new tile data.");
    Send(w, "documentProperties", ("properties", new[] { new { key = "shared", value = "updated" } }));
    w.FlushAutoExports();
    Check(AutoFiles(w).All(path => MapDocumentStore.Load(path).properties.Single().value == "updated"),
        "Global metadata changes must propagate into every standalone room document.");
}
static void AutoExportNames(EditorWorkspace w)
{
    var document = new MapDocument { rooms = new List<MapRoom>
    {
        new() { id = "a", name = "same/한글", x = 0 },
        new() { id = "b", name = "same/한글", x = 50 },
        new() { id = "c", name = "CON", x = 100 },
        new() { id = "d", name = ".hidden", x = 150 }
    }};
    w.Session.New(document); w.FlushAutoExports();
    Check(AutoFiles(w).Select(Path.GetFileName).Order().SequenceEqual(new[] { "_.hidden.json", "_CON.json", "same_한글 (2).json", "same_한글.json" }.Order()),
        "Unsafe, Unicode, duplicate and reserved Windows names must export without collisions.");
    string old = Path.Combine(AutoDirectory(w), "_CON.json");
    Send(w, "roomProperties", ("id", "c"), ("name", "renamed")); w.FlushAutoExports();
    Check(!File.Exists(old) && File.Exists(Path.Combine(AutoDirectory(w), "renamed.json")),
        "A rename removes only the old generated room and its sidecar.");
    Send(w, "roomDelete", ("id", "c")); w.FlushAutoExports();
    Check(AutoFiles(w).Length == 3 && !File.Exists(Path.Combine(AutoDirectory(w), "renamed.json")),
        "A deleted room must disappear from the automatic loader list.");
}
static void AutoExportIdentity(EditorWorkspace w)
{
    w.FlushAutoExports(); string workspaceDirectory = AutoDirectory(w);
    Send(w, "save", ("path", "named.map.json")); w.FlushAutoExports(); string namedDirectory = AutoDirectory(w);
    Check(workspaceDirectory != namedDirectory && AutoFiles(w).Length == 1, "Saving under a name must queue an export even when room JSON is unchanged.");
    Check(w.Files.List().SequenceEqual(new[] { "named.map.json" }), "Generated room JSON must never become a saved-map startup candidate.");
    Throws<ArgumentException>(() => w.Files.Map("AutoExport/Workspace/room_00.json"));
    Throws<ArgumentException>(() => w.Files.ExportDirectory("AutoExport/foreign"));
    var escape = Throws<System.Reflection.TargetInvocationException>(() => typeof(ProjectFiles)
        .GetMethod("AutoExportPath", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(w.Files, new object[] { "../outside.json" }));
    Check(escape.InnerException is ArgumentException, "The internal automatic output resolver must enforce its own AutoExport root.");
    Send(w, "new", ("name", "replacement"));
    Send(w, "roomProperties", ("id", w.Canvas.Room.id), ("name", "new-room")); w.FlushAutoExports();
    Check(AutoDirectory(w) == workspaceDirectory && AutoFiles(w).Select(Path.GetFileName).SequenceEqual(new[] { "new-room.json" })
        && Directory.GetFiles(namedDirectory, "*.json").Length == 1,
        "New replaces only the unsaved workspace exports; saved map identity remains isolated.");
    Check((AutoRoomExporter.MapKey("Folder/Map.json") == AutoRoomExporter.MapKey("folder\\map.JSON")) == OperatingSystem.IsWindows(),
        "Windows export identity keeps its legacy casing; Unix names remain distinct.");
    Check(AutoRoomExporter.MapKey("Folder/Map.json") == AutoRoomExporter.MapKey("Folder\\Map.json"),
        "Portable separators identify the same map on every platform.");
}
static void AutoExportConflict(EditorWorkspace w)
{
    w.FlushAutoExports();
    string path = AutoFiles(w).Single(); string own = File.ReadAllText(path);
    File.WriteAllText(path, "externally authored content", new UTF8Encoding(false));
    Send(w, "begin", ("x", 1), ("y", 1)); Send(w, "end", ("x", 1), ("y", 1)); w.FlushAutoExports();
    Check(File.ReadAllText(path) == "externally authored content" && w.Notice?.Contains("auto-export") == true,
        "A changed generated file must be preserved and reported without rejecting the edit.");
    File.WriteAllText(path, own, new UTF8Encoding(false));
    Send(w, "begin", ("x", 2), ("y", 1)); Send(w, "end", ("x", 2), ("y", 1)); w.FlushAutoExports();
    Check(MapDocumentStore.Load(path).rooms[0].foreground.Count == 2 && w.Notice?.Contains("auto-export") != true,
        "Restoring the known owned bytes must allow the next export to clear the conflict.");
    File.WriteAllText(path, "external old room", new UTF8Encoding(false));
    Send(w, "roomProperties", ("id", w.Canvas.Room.id), ("name", "renamed")); w.FlushAutoExports();
    Check(File.ReadAllText(path) == "external old room" && File.Exists(Path.Combine(AutoDirectory(w), "renamed.json")),
        "Rename cleanup preserves a foreign old file while still exporting the new room.");
}
static void AutoExportOutsideGate()
{
    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
    bool blocked = false;
    FixtureWithPublishHook(path =>
    {
        if (blocked || !path.Contains(Path.DirectorySeparatorChar + "AutoExport" + Path.DirectorySeparatorChar)
            || !path.EndsWith(".json", StringComparison.Ordinal)) return;
        blocked = true; entered.Set();
        if (!release.Wait(5000)) throw new IOException("Test publication release timed out.");
    }, w =>
    {
        Task flushing = Task.Run(w.FlushAutoExports);
        try
        {
            Check(entered.Wait(3000), "The asynchronous exporter must reach its publication hook.");
            Check(w.State().export.phase == "exporting", "Blocked publication must remain in progress rather than report saved.");
            Task editing = Task.Run(() =>
            {
                lock (w.Gate)
                {
                    Send(w, "options", ("brushSize", 2));
                    Send(w, "begin", ("x", 4), ("y", 4)); Send(w, "end", ("x", 4), ("y", 4));
                }
            });
            Check(editing.Wait(1000), "Blocked disk publication must not hold the editor gate or block a new brush command.");
        }
        finally { release.Set(); }
        Check(flushing.Wait(5000), "Flushing must consume the latest queued edit and finish after publication is released.");
        Check(MapDocumentStore.Load(AutoFiles(w).Single()).rooms[0].foreground.Count == 4,
            "A newer edit queued during old publication must be the final exported snapshot.");
        Check(w.State().export.phase == "saved" && w.State().export.hash == Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(AutoFiles(w).Single()))),
            "An obsolete export must not acknowledge the newer snapshot with stale bytes.");
    });
}
static void AutoExportFailure(EditorWorkspace w)
{
    string root = Path.Combine(w.Files.MapsPath, "AutoExport"); File.WriteAllText(root, "blocked directory");
    var watch = System.Diagnostics.Stopwatch.StartNew();
    w.FlushAutoExports();
    Check(watch.ElapsedMilliseconds < 2000 && w.Notice?.Contains("auto-export") == true,
        "An output failure must be visible and Flush must not wait through an infinite retry loop.");
    Check(w.State().export.phase == "error" && !string.IsNullOrEmpty(w.State().export.error), "Export errors must remain visible in status.");
    File.Delete(root);
    Send(w, "roomProperties", ("id", w.Canvas.Room.id), ("name", "after-error")); w.FlushAutoExports();
    Check(AutoFiles(w).Length == 1 && w.Notice?.Contains("auto-export") != true,
        "Output must recover after the folder is repaired and a new change arrives.");
}
static void StaleView(EditorWorkspace w)
{
    long original = w.Revision;
    Send(w, "documentProperties", ("name", "new revision")); string snapshot = Snapshot(w);
    Throws<WorkspaceConflict>(() => Send(w, "documentProperties", ("name", "stale overwrite"), ("expectedRevision", original), ("clientId", "B")));
    Check(Snapshot(w) == snapshot && w.Revision > original, "Rejected view must not overwrite newer content.");
}
static void ForeignGesture(EditorWorkspace w)
{
    Send(w, "begin", ("x", 1), ("y", 1));
    Throws<WorkspaceConflict>(() => Send(w, "end", ("x", 3), ("y", 1), ("clientId", "B")));
    Check(w.Session.IsEditing, "Foreign end must preserve original gesture.");
    Send(w, "drag", ("x", 3), ("y", 1)); Send(w, "end", ("x", 3), ("y", 1));
    Check(w.Canvas.Room.foreground.Count == 3, "Owner finishes all stroke tiles.");
    Send(w, "undo"); Check(w.Canvas.Room.foreground.Count == 0 && !w.Session.CanUndo, "One gesture must be one undo.");
}
static void InvalidBegin(EditorWorkspace w)
{
    Throws<InvalidOperationException>(() => Send(w, "begin", ("x", "invalid coordinate"), ("y", 1)));
    Send(w, "options", ("tool", (int)MetroidvaniaStudioTool.Rectangle), ("clientId", "B"));
    Check(!w.Session.IsEditing && w.Canvas.Tool == MetroidvaniaStudioTool.Rectangle, "Failed begin must release view ownership.");
}
static void DuplicateBegin(EditorWorkspace w)
{
    Send(w, "begin", ("x", 1), ("y", 1)); Send(w, "begin", ("x", 5), ("y", 5)); Send(w, "end", ("x", 5), ("y", 5));
    Check(w.Canvas.Room.foreground.Count == 1 && w.Canvas.Room.foreground[0].x == 5, "Second begin cancels older stroke.");
    Send(w, "undo"); Check(w.Canvas.Room.foreground.Count == 0 && !w.Session.CanUndo, "Cancelled stroke never becomes history.");
}
static void RoomAdd(EditorWorkspace w)
{
    Send(w, "roomAdd", ("x", 60), ("y", 0), ("name", "named room"));
    Check(w.Session.Document.rooms.Count == 2 && w.Canvas.Room.name == "named room", "Room created with intended name.");
    Send(w, "undo"); Check(w.Session.Document.rooms.Count == 1 && !w.Session.CanUndo, "Named room must disappear after one Undo.");
}
static void ExpiredGesture(EditorWorkspace w)
{
    Send(w, "begin", ("x", 1), ("y", 1)); long before = w.Revision;
    // Advance only the lease timestamp; avoid timing-sensitive sleeps in a deterministic console regression.
    typeof(EditorWorkspace).GetField("gestureAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .SetValue(w, DateTime.UtcNow - TimeSpan.FromSeconds(30));
    w.Tick();
    Check(!w.Session.IsEditing && w.Canvas.Room.foreground.Count == 0 && !w.Session.CanUndo && w.Revision > before, "Expired gesture must roll back its uncommitted paint.");
    Send(w, "options", ("tool", (int)MetroidvaniaStudioTool.Line), ("clientId", "B"));
    Check(w.Canvas.Tool == MetroidvaniaStudioTool.Line, "Another view can edit after timeout.");
}
static void InvalidRoomAdd(EditorWorkspace w)
{
    string before = Snapshot(w);
    Throws<InvalidOperationException>(() => Send(w, "roomAdd", ("x", 60), ("y", 0), ("name", 42)));
    Check(Snapshot(w) == before && !w.Session.CanUndo, "Invalid name must reject entire room addition.");
}
static void RecoveryFailure(EditorWorkspace w)
{
    string block = Path.Combine(w.Files.MapsPath, ".Recovery"); File.WriteAllText(block, "blocked fixture"); long before = w.Revision;
    try
    {
        Send(w, "documentProperties", ("name", "committed despite recovery warning"));
        w.FlushRecovery();
        Check(w.Session.Document.name == "committed despite recovery warning" && w.Session.CanUndo && w.Revision > before, "Committed changes must remain acknowledged with a fresh revision.");
        Check(!string.IsNullOrWhiteSpace(w.Notice), "Recovery failure requires an explicit visible notice.");
    }
    finally { File.Delete(block); }
}
static void InvalidOptions(EditorWorkspace w)
{
    MetroidvaniaStudioTool tool = w.Canvas.Tool; MapLayer layer = w.Canvas.Layer;
    Throws<ArgumentException>(() => Send(w, "options", ("tool", (int)MetroidvaniaStudioTool.Rectangle), ("layer", 999)));
    Check(w.Canvas.Tool == tool && w.Canvas.Layer == layer, "Invalid options are all-or-nothing.");
    const string groupId = "foreground-group";
    Send(w, "documentProperties", ("layerGroups", new[] { new { id = groupId, name = "Foreground", parentId = "", layer = (int)MapLayer.ForegroundTiles, visible = true, locked = false } }));
    Send(w, "options", ("layer", (int)MapLayer.ForegroundTiles), ("groupId", groupId));
    tool = w.Canvas.Tool; layer = w.Canvas.Layer; string selectedGroup = w.Canvas.ActiveGroupId;
    Throws<ArgumentException>(() => Send(w, "options", ("tool", (int)MetroidvaniaStudioTool.Rectangle),
        ("layer", (int)MapLayer.BackgroundTiles), ("groupId", groupId)));
    Check(w.Canvas.Tool == tool && w.Canvas.Layer == layer && w.Canvas.ActiveGroupId == selectedGroup,
        "A wrong-layer group must reject the complete option batch atomically.");
    Throws<ArgumentException>(() => Send(w, "options", ("groupId", "missing-group")));
    Check(w.Canvas.Tool == tool && w.Canvas.Layer == layer && w.Canvas.ActiveGroupId == selectedGroup,
        "A missing group must preserve every current option.");
}
static void FixtureWithPublishHook(Action<string> hook, Action<EditorWorkspace> action,
    Action<string>? rollbackHook = null, Action<string>? conflictCheckHook = null)
{
    string project = Path.GetFullPath("../../..", AppContext.BaseDirectory);
    string dir = Path.Combine(project, ".test-output", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    EditorWorkspace? workspace = null;
    try { workspace = new EditorWorkspace(new ProjectFiles(dir, hook, rollbackHook, conflictCheckHook)); action(workspace); }
    finally { workspace?.Cancel(); workspace?.FlushRecovery(); workspace?.StopAutoExports(); workspace?.Canvas.Dispose(); DeleteFixtureDirectory(dir); }
}
static void FixtureWithProbeHook(Action<string> hook, Action<EditorWorkspace> action)
{
    string project = Path.GetFullPath("../../..", AppContext.BaseDirectory);
    string dir = Path.Combine(project, ".test-output", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    EditorWorkspace? workspace = null;
    try { workspace = new EditorWorkspace(new ProjectFiles(dir, beforeMapProbe: hook)); action(workspace); }
    finally { workspace?.Cancel(); workspace?.FlushRecovery(); workspace?.StopAutoExports(); workspace?.Canvas.Dispose(); DeleteFixtureDirectory(dir); }
}
static void DeleteFixtureDirectory(string path)
{
    for (int attempt = 0; ; attempt++)
    {
        try { Directory.Delete(path, true); return; }
        catch (IOException) when (attempt < 5)
        {
            // Windows ReplaceFile can briefly retain its internal ~RF*.tmp
            // rename after returning. Production never deletes the map root;
            // let deterministic fixtures wait for that kernel cleanup.
            Thread.Sleep((attempt + 1) * 10);
        }
    }
}
static void BrushSizeBoundary(EditorWorkspace w)
{
    Send(w, "options", ("brushSize", MapBrushGeometry.MaximumBrushSize));
    Check(w.Canvas.BrushSize == MapBrushGeometry.MaximumBrushSize, "The maximum brush size must be accepted.");
    Send(w, "begin", ("x", 8), ("y", 8)); Send(w, "end", ("x", 8), ("y", 8));
    Check(w.Canvas.Room.foreground.Count == MapBrushGeometry.MaximumBrushSize * MapBrushGeometry.MaximumBrushSize,
        "The maximum brush size must paint successfully.");
    string before = Snapshot(w); long revision = w.Revision; bool undo = w.Session.CanUndo;
    Throws<ArgumentOutOfRangeException>(() => Send(w, "options", ("brushSize", MapBrushGeometry.MaximumBrushSize + 1)));
    Check(w.Canvas.BrushSize == MapBrushGeometry.MaximumBrushSize && Snapshot(w) == before
        && w.Revision == revision && w.Session.CanUndo == undo && !w.Session.IsEditing,
        "A brush above the maximum must preserve selection, document, revision, and history.");
}
static void DiskConflict(EditorWorkspace w)
{
    Send(w, "save", ("path", "source.map.json"));
    string path = w.Session.FilePath!;
    var stamp = new FileInfo(path); long savedLength = stamp.Length; DateTime savedTime = stamp.LastWriteTimeUtc;
    var external = w.Session.Document.Clone(); external.name = new string('X', external.name.Length);
    MapDocumentStore.Save(path, external);
    Check(new FileInfo(path).Length == savedLength, "The conflict fixture must preserve file length.");
    File.SetLastWriteTimeUtc(path, savedTime);
    var forgedStamp = new FileInfo(path);
    Check(forgedStamp.Length == savedLength && forgedStamp.LastWriteTimeUtc.Ticks == savedTime.Ticks,
        "The conflict fixture must restore the original metadata stamp.");
    Send(w, "documentProperties", ("name", "local change")); string local = Snapshot(w);
    Throws<WorkspaceConflict>(() => Send(w, "save", ("path", "source.map.json"), ("overwrite", true)));
    Check(Snapshot(w) == local && MapDocumentStore.Load(path).name == external.name,
        "Save must full-hash and preserve an external edit even when its length and timestamp were forged to match the cached stamp.");
    Send(w, "save", ("path", "local-copy.map.json")); Check(!w.Session.IsDirty, "Save As resolves conflict without destructive overwrite.");
}
static void SaveBoundaryRace()
{
    bool armed = false;
    MapDocument? external = null;
    MapDocument third = MapDocument.CreateDefault(); third.name = "third boundary writer";
    FixtureWithPublishHook(path =>
    {
        if (armed) MapDocumentStore.Save(path, external!);
    }, w =>
    {
        Send(w, "save", ("path", "source.map.json"));
        Send(w, "documentProperties", ("name", "local pending version"));
        string local = Snapshot(w);
        external = MapDocument.CreateDefault(); external.name = "external boundary version";
        armed = true;
        Throws<WorkspaceConflict>(() => Send(w, "save", ("path", "source.map.json"), ("overwrite", true)));
        armed = false;
        Check(Snapshot(w) == local && w.Session.IsDirty
            && MapDocumentStore.Load(w.Files.Map("source.map.json")).name == external.name,
            "A same-path writer at the publish boundary must be restored byte-for-byte while the local document stays dirty.");
        string[] preserved = Directory.GetFiles(w.Files.MapsPath, "*.rejected.tmp", SearchOption.TopDirectoryOnly);
        Check(preserved.Length == 1 && MapDocumentStore.Load(preserved[0]).name == third.name,
            "A third writer between verification and rollback must be retained as a distinct conflict artifact.");
    }, path => MapDocumentStore.Save(path, third));
}
static void PreservedBackupRetention()
{
    bool armed = false;
    var external = MapDocument.CreateDefault(); external.name = "external boundary version";
    FixtureWithPublishHook(path =>
    {
        if (armed) MapDocumentStore.Save(path, external);
    }, w =>
    {
        Send(w, "save", ("path", "source.map.json"));
        Send(w, "documentProperties", ("name", "local replacement"));
        armed = true;
        Throws<WorkspaceConflict>(() => Send(w, "save", ("path", "source.map.json"), ("overwrite", true)));
        armed = false;

        Check(MapDocumentStore.Load(w.Session.FilePath!).name == external.name,
            "The external destination written at the conflict boundary must remain live.");
        string[] preserved = Directory.GetFiles(w.Files.MapsPath, "*.preserved.tmp", SearchOption.TopDirectoryOnly);
        Check(preserved.Length == 1 && MapDocumentStore.Load(preserved[0]).name == external.name,
            "A backup surviving the conflict path must be retained even when the current destination has equal bytes.");
    }, conflictCheckHook: path =>
    {
        if (armed) MapDocumentStore.Save(path, external);
    });
}
static void SaveAsCreateRace()
{
    bool armed = false;
    MapDocument external = MapDocument.CreateDefault(); external.name = "competing create";
    FixtureWithPublishHook(path =>
    {
        if (armed) MapDocumentStore.Save(path, external);
    }, w =>
    {
        Send(w, "documentProperties", ("name", "local Save As"));
        string local = Snapshot(w);
        armed = true;
        Throws<WorkspaceConflict>(() => Send(w, "save", ("path", "new.map.json")));
        armed = false;
        Check(w.Session.FilePath == null && w.Session.IsDirty && Snapshot(w) == local
            && MapDocumentStore.Load(w.Files.Map("new.map.json")).name == external.name,
            "Create-new publication must preserve a file created after the preliminary existence check.");

        armed = true;
        Throws<WorkspaceConflict>(() => Send(w, "save", ("path", "new-with-overwrite.map.json"), ("overwrite", true)));
        armed = false;
        Check(w.Session.FilePath == null && w.Session.IsDirty && Snapshot(w) == local
            && MapDocumentStore.Load(w.Files.Map("new-with-overwrite.map.json")).name == external.name,
            "Overwrite permission must not replace a file created after the absent destination baseline.");
    });
}
static void SaveAsOverwriteRace()
{
    bool armed = false;
    MapDocument external = MapDocument.CreateDefault(); external.name = "external Save As boundary version";
    FixtureWithPublishHook(path =>
    {
        if (armed) MapDocumentStore.Save(path, external);
    }, w =>
    {
        string path = w.Files.Map("existing.map.json");
        MapDocument baseline = MapDocument.CreateDefault(); baseline.name = "confirmed overwrite target";
        MapDocumentStore.Save(path, baseline);
        Send(w, "documentProperties", ("name", "local Save As replacement"));
        string local = Snapshot(w);

        armed = true;
        Throws<WorkspaceConflict>(() => Send(w, "save", ("path", "existing.map.json"), ("overwrite", true)));
        armed = false;

        Check(w.Session.FilePath == null && w.Session.IsDirty && Snapshot(w) == local,
            "A rejected Save As overwrite must preserve the unsaved document and path.");
        Check(MapDocumentStore.Load(path).name == external.name,
            "A Save As writer at the publication boundary must remain live byte-for-byte.");

        Send(w, "save", ("path", "existing.map.json"), ("overwrite", true));
        Check(w.Session.FilePath == path && !w.Session.IsDirty && MapDocumentStore.Load(path).name == "local Save As replacement",
            "A confirmed unchanged Save As target must still be overwritten successfully.");
    });
}
static void ExportRollbackRace(EditorWorkspace w)
{
    var source = new MapDocument
    {
        name = "export source",
        rooms = new List<MapRoom>
        {
            new() { id = "first", name = "First", width = 8, height = 8 },
            new() { id = "second", name = "Second", x = 12, width = 8, height = 8 }
        }
    };
    string output = Path.Combine(w.Files.MapsPath, "Exports");
    Directory.CreateDirectory(output);
    var entries = MapRoomJsonExporter.Plan(source);
    string first = Path.Combine(output, entries[0].FileName);
    string second = Path.Combine(output, entries[1].FileName);
    var original = MapDocument.CreateDefault(); original.name = "original first";
    var external = MapDocument.CreateDefault(); external.name = "external after first publish";
    MapDocumentStore.Save(first, original);
    MapDocumentStore.Save(second, MapDocument.CreateDefault());

    Throws<IOException>(() => MapRoomJsonExporter.Export(source, output, true, null, null,
        (index, destination) =>
        {
            if (index != 1) return;
            MapDocumentStore.Save(first, external);
            File.Delete(destination);
            Directory.CreateDirectory(destination);
        }));

    Check(MapDocumentStore.Load(first).name == external.name,
        "Rollback must not overwrite bytes written externally after the first room was published.");
    string staging = Directory.GetDirectories(output, ".map-room-export-*", SearchOption.TopDirectoryOnly).Single();
    string backup = Path.Combine(staging, "0.backup");
    Check(File.Exists(backup) && MapDocumentStore.Load(backup).name == original.name,
        "The displaced pre-export original must remain in staging when the live destination changed.");
}
static void ExportOverwriteBoundaryRace(EditorWorkspace w)
{
    var source = new MapDocument
    {
        name = "export CAS source",
        rooms = new List<MapRoom>
        {
            new() { id = "first", name = "First", width = 8, height = 8 },
            new() { id = "second", name = "Second", x = 12, width = 8, height = 8 }
        }
    };
    IReadOnlyList<MapRoomJsonExporter.Entry> plan = MapRoomJsonExporter.Plan(source);
    string output = Path.Combine(w.Files.MapsPath, "export-cas");
    Directory.CreateDirectory(output);
    string first = Path.Combine(output, plan[0].FileName);
    string second = Path.Combine(output, plan[1].FileName);
    byte[] firstBaseline = Encoding.UTF8.GetBytes("first confirmed external bytes");
    byte[] secondBaseline = Encoding.UTF8.GetBytes("second confirmed external bytes");
    byte[] secondBoundary = Encoding.UTF8.GetBytes("second boundary writer bytes");
    File.WriteAllBytes(first, firstBaseline);
    File.WriteAllBytes(second, secondBaseline);

    Throws<IOException>(() => MapRoomJsonExporter.ExportPlanned(plan, output, true, null, null,
        (index, destination) =>
        {
            if (index == 1) File.WriteAllBytes(destination, secondBoundary);
        }));

    Check(File.ReadAllBytes(first).SequenceEqual(firstBaseline),
        "A room published before the conflict must be rolled back to its confirmed bytes.");
    Check(File.ReadAllBytes(second).SequenceEqual(secondBoundary),
        "The room changed at its publication boundary must be restored byte-for-byte.");
    Check(!Directory.EnumerateDirectories(output, ".map-room-export-*", SearchOption.TopDirectoryOnly).Any(),
        "A successful rollback must remove its staging directory.");

    string[] published = MapRoomJsonExporter.ExportPlanned(plan, output, true);
    Check(published.Length == 2 && published.All(File.Exists),
        "An unchanged overwrite target must still publish every room after a rolled-back conflict.");
    Check(!Directory.EnumerateDirectories(output, ".map-room-export-*", SearchOption.TopDirectoryOnly).Any(),
        "A successful export must remove its staging directory.");
}
static void ExportCreateBoundaryRace(EditorWorkspace w)
{
    var source = new MapDocument
    {
        name = "export create CAS source",
        rooms = new List<MapRoom>
        {
            new() { id = "first", name = "First", width = 8, height = 8 },
            new() { id = "second", name = "Second", x = 12, width = 8, height = 8 }
        }
    };
    IReadOnlyList<MapRoomJsonExporter.Entry> plan = MapRoomJsonExporter.Plan(source);
    string output = Path.Combine(w.Files.MapsPath, "export-create-cas");
    Directory.CreateDirectory(output);
    string first = Path.Combine(output, plan[0].FileName);
    string second = Path.Combine(output, plan[1].FileName);
    byte[] firstBaseline = Encoding.UTF8.GetBytes("first confirmed bytes before create race");
    byte[] secondBoundary = Encoding.UTF8.GetBytes("second concurrently created bytes");
    File.WriteAllBytes(first, firstBaseline);

    Throws<IOException>(() => MapRoomJsonExporter.ExportPlanned(plan, output, true, null, null,
        (index, destination) =>
        {
            if (index == 1) File.WriteAllBytes(destination, secondBoundary);
        }));

    Check(File.ReadAllBytes(first).SequenceEqual(firstBaseline),
        "A prior overwrite must roll back when a later absent target is concurrently created.");
    Check(File.ReadAllBytes(second).SequenceEqual(secondBoundary),
        "A target created after baseline capture must remain byte-for-byte intact.");
    Check(!Directory.EnumerateDirectories(output, ".map-room-export-*", SearchOption.TopDirectoryOnly).Any(),
        "The concurrent-create rollback must remove its staging directory.");
}
static void ExportAggregateLimit(EditorWorkspace w)
{
    var source = new MapDocument { name = "bounded export" };
    int sharedLength = checked((int)(MapRoomJsonExporter.MaximumAggregateUtf8Bytes
        / MapDocument.MaximumRoomCount + 1024));
    source.properties.Add(new MapProperty { key = "shared", value = new string('x', sharedLength) });
    for (int index = 0; index < MapDocument.MaximumRoomCount; index++)
        source.rooms.Add(new MapRoom { id = "room-" + index, name = "Room " + index, x = index, width = 8, height = 8 });
    w.Session.New(source);
    string before = Snapshot(w);
    long revision = w.Revision;
    bool canUndo = w.Session.CanUndo;

    InvalidOperationException error = Throws<InvalidOperationException>(() =>
        Send(w, "exportRooms", ("directory", "AggregateOverflow")));
    Check(error.Message.Contains("32", StringComparison.Ordinal)
        && Snapshot(w) == before && w.Revision == revision && w.Session.CanUndo == canUndo
        && !Directory.Exists(Path.Combine(w.Files.MapsPath, "AggregateOverflow")),
        "Oversized aggregate export work must fail before publication and preserve document, revision, and history.");
}
static void InvalidLoad(EditorWorkspace w)
{
    Send(w, "documentProperties", ("name", "keep this")); string before = Snapshot(w); bool undo = w.Session.CanUndo;
    string invalid = Path.Combine(w.Files.MapsPath, "broken.json"); File.WriteAllText(invalid, "{broken");
    Throws<InvalidDataException>(() => Send(w, "open", ("path", "broken.json"), ("discard", true)));
    Check(Snapshot(w) == before && w.Session.CanUndo == undo && w.Session.FilePath == null, "Failed open preserves original state.");
}
static void MapFileEncoding(EditorWorkspace w)
{
    var document = w.Session.Document.Clone(); document.name = "UTF-8 한글";
    string json = MapDocumentStore.Serialize(document, true);
    string directPath = w.Files.Map("direct-encoding.map.json");
    File.WriteAllBytes(directPath, new UTF8Encoding(false, true).GetBytes(json));
    Check(ProjectFiles.LoadStable(directPath).Document.name == document.name,
        "Stable load must accept ordinary UTF-8.");
    File.WriteAllBytes(directPath, WithPreamble(new UTF8Encoding(true, true), json));
    Check(ProjectFiles.LoadStable(directPath).Document.name == document.name,
        "Stable load must accept a UTF-8 BOM.");

    Encoding[] forbidden =
    {
        new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
        new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true)
    };
    foreach (Encoding encoding in forbidden)
    {
        File.WriteAllBytes(directPath, WithPreamble(encoding, json));
        Throws<InvalidDataException>(() => ProjectFiles.LoadStable(directPath));
    }

    Send(w, "save", ("path", "probe-encoding.map.json"));
    string probePath = w.Session.FilePath!;
    var bomUpdate = w.Session.Document.Clone(); bomUpdate.name = "UTF-8 BOM probe";
    File.WriteAllBytes(probePath, WithPreamble(new UTF8Encoding(true, true),
        MapDocumentStore.Serialize(bomUpdate, true)));
    bool reloaded = SpinWait.SpinUntil(() => { w.Tick(); return w.Session.Document.name == bomUpdate.name; },
        TimeSpan.FromSeconds(5));
    Check(reloaded && !w.Session.IsDirty, "Background probing must accept a UTF-8 BOM.");

    var forbiddenUpdate = w.Session.Document.Clone(); forbiddenUpdate.name = "UTF-16 probe must not load";
    File.WriteAllBytes(probePath, WithPreamble(new UnicodeEncoding(false, true, true),
        MapDocumentStore.Serialize(forbiddenUpdate, true)));
    bool rejected = SpinWait.SpinUntil(() =>
    {
        w.Tick();
        return w.Notice?.Contains("could not be reloaded", StringComparison.OrdinalIgnoreCase) == true;
    }, TimeSpan.FromSeconds(5));
    Check(rejected && w.Session.Document.name == bomUpdate.name && !w.Session.IsDirty,
        "Background probing must reject UTF-16 without replacing the clean in-memory map.");
}

static void CatalogFileEncoding(EditorWorkspace w)
{
    Directory.CreateDirectory(Path.GetDirectoryName(w.Files.CatalogPath)!);
    const string json = "{\"materials\":[],\"objects\":[],\"camera\":{\"ppu\":16,\"referenceWidth\":320,\"referenceHeight\":180,\"orthographicSize\":5.625,\"x\":0,\"y\":0}}";
    File.WriteAllBytes(w.Files.CatalogPath, WithPreamble(new UTF8Encoding(true, true), json));
    Check(w.Catalog.Refresh(), "Catalog loading must accept a UTF-8 BOM.");
    string accepted = w.Catalog.Data.GetRawText();

    File.WriteAllBytes(w.Files.CatalogPath, WithPreamble(new UnicodeEncoding(false, true, true), json));
    File.SetLastWriteTimeUtc(w.Files.CatalogPath, DateTime.UtcNow.AddSeconds(1));
    Throws<InvalidDataException>(() => w.Catalog.Refresh());
    Check(w.Catalog.Data.GetRawText() == accepted,
        "A UTF-16 catalog must be rejected without replacing the last valid catalog.");
}
static void DiskReload(EditorWorkspace w)
{
    Send(w, "save", ("path", "source.map.json")); long before = w.Revision;
    var external = w.Session.Document.Clone(); external.name = "updated externally"; MapDocumentStore.Save(w.Session.FilePath!, external);
    bool reloaded = SpinWait.SpinUntil(() => { w.Tick(); return w.Session.Document.name == "updated externally"; },
        TimeSpan.FromSeconds(5));
    Check(reloaded && !w.Session.IsDirty && w.Revision > before,
        "Clean view reloads an external file from the completed background snapshot and advertises fresh state.");
}
static void MetadataProbeOutsideGate()
{
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    int probeCount = 0;
    FixtureWithProbeHook(_ =>
    {
        if (Interlocked.Increment(ref probeCount) != 1) return;
        entered.Set();
        if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Probe fixture was not released.");
    }, w =>
    {
        try
        {
            Send(w, "save", ("path", "source.map.json"));
            var external = w.Session.Document.Clone(); external.name = "external snapshot";
            MapDocumentStore.Save(w.Session.FilePath!, external);

            lock (w.Gate) w.Tick();
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "The metadata change must start its background snapshot probe.");

            lock (w.Gate) Send(w, "documentProperties", ("name", "local edit after probe"));
        }
        finally { release.Set(); }

        bool noticed = SpinWait.SpinUntil(() =>
        {
            lock (w.Gate)
            {
                w.Tick();
                return w.Notice?.Contains("changed outside", StringComparison.OrdinalIgnoreCase) == true;
            }
        }, TimeSpan.FromSeconds(5));
        Check(noticed && w.Session.Document.name == "local edit after probe" && w.Session.IsDirty,
            "A probe captured before a command must become stale and must never replace the newer local document.");
    });
}
static void EqualMetadataDiskReload(EditorWorkspace w)
{
    Send(w, "documentProperties", ("name", "Original"));
    Send(w, "save", ("path", "source.map.json"));
    string path = w.Session.FilePath!;
    var observedField = typeof(EditorWorkspace).GetField("observedDiskFingerprint",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    bool observed = SpinWait.SpinUntil(() =>
    {
        w.Tick();
        ProjectFiles.DiskMetadata? disk = ProjectFiles.Metadata(path);
        return observedField.GetValue(w) is ProjectFiles.DiskFingerprint value
            && disk.HasValue && ProjectFiles.SameMetadata(value, disk.Value);
    }, TimeSpan.FromSeconds(5));
    Check(observed, "The fixture must establish the polling metadata/hash baseline.");
    var info = new FileInfo(path);
    DateTime timestamp = info.LastWriteTimeUtc; long length = info.Length;
    var external = w.Session.Document.Clone(); external.name = "External";
    string replacement = MapDocumentStore.Serialize(external, true);
    Check(System.Text.Encoding.UTF8.GetByteCount(replacement) == length,
        "The disk polling fixture must preserve byte length.");
    File.WriteAllText(path, replacement);
    File.SetLastWriteTimeUtc(path, timestamp);
    info.Refresh();
    Check(info.Length == length && info.LastWriteTimeUtc == timestamp,
        "The external rewrite must preserve the cached metadata exactly.");
    typeof(EditorWorkspace).GetField("nextDiskContentProbeAt",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(w, 0L);
    w.Tick(); // Queue the full content probe without hashing under Gate.
    bool reloaded = SpinWait.SpinUntil(() => { w.Tick(); return w.Session.Document.name == "External"; },
        TimeSpan.FromSeconds(5));
    Check(reloaded && !w.Session.IsDirty && w.Notice?.Contains("Reloaded", StringComparison.OrdinalIgnoreCase) == true,
        "The background content probe must eventually reload a clean equal-metadata rewrite.");
}
static void InvalidDiskReload(EditorWorkspace w)
{
    Send(w, "save", ("path", "source.map.json"));
    string before = Snapshot(w); string path = w.Session.FilePath!;
    File.WriteAllText(path, "{broken external map");
    bool rejectedMap = SpinWait.SpinUntil(() =>
    {
        w.Tick();
        return w.Notice?.Contains("could not be reloaded", StringComparison.OrdinalIgnoreCase) == true;
    }, TimeSpan.FromSeconds(5));
    Check(rejectedMap && Snapshot(w) == before && !w.Session.IsDirty
        && w.Notice?.Contains("could not be reloaded", StringComparison.OrdinalIgnoreCase) == true,
        "Malformed external bytes must leave the clean in-memory document intact and publish an explanation.");
    Send(w, "options", ("brushSize", 2));
    Check(w.Notice?.Contains("could not be reloaded", StringComparison.OrdinalIgnoreCase) == true,
        "A successful command must not erase a persistent malformed-disk warning.");
    long rejected = w.Revision;
    w.Tick();
    Check(w.Revision == rejected, "An unchanged rejected disk stamp must not be reparsed or republished every tick.");
    var valid = w.Session.Document.Clone(); valid.name = "valid retry";
    MapDocumentStore.Save(path, valid);
    bool repaired = SpinWait.SpinUntil(() => { w.Tick(); return w.Session.Document.name == "valid retry"; },
        TimeSpan.FromSeconds(5));
    Check(repaired && !w.Session.IsDirty,
        "A changed disk stamp must retry and accept a repaired external map.");
}
static void OversizedDiskReload(EditorWorkspace w)
{
    Send(w, "save", ("path", "source.map.json"));
    string before = Snapshot(w), path = w.Session.FilePath!;
    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        stream.SetLength(MapDocumentStore.MaximumFileBytes + 1);
    ProjectFiles.DiskFingerprint marker = ProjectFiles.Fingerprint(path)!.Value;
    Check(marker.Hash.StartsWith("OVERSIZED:", StringComparison.Ordinal),
        "An oversized external file must use a bounded marker instead of hashing its payload.");
    bool rejected = SpinWait.SpinUntil(() =>
    {
        w.Tick();
        return w.Notice?.Contains("could not be reloaded", StringComparison.OrdinalIgnoreCase) == true;
    }, TimeSpan.FromSeconds(5));
    Check(rejected && Snapshot(w) == before,
        "An oversized external map must preserve the in-memory document and report the rejected reload.");
    Throws<WorkspaceConflict>(() => Send(w, "save", ("path", "source.map.json"), ("overwrite", true)));
}
static void DiskDeleteAndReappear(EditorWorkspace w)
{
    Send(w, "save", ("path", "source.map.json"));
    string path = w.Session.FilePath!; File.Delete(path); long before = w.Revision;
    w.Tick();
    Check(w.Revision > before && w.Notice?.Contains("deleted", StringComparison.OrdinalIgnoreCase) == true,
        "A deleted backing file must produce a visible state revision.");
    Send(w, "options", ("brushSize", 2));
    Check(w.Notice?.Contains("deleted", StringComparison.OrdinalIgnoreCase) == true,
        "A successful command must not erase a persistent deleted-file warning.");
    long noticed = w.Revision;
    w.Tick();
    Check(w.Revision == noticed, "Polling a still-missing file must not repeat the same state change.");
    Throws<WorkspaceConflict>(() => Send(w, "save", ("path", "source.map.json"), ("overwrite", true)));
    Check(!File.Exists(path), "Saving over an externally deleted file requires an explicit new path or reopen.");
    var external = MapDocument.CreateDefault(); external.name = "reappeared externally"; MapDocumentStore.Save(path, external);
    bool reloaded = SpinWait.SpinUntil(() => { w.Tick(); return w.Session.Document.name == "reappeared externally"; },
        TimeSpan.FromSeconds(5));
    Check(reloaded && !w.Session.IsDirty,
        "A clean workspace must detect and reload a file that reappears after deletion.");
}
static void PartialState(EditorWorkspace w)
{
    JsonElement full = StateElement(w);
    Check(full.TryGetProperty("document", out _) && full.TryGetProperty("connections", out _) && full.TryGetProperty("catalog", out _),
        "A full state must include document, derived connections, and catalog.");
    long documentRevision = full.GetProperty("documentRevision").GetInt64();
    long catalogRevision = full.GetProperty("catalogRevision").GetInt64();
    Check(w.IsStateCurrent(w.Revision, w.InstanceId, documentRevision, catalogRevision)
        && !w.IsStateCurrent(w.Revision, w.InstanceId, documentRevision - 1, catalogRevision)
        && !w.IsStateCurrent(w.Revision, w.InstanceId, documentRevision, null),
        "A 204 response requires the overall revision and both independent payload tokens to match.");
    Send(w, "options", ("brushSize", 2));
    JsonElement metadataOnly = StateElement(w, false, false);
    Check(metadataOnly.GetProperty("documentRevision").GetInt64() == documentRevision
        && metadataOnly.GetProperty("catalogRevision").GetInt64() == catalogRevision
        && !metadataOnly.TryGetProperty("document", out _) && !metadataOnly.TryGetProperty("connections", out _)
        && !metadataOnly.TryGetProperty("catalog", out _),
        "Unchanged large sections must be absent while their revision tokens remain present.");
    JsonElement compact = StateElement(w, false, false, false);
    Check(!compact.TryGetProperty("selection", out _), "A compact response may omit an unchanged, potentially large selection.");
    Send(w, "documentProperties", ("name", "partial document"));
    JsonElement documentOnly = StateElement(w, true, false);
    Check(documentOnly.GetProperty("documentRevision").GetInt64() > documentRevision
        && documentOnly.TryGetProperty("document", out _) && documentOnly.TryGetProperty("connections", out _)
        && !documentOnly.TryGetProperty("catalog", out _),
        "A document-only response must carry the changed document and omit the catalog.");

    w.Session.Execute("selection fixture", document => document.rooms[0].objects.Add(new MapObject
        { id = "selected", definition = "missing", layer = MapLayer.Entities, x = 1, y = 1, width = 1, height = 1, scaleX = 1, scaleY = 1 }));
    Send(w, "options", ("layer", (int)MapLayer.Entities), ("tool", (int)MetroidvaniaStudioTool.Selection));
    Send(w, "objectClick", ("x", 1.5), ("y", 1.5));
    Check(StateElement(w, false, false).GetProperty("selection").GetProperty("objects").GetArrayLength() == 1,
        "The selection fixture must select its object.");
    Send(w, "options", ("layer", (int)MapLayer.ForegroundTiles));
    Check(StateElement(w, false, false).GetProperty("selection").GetProperty("objects").GetArrayLength() == 0,
        "A state response must synchronize selection before serializing object IDs.");

    Directory.CreateDirectory(Path.GetDirectoryName(w.Files.CatalogPath)!);
    File.WriteAllText(w.Files.CatalogPath,
        "{\"materials\":[],\"objects\":[],\"camera\":{\"ppu\":16,\"referenceWidth\":320,\"referenceHeight\":180,\"orthographicSize\":5.625}}");
    w.Tick();
    JsonElement catalogOnly = StateElement(w, false, true);
    Check(catalogOnly.GetProperty("catalogRevision").GetInt64() > catalogRevision
        && catalogOnly.TryGetProperty("catalog", out _) && !catalogOnly.TryGetProperty("document", out _),
        "Catalog refreshes must advance only their independent payload token.");
}
static void CleanRecovery(EditorWorkspace w)
{
    Send(w, "save", ("path", "source.map.json"));
    string recovery = w.Files.RecoveryPath;
    Send(w, "documentProperties", ("name", "temporary edit"));
    w.FlushRecovery();
    Check(File.Exists(recovery), "A dirty edit must create a recovery snapshot.");
    Send(w, "undo");
    w.FlushRecovery();
    Check(!w.Session.IsDirty && !File.Exists(recovery), "Undoing to the saved revision must delete stale recovery.");

    MapDocumentStore.SaveValidatedSnapshot(recovery, MapDocumentStore.Serialize(MapDocument.CreateDefault()));
    var external = w.Session.Document.Clone(); external.name = "clean external reload";
    MapDocumentStore.Save(w.Session.FilePath!, external);
    bool reloaded = SpinWait.SpinUntil(() => { w.Tick(); return w.Session.Document.name == "clean external reload"; },
        TimeSpan.FromSeconds(5));
    w.FlushRecovery();
    Check(reloaded && !File.Exists(recovery),
        "A clean external reload must remove a stale recovery snapshot.");
    var restarted = new EditorWorkspace(new ProjectFiles(w.Files.ProjectPath));
    try
    {
        Check(restarted.Session.FilePath != null && !restarted.Session.IsDirty
            && restarted.Session.Document.name == "clean external reload",
            "Restart must open the named clean file instead of stale recovery data.");
    }
    finally { restarted.StopAutoExports(); restarted.Canvas.Dispose(); }
}
static void CorruptRecoveryFallback(EditorWorkspace w)
{
    Send(w, "documentProperties", ("name", "saved fallback"));
    Send(w, "save", ("path", "fallback.map.json"));
    w.FlushRecovery();
    Directory.CreateDirectory(Path.GetDirectoryName(w.Files.RecoveryPath)!);
    File.WriteAllText(w.Files.RecoveryPath, "{broken recovery");
    var restarted = new EditorWorkspace(new ProjectFiles(w.Files.ProjectPath));
    try
    {
        Check(restarted.Session.Document.name == "saved fallback" && restarted.Session.FilePath != null && !restarted.Session.IsDirty,
            "An invalid recovery snapshot must not prevent startup or replace the saved-map fallback.");
        Check(!File.Exists(w.Files.RecoveryPath)
            && Directory.EnumerateFiles(Path.GetDirectoryName(w.Files.RecoveryPath)!, "Workspace.corrupt-*.map.json").Any()
            && restarted.Notice?.Contains("invalid recovery", StringComparison.OrdinalIgnoreCase) == true,
            "Corrupt recovery bytes must be quarantined and explained instead of deleted.");
    }
    finally { restarted.StopAutoExports(); restarted.Canvas.Dispose(); }
}
static void ResizeAndErase(EditorWorkspace w)
{
    string first = w.Canvas.Room.id; Send(w, "roomAdd", ("x", 45), ("y", 0), ("width", 10), ("height", 10)); string right = w.Canvas.Room.id;
    Send(w, "roomResize", ("id", first), ("x", 0), ("y", 0), ("width", 44), ("height", 24));
    Check(w.Session.Document.rooms.Single(r => r.id == right).x == 49, "Neighbor gap survives API resize.");
    Send(w, "selectRoom", ("id", first)); Send(w, "begin", ("x", 1), ("y", 1)); Send(w, "end", ("x", 5), ("y", 1));
    Send(w, "options", ("tool", (int)MetroidvaniaStudioTool.Rectangle));
    Send(w, "begin", ("x", 2), ("y", 1), ("erase", true)); Send(w, "end", ("x", 4), ("y", 1));
    Check(w.Canvas.Room.foreground.Select(c => c.x).Order().SequenceEqual(new[] { 1, 5 }), "Right erase is continuous regardless of active shape tool.");
}
static void Paths() => Fixture(w =>
{
    foreach (string path in new[] { "../outside.json", "../../outside.json", Path.GetFullPath("outside.json"), "foo.json:stream", "../MapsSibling/file.json" })
        Throws<ArgumentException>(() => w.Files.Map(path));
    foreach (string path in new[] { ".Recovery/Workspace.map.json", ".recovery/user-save.json", "./.Recovery/nested.json" })
        Throws<ArgumentException>(() => w.Files.Map(path));
    Throws<ArgumentException>(() => w.Files.ExportDirectory(".RECOVERY/exports"));
    Throws<ArgumentException>(() => w.Files.Asset("Textures/../Settings/secret.png"));
    Check(w.Files.Map("nested/map.json").StartsWith(w.Files.MapsPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Nested project maps allowed.");

    string fingerprintPath = w.Files.Map("fingerprint.json");
    MapDocumentStore.Save(fingerprintPath, MapDocument.CreateDefault());
    ProjectFiles.DiskFingerprint first = ProjectFiles.Fingerprint(fingerprintPath)!.Value;
    ProjectFiles.DiskFingerprint unchanged = ProjectFiles.Fingerprint(fingerprintPath, first)!.Value;
    Check(object.ReferenceEquals(first.Hash, unchanged.Hash), "An unchanged metadata stamp must reuse its cached content hash.");
    File.SetLastWriteTimeUtc(fingerprintPath, File.GetLastWriteTimeUtc(fingerprintPath) + TimeSpan.FromSeconds(2));
    ProjectFiles.DiskFingerprint touched = ProjectFiles.Fingerprint(fingerprintPath, unchanged)!.Value;
    Check(ProjectFiles.SameContent(first, touched) && touched.LastWriteUtcTicks != first.LastWriteUtcTicks,
        "A metadata-only touch must refresh its stamp without inventing a content conflict.");
});
static void PortablePaths(EditorWorkspace w)
{
    Check(w.Files.Map(@"nested\upper.JSON") == Path.Combine(w.Files.MapsPath, "nested", "upper.JSON"),
        "Backslash input must become directory separators rather than literal Unix filename characters.");
    string rootedShare = new string((char)92, 2) + string.Join((char)92, "host", "share", "map.json");
    foreach (string path in new[] { @"..\outside.json", @"\rooted.json", rootedShare })
        Throws<ArgumentException>(() => w.Files.Map(path));
    Send(w, "save", ("path", @"nested\upper.JSON"));
    Check(w.Files.List().Contains("nested/upper.JSON"),
        "Files accepted by Save must remain discoverable even with an uppercase extension.");
    Check(w.Files.Relative(w.Session.FilePath!) == "nested/upper.JSON", "Saved paths use portable separators.");

    if (OperatingSystem.IsWindows()) return;
    Throws<ArgumentException>(() => w.Files.Map("../maps/outside.json"));
    Check(AutoRoomExporter.MapKey("map.json") != AutoRoomExporter.MapKey("Map.json")
        && AutoRoomExporter.MapKey("\u00e9.json") != AutoRoomExporter.MapKey("e\u0301.json"),
        "Unix export namespaces must not merge case-distinct or Unicode-distinct filenames.");

    // macOS supports both case-sensitive and case-insensitive volumes. Verify
    // independent Save As baselines only when this actual volume permits them.
    Send(w, "save", ("path", "case.map.json")); w.FlushAutoExports();
    string firstExports = AutoDirectory(w);
    if (File.Exists(w.Files.Map("CASE.map.json"))) return;
    string original = File.ReadAllText(w.Files.Map("case.map.json"));
    Send(w, "roomProperties", ("id", w.Canvas.Room.id), ("name", "second-map"));
    Send(w, "save", ("path", "CASE.map.json")); w.FlushAutoExports();
    Check(File.ReadAllText(w.Files.Map("case.map.json")) == original && AutoDirectory(w) != firstExports,
        "Case-distinct Save As must preserve the first source and keep separate automatic exports.");
    Check(w.Files.List().Contains("case.map.json") && w.Files.List().Contains("CASE.map.json"),
        "Both case-distinct saved maps must remain discoverable.");
}
static void StartPropertyFixture(EditorWorkspace w, bool lockedNeighbor = false)
{
    var target = new MapRoom { id = "target", name = "before", width = 10, height = 10 };
    target.foreground.Add(new MapCell { x = 0, y = 0, shape = TileShape.BottomLeft });
    target.foreground.Add(new MapCell { x = 9, y = 9, shape = TileShape.TopRight });
    target.background.Add(new MapCell { x = 8, y = 8, material = "background-material" });
    target.objects.Add(new MapObject { id = "object", x = 2.5f, y = 3.5f,
        nodes = new List<MetroidvaniaStudio.Primitives.Vector2> { new(1, 1), new(8, 7) } });
    target.properties.Add(new MapProperty { key = "old", value = "kept" });
    var neighbor = new MapRoom { id = "neighbor", name = "neighbor", x = 15, width = 10, height = 10, locked = lockedNeighbor };
    neighbor.foreground.Add(new MapCell { x = 3, y = 4 });
    w.Session.New(new MapDocument { rooms = new List<MapRoom> { target, neighbor } });
    w.Canvas.SelectRoom(target.id);
}
static void RoomPropertyPosition(EditorWorkspace w)
{
    StartPropertyFixture(w); string before = Snapshot(w);
    Send(w, "roomProperties", ("id", "target"), ("x", -100), ("y", 75));
    var room = w.Canvas.Room;
    Check(room.x == -100 && room.y == 75 && room.width == 10 && room.height == 10, "Property coordinates translate the room.");
    Check(room.foreground.Count == 2 && room.foreground.Any(c => c.x == 0 && c.y == 0) && room.foreground.Any(c => c.x == 9 && c.y == 9)
        && room.background.Single().x == 8, "Whole local tile contents survive positive and negative origin changes.");
    Check(room.objects.Single().x == 2.5f && room.objects.Single().y == 3.5f
        && room.objects.Single().nodes[1] == new MetroidvaniaStudio.Primitives.Vector2(8, 7), "Local objects and nodes move with their room.");
    Check(w.Session.Document.rooms.Single(r => r.id == "neighbor").x == 15, "Explicit coordinate translation does not resize other rooms.");
    Send(w, "undo"); Check(Snapshot(w) == before && !w.Session.CanUndo, "Position change is one undo.");
}
static void RoomPropertyResize(EditorWorkspace w)
{
    StartPropertyFixture(w); string before = Snapshot(w);
    Send(w, "roomProperties", ("id", "target"), ("width", 14), ("name", "expanded"),
        ("properties", new[] { new { key = "new", value = "metadata" } }));
    var room = w.Canvas.Room; var neighbor = w.Session.Document.rooms.Single(r => r.id == "neighbor");
    Check(room.width == 14 && neighbor.x == 19 && neighbor.x - (room.x + room.width) == 5, "Width expansion preserves the five-tile neighbor gap.");
    Check(room.name == "expanded" && room.properties.Single().key == "new" && neighbor.foreground.Single().x == 3, "Metadata and neighbor local data remain consistent.");
    string after = Snapshot(w); Send(w, "undo");
    Check(Snapshot(w) == before && !w.Session.CanUndo, "Bounds, neighbors and metadata roll back together in one undo.");
    Send(w, "redo"); Check(Snapshot(w) == after, "One redo restores the whole property form.");
}
static void RoomPropertyLockedFailure(EditorWorkspace w)
{
    StartPropertyFixture(w, true); string before = Snapshot(w); long revision = w.Revision;
    Throws<InvalidOperationException>(() => Send(w, "roomProperties", ("id", "target"), ("width", 14),
        ("x", -20), ("name", "must not remain"), ("visible", false),
        ("properties", new[] { new { key = "replacement", value = "must not remain" } })));
    Check(Snapshot(w) == before && !w.Session.CanUndo && !w.Session.IsEditing, "Locked neighbor failure must preserve all metadata and bounds without a transaction.");
    Check(w.Revision == revision, "Detached validation failure must not change the live workspace revision.");
}
static void RoomPropertyMoveResize(EditorWorkspace w)
{
    StartPropertyFixture(w); string before = Snapshot(w);
    Send(w, "roomProperties", ("id", "target"), ("x", 10), ("width", 14));
    MapRoom target = w.Session.Document.rooms.Single(room => room.id == "target");
    MapRoom neighbor = w.Session.Document.rooms.Single(room => room.id == "neighbor");
    Check(target.x == 10 && target.width == 14 && neighbor.x == 29
        && neighbor.x - (target.x + target.width) == 5,
        "The layout planner must preserve the original gap around the complete final target rectangle.");
    Check(target.foreground.Any(cell => cell.x == 0) && target.foreground.Any(cell => cell.x == 9)
        && target.objects.Single().x == 2.5f,
        "Inspector coordinate changes must retain target contents at their room-local positions.");
    Send(w, "undo");
    Check(Snapshot(w) == before && !w.Session.CanUndo, "Combined bounds and propagated neighbors must be one Undo.");
}
static void RoomTargetAtomicity(EditorWorkspace w)
{
    string first = w.Canvas.Room.id;
    Send(w, "roomAdd", ("x", 60), ("y", 0), ("width", 10), ("height", 10), ("name", "locked target"));
    string target = w.Canvas.Room.id;
    Send(w, "roomProperties", ("id", target), ("locked", true));
    Send(w, "selectRoom", ("id", first));
    string before = Snapshot(w); long revision = w.Revision;
    Throws<ArgumentException>(() => Send(w, "selectRoom", ("id", "missing-room")));
    Check(w.Canvas.ActiveRoomId == first && Snapshot(w) == before && w.Revision == revision,
        "An unknown room selection must preserve active room, document, and revision.");
    Throws<InvalidOperationException>(() => Send(w, "roomMove", ("id", target), ("dx", "not-an-integer"), ("dy", 0)));
    Check(w.Canvas.ActiveRoomId == first && Snapshot(w) == before && w.Revision == revision,
        "Malformed movement values must be parsed before changing room selection.");
    Throws<InvalidOperationException>(() => Send(w, "roomMove", ("id", target), ("dx", 2), ("dy", 0), ("snap", false)));
    Check(w.Canvas.ActiveRoomId == first && Snapshot(w) == before && w.Revision == revision,
        "A locked movement target must fail before changing selection or document state.");
    Throws<InvalidOperationException>(() => Send(w, "roomDelete", ("id", target)));
    Check(w.Canvas.ActiveRoomId == first && Snapshot(w) == before && w.Revision == revision,
        "A locked delete target must fail before changing selection or document state.");

    Send(w, "roomProperties", ("id", target), ("locked", false));
    Send(w, "selectRoom", ("id", first));
    Send(w, "roomDelete", ("id", target));
    Check(w.Session.Document.rooms.Count == 1 && w.Session.Document.rooms[0].id == first,
        "roomDelete must delete its explicit id rather than whichever room was selected when the request arrived.");
}
static void CameraValidation(EditorWorkspace w)
{
    Directory.CreateDirectory(Path.GetDirectoryName(w.Files.CatalogPath)!);
    string catalogBefore = w.Catalog.Data.GetRawText();
    File.WriteAllText(w.Files.CatalogPath,
        "{\"materials\":[],\"objects\":[],\"camera\":{\"ppu\":0,\"referenceWidth\":320,\"referenceHeight\":180,\"orthographicSize\":5.625}}");
    Throws<InvalidDataException>(() => w.Catalog.Refresh());
    Check(w.Catalog.Data.GetRawText() == catalogBefore && w.Catalog.Materials.Count == 0 && w.Catalog.Objects.Count == 0,
        "An invalid catalog camera must not publish a partial catalog.");
    var restarted = new EditorWorkspace(new ProjectFiles(w.Files.ProjectPath));
    try
    {
        Check(restarted.Catalog.Data.GetProperty("camera").GetProperty("ppu").GetInt32() == 16
            && restarted.Notice?.Contains("built-in catalog", StringComparison.OrdinalIgnoreCase) == true,
            "An invalid persisted camera catalog must fall back safely instead of preventing server startup.");
    }
    finally { restarted.StopAutoExports(); restarted.Canvas.Dispose(); }

    File.WriteAllText(w.Files.CatalogPath,
        "{\"materials\":{},\"objects\":[],\"camera\":{\"ppu\":16,\"referenceWidth\":320,\"referenceHeight\":180,\"orthographicSize\":5.625}}");
    Throws<InvalidDataException>(() => w.Catalog.Refresh());
    var malformedRestart = new EditorWorkspace(new ProjectFiles(w.Files.ProjectPath));
    try
    {
        Check(malformedRestart.Catalog.Data.GetProperty("camera").GetProperty("ppu").GetInt32() == 16
            && malformedRestart.Notice?.Contains("built-in catalog", StringComparison.OrdinalIgnoreCase) == true,
            "A wrong catalog collection type must use the built-in fallback instead of stopping server startup.");
    }
    finally { malformedRestart.StopAutoExports(); malformedRestart.Canvas.Dispose(); }

    string validCatalogBefore = w.Catalog.Data.GetRawText();
    File.WriteAllText(w.Files.CatalogPath,
        "{\"materials\":[{\"id\":\"bad-theme\",\"name\":\"Bad\",\"themeId\":\"bad\",\"color\":\"not-a-color\",\"sprites\":[]}],\"objects\":[],\"camera\":{\"ppu\":16,\"referenceWidth\":320,\"referenceHeight\":180,\"orthographicSize\":5.625}}");
    Throws<InvalidDataException>(() => w.Catalog.Refresh());
    Check(w.Catalog.Data.GetRawText() == validCatalogBefore && !w.Catalog.Materials.ContainsKey("bad-theme"),
        "An invalid themed color must not publish a catalog the web client would interpret differently.");

    File.WriteAllText(w.Files.CatalogPath,
        "{\"materials\":[{\"id\":\"named-theme\",\"name\":\"Named\",\"themeId\":\"named\",\"color\":\"red\",\"sprites\":[]}],\"objects\":[],\"camera\":{\"ppu\":16,\"referenceWidth\":320,\"referenceHeight\":180,\"orthographicSize\":5.625}}");
    Throws<InvalidDataException>(() => w.Catalog.Refresh());
    Check(w.Catalog.Data.GetRawText() == validCatalogBefore && !w.Catalog.Materials.ContainsKey("named-theme"),
        "Catalog colors must use the portable hex subset rendered identically by exact and overview paths.");

    File.WriteAllText(w.Files.CatalogPath,
        "{\"materials\":[{\"id\":\"incomplete\"}],\"objects\":[],\"camera\":{\"ppu\":16,\"referenceWidth\":320,\"referenceHeight\":180,\"orthographicSize\":5.625}}");
    Throws<InvalidDataException>(() => w.Catalog.Refresh());
    Check(w.Catalog.Data.GetRawText() == validCatalogBefore,
        "A catalog missing fields required by the browser must not be published.");

    using (var oversized = new FileStream(w.Files.CatalogPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        oversized.SetLength(Catalog.MaximumCatalogBytes + 1);
    Throws<InvalidDataException>(() => w.Catalog.Refresh());
    Check(w.Catalog.Data.GetRawText() == validCatalogBefore,
        "An oversized catalog must be rejected before reading or replacing the published definitions.");

    File.WriteAllText(w.Files.CatalogPath, """
        {"materials":[],"objects":[{"id":"node-bomb","name":"Node bomb","color":"#808080","layer":2,
        "width":1,"height":1,"minimumWidth":1,"minimumHeight":1,"placement":2,
        "minimumNodes":__MAX_NODES__,"maximumNodes":-1,"resizable":true,"rotatable":true,"flippable":true,
        "sprite":null,"properties":[]}],"camera":{"ppu":16,"referenceWidth":320,"referenceHeight":180,"orthographicSize":5.625}}
        """.Replace("__MAX_NODES__", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    Throws<InvalidDataException>(() => w.Catalog.Refresh());
    Check(!w.Catalog.Objects.ContainsKey("node-bomb"),
        "A small catalog must not encode a placement that allocates an unbounded initial node list.");

    File.WriteAllText(w.Files.CatalogPath,
        "{\"materials\":[{\"id\":\"temporary\",\"name\":\"Temporary\",\"color\":\"#808080\",\"themeId\":\"\",\"sprites\":[]}],\"objects\":[],\"camera\":{\"ppu\":16,\"referenceWidth\":320,\"referenceHeight\":180,\"orthographicSize\":5.625}}");
    Check(w.Catalog.Refresh() && w.Catalog.Materials.ContainsKey("temporary"), "The deletion fixture catalog must load.");
    File.Delete(w.Files.CatalogPath);
    Check(w.Catalog.Refresh() && w.Catalog.Materials.Count == 0 && w.Catalog.Objects.Count == 0,
        "Deleting EditorCatalog.json must replace stale definitions with the built-in catalog.");


}
static void PersistentHealthNotices(EditorWorkspace w)
{
    Directory.CreateDirectory(Path.GetDirectoryName(w.Files.CatalogPath)!);
    File.WriteAllText(w.Files.CatalogPath, "{invalid catalog");
    w.Tick();
    Check(w.Notice?.Contains("catalog", StringComparison.OrdinalIgnoreCase) == true,
        "An invalid catalog must publish a persistent health warning.");
    Send(w, "options", ("brushSize", 2));
    Check(w.Notice?.Contains("catalog", StringComparison.OrdinalIgnoreCase) == true,
        "Ordinary commands must not clear the persistent catalog warning.");
    File.Delete(w.Files.CatalogPath);
    w.Tick();
    Check(w.Notice == null, "Removing the rejected catalog must clear its health warning and keep the built-in catalog.");
}
static void StaleInstance(EditorWorkspace w)
{
    string before = Snapshot(w); long revision = w.Revision;
    using (var missing = JsonDocument.Parse("{\"action\":\"undo\",\"clientId\":\"A\",\"commandId\":\"missing-instance\"}"))
        Throws<WorkspaceConflict>(() => w.Command(missing.RootElement));
    using (var badRevision = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        action = "undo", clientId = "A", commandId = "bad-revision", expectedInstanceId = w.InstanceId, expectedRevision = "current"
    }))) Throws<ArgumentException>(() => w.Command(badRevision.RootElement));
    using (var missingClient = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        action = "undo", commandId = "missing-client", expectedInstanceId = w.InstanceId, expectedRevision = w.Revision
    }))) Throws<ArgumentException>(() => w.Command(missingClient.RootElement));
    using (var oversizedClient = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        action = "undo", clientId = new string('x', 129), commandId = "oversized-client",
        expectedInstanceId = w.InstanceId, expectedRevision = w.Revision
    }))) Throws<ArgumentException>(() => w.Command(oversizedClient.RootElement));
    using (var missingCommand = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        action = "undo", clientId = "A", expectedInstanceId = w.InstanceId, expectedRevision = w.Revision
    }))) Throws<ArgumentException>(() => w.Command(missingCommand.RootElement));
    Check(Snapshot(w) == before && w.Revision == revision,
        "Missing or malformed concurrency credentials must reject a command atomically.");
    Throws<WorkspaceConflict>(() => Send(w, "documentProperties", ("name", "old process write"),
        ("expectedRevision", revision), ("expectedInstanceId", "old-process-instance")));
    Check(Snapshot(w) == before && !w.Session.CanUndo && w.Revision == revision, "Restarted server rejects stale process writes even if revision numbers match.");
    Send(w, "documentProperties", ("name", "current process write"));
    Check(w.Session.Document.name == "current process write", "Current process identity can still edit.");
}
static void IdempotentCommand(EditorWorkspace w)
{
    string before = Snapshot(w);
    var request = new
    {
        action = "documentProperties", name = "exactly once", clientId = "retry-client", commandId = "fixed-command-id",
        expectedInstanceId = w.InstanceId, expectedRevision = w.Revision
    };
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(request));
    Check(w.Command(json.RootElement), "The first delivery must execute.");
    string after = Snapshot(w); long revision = w.Revision;
    Check(!w.Command(json.RootElement), "A completed command ID must be acknowledged without executing twice.");
    Check(Snapshot(w) == after && w.Revision == revision && w.Session.CanUndo,
        "A response-loss retry must preserve the first result, revision, and single Undo entry.");
    Send(w, "undo");
    Check(Snapshot(w) == before && !w.Session.CanUndo,
        "One Undo must revert the once-only command after an idempotent retry.");
}
static void IdempotentPayloadMismatch(EditorWorkspace w)
{
    var original = new
    {
        action = "documentProperties", name = "first payload", clientId = "retry-client", commandId = "shared-command-id",
        expectedInstanceId = w.InstanceId, expectedRevision = w.Revision
    };
    using var first = JsonDocument.Parse(JsonSerializer.Serialize(original));
    Check(w.Command(first.RootElement), "The original command must execute.");
    string after = Snapshot(w); long revision = w.Revision;
    using var different = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        action = "documentProperties", name = "second payload", clientId = "retry-client", commandId = "shared-command-id",
        expectedInstanceId = w.InstanceId, expectedRevision = original.expectedRevision
    }));
    WorkspaceConflict error = Throws<WorkspaceConflict>(() => w.Command(different.RootElement));
    Check(error.Message.Contains("different command payload", StringComparison.OrdinalIgnoreCase),
        "A reused idempotency key must return an explicit payload mismatch conflict.");
    Check(Snapshot(w) == after && w.Revision == revision && w.Session.Document.name == "first payload",
        "A payload mismatch must preserve the completed result and its revision.");
}
static void EqualMetadataCatalogRewrite(EditorWorkspace w)
{
    Directory.CreateDirectory(Path.GetDirectoryName(w.Files.CatalogPath)!);
    const string first = "{\"materials\":[{\"id\":\"alpha\",\"name\":\"Alpha\",\"color\":\"#808080\",\"themeId\":\"\",\"sprites\":[]}],\"objects\":[],\"camera\":{\"ppu\":16,\"referenceWidth\":320,\"referenceHeight\":180,\"orthographicSize\":5.625}}";
    const string second = "{\"materials\":[{\"id\":\"bravo\",\"name\":\"Bravo\",\"color\":\"#808080\",\"themeId\":\"\",\"sprites\":[]}],\"objects\":[],\"camera\":{\"ppu\":16,\"referenceWidth\":320,\"referenceHeight\":180,\"orthographicSize\":5.625}}";
    Check(System.Text.Encoding.UTF8.GetByteCount(first) == System.Text.Encoding.UTF8.GetByteCount(second),
        "The catalog replacement fixture must preserve byte length.");
    File.WriteAllText(w.Files.CatalogPath, first);
    Check(w.Catalog.Refresh() && w.Catalog.Materials.ContainsKey("alpha"), "The original catalog must load.");
    var original = new FileInfo(w.Files.CatalogPath);
    DateTime timestamp = original.LastWriteTimeUtc; long length = original.Length;
    File.WriteAllText(w.Files.CatalogPath, second);
    File.SetLastWriteTimeUtc(w.Files.CatalogPath, timestamp);
    var replacement = new FileInfo(w.Files.CatalogPath);
    Check(replacement.LastWriteTimeUtc == timestamp && replacement.Length == length,
        "The replacement must retain the exact metadata used by the fast path.");
    Check(!w.Catalog.Refresh(), "An equal-metadata check must be queued without blocking the workspace Tick.");
    bool refreshed = SpinWait.SpinUntil(() => w.Catalog.Refresh(), TimeSpan.FromSeconds(5));
    Check(refreshed && !w.Catalog.Materials.ContainsKey("alpha") && w.Catalog.Materials.ContainsKey("bravo"),
        "The background content fingerprint must eventually publish an equal-metadata rewrite.");
}
static void CatalogComplexityLimits(EditorWorkspace w)
{
    Directory.CreateDirectory(Path.GetDirectoryName(w.Files.CatalogPath)!);
    const string camera = "\"camera\":{\"ppu\":16,\"referenceWidth\":320,\"referenceHeight\":180,\"orthographicSize\":5.625}";
    const string valid = "{\"materials\":[{\"id\":\"alpha\",\"name\":\"Alpha\",\"color\":\"#808080\",\"themeId\":\"\",\"sprites\":[]}],\"objects\":[]," + camera + "}";
    DateTime timestamp = DateTime.UtcNow.AddMinutes(-10);
    File.WriteAllText(w.Files.CatalogPath, valid);
    File.SetLastWriteTimeUtc(w.Files.CatalogPath, timestamp);
    Check(w.Catalog.Refresh() && w.Catalog.Materials.ContainsKey("alpha"), "The catalog limit baseline must load.");
    string baseline = w.Catalog.Data.GetRawText(); int version = 0;

    void Reject(string json, string expectedMessage)
    {
        File.WriteAllText(w.Files.CatalogPath, json);
        File.SetLastWriteTimeUtc(w.Files.CatalogPath, timestamp.AddSeconds(++version));
        InvalidDataException error = Throws<InvalidDataException>(() => w.Catalog.Refresh());
        Check(error.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase),
            "Catalog limit rejection did not identify " + expectedMessage + ": " + error.Message);
        Check(w.Catalog.Data.GetRawText() == baseline && w.Catalog.Materials.ContainsKey("alpha"),
            "Rejecting a catalog limit violation must preserve the last complete catalog atomically.");
    }
    static string Entries(int count, string entry) => string.Join(',', Enumerable.Repeat(entry, count));
    static string Root(string materials, string objects, string cameraJson) =>
        "{\"materials\":[" + materials + "],\"objects\":[" + objects + "]," + cameraJson + "}";

    Reject(Root(Entries(Catalog.MaximumMaterialDefinitions + 1, "{}"), "", camera), "Catalog materials");
    Reject(Root("", Entries(Catalog.MaximumObjectDefinitions + 1, "{}"), camera), "Catalog objects");
    Reject(Root("{\"sprites\":[" + Entries(Catalog.MaximumSpriteEntriesPerMaterial + 1, "{}") + "]}", "", camera),
        "material sprites");
    Reject(Root("", "{\"properties\":[" + Entries(Catalog.MaximumFieldsPerObjectDefinition + 1, "{}") + "]}", camera),
        "object properties");
    Reject(Root("", "{\"properties\":[{\"choices\":[" + Entries(Catalog.MaximumChoicesPerField + 1, "\"\"") + "]}]}", camera),
        "property choices");

    string maximumSprites = Entries(Catalog.MaximumSpriteEntriesPerMaterial, "{}");
    string material = "{\"sprites\":[" + maximumSprites + "]}";
    int materialCount = Catalog.MaximumCatalogWork / (Catalog.MaximumSpriteEntriesPerMaterial + 1) + 1;
    Reject(Root(Entries(materialCount, material), "", camera), "processing budget");
}
static void Recovery(EditorWorkspace w)
{
    Send(w, "documentProperties", ("name", "recover me")); string before = Snapshot(w);
    w.FlushRecovery();
    var restarted = new EditorWorkspace(new ProjectFiles(w.Files.ProjectPath));
    try { Check(Snapshot(restarted) == before && restarted.Session.IsDirty && restarted.Session.FilePath == null, "Unsaved recovery retained without masquerading as saved map."); }
    finally { restarted.StopAutoExports(); restarted.Canvas.Dispose(); }
}
static void StaleRecoveryAfterSave()
{
    byte[]? saveIntent = null;
    FixtureWithPublishHook(destination =>
    {
        if (Path.GetFileName(destination) != "latest.map.json") return;
        string recovery = Path.Combine(Path.GetDirectoryName(destination)!, ".Recovery", "Workspace.map.json");
        saveIntent = File.ReadAllBytes(recovery);
    }, w =>
    {
        Send(w, "documentProperties", ("name", "older dirty A"));
        w.FlushRecovery();
        Send(w, "documentProperties", ("name", "latest saved B"));
        Send(w, "save", ("path", "latest.map.json"));
        w.FlushRecovery();
        Check(saveIntent != null, "The save intent must already exist at the target publication boundary.");
        // Restore the exact pre-publication bytes to simulate a crash before
        // recovery cleanup. Open-file deletion rules differ between platforms.
        Directory.CreateDirectory(Path.GetDirectoryName(w.Files.RecoveryPath)!);
        File.WriteAllBytes(w.Files.RecoveryPath, saveIntent!);

        ProjectFiles.RecoveryMap marker = w.Files.LoadRecovery();
        Check(!marker.IsDirty && marker.Document.name == "latest saved B" && marker.SavedPath == "latest.map.json",
            "The pre-publication marker must replace the older dirty recovery with the exact document being saved.");
        var restarted = new EditorWorkspace(new ProjectFiles(w.Files.ProjectPath));
        try
        {
            Check(restarted.Session.Document.name == "latest saved B" && !restarted.Session.IsDirty
                && restarted.Session.FilePath == w.Files.Map("latest.map.json"),
                "A crash-window clean marker must validate and open B instead of the older A snapshot.");
        }
        finally { restarted.StopAutoExports(); restarted.Canvas.Dispose(); }
    });
}
static void DirtyRecoveryAfterSave(EditorWorkspace w)
{
    Send(w, "documentProperties", ("name", "saved B"));
    Send(w, "save", ("path", "source.map.json"));
    w.FlushRecovery();
    Send(w, "documentProperties", ("name", "unsaved C"));
    w.FlushRecovery();
    ProjectFiles.RecoveryMap recovery = w.Files.LoadRecovery();
    Check(recovery.IsDirty && recovery.Document.name == "unsaved C" && recovery.SavedPath == "source.map.json",
        "A newer dirty envelope must replace every clean save marker in the single recovery path.");
    var restarted = new EditorWorkspace(new ProjectFiles(w.Files.ProjectPath));
    try
    {
        Check(restarted.Session.Document.name == "unsaved C" && restarted.Session.IsDirty
            && restarted.Session.FilePath == w.Files.Map("source.map.json"),
            "A valid saved baseline must reattach C as dirty edits instead of hiding it behind B.");
    }
    finally { restarted.StopAutoExports(); restarted.Canvas.Dispose(); }
}
static void LegacyRecovery(EditorWorkspace w)
{
    var legacy = MapDocument.CreateDefault(); legacy.name = "legacy raw recovery";
    Directory.CreateDirectory(Path.GetDirectoryName(w.Files.RecoveryPath)!);
    MapDocumentStore.SaveValidatedSnapshot(w.Files.RecoveryPath, MapDocumentStore.Serialize(legacy));
    var restarted = new EditorWorkspace(new ProjectFiles(w.Files.ProjectPath));
    try
    {
        Check(restarted.Session.Document.name == "legacy raw recovery" && restarted.Session.IsDirty
            && restarted.Session.FilePath == null,
            "A pre-envelope raw MapDocument recovery file must remain readable as an unsaved document.");
    }
    finally { restarted.StopAutoExports(); restarted.Canvas.Dispose(); }
}
static void UnthemedMaterial(EditorWorkspace w)
{
    Directory.CreateDirectory(Path.GetDirectoryName(w.Files.CatalogPath)!);
    File.WriteAllText(w.Files.CatalogPath, """
        {"materials":[{"id":"plain","name":"Plain material","color":"#808080","themeId":"","sprites":[]}],"objects":[],"camera":{"ppu":16,"referenceWidth":320,"referenceHeight":180,"orthographicSize":5.625}}
        """);
    w.Catalog.Refresh();
    Check(!w.Catalog.Materials["plain"].HasTheme, "Display swatch must not invent authored terrain theme metadata.");
    Send(w, "options", ("material", "plain")); Send(w, "begin", ("x", 1), ("y", 1)); Send(w, "end", ("x", 1), ("y", 1));
    Check(!MapRoomTheme.TryGetColor(w.Canvas.Room, out _), "Plain material paint must preserve unthemed room state.");
}

static object[] Points(params (object x, object y)[] points) => points.Select(point => (object)new { x = point.x, y = point.y }).ToArray();
static void TileGesture(EditorWorkspace w, string roomId, bool erase, object[] points, long? baseRevision = null)
    => Send(w, "tileGesture", ("roomId", roomId), ("erase", erase), ("points", points), ("baseRevision", baseRevision ?? w.Revision));
static void TileGesturePath(EditorWorkspace w)
{
    string room = w.Canvas.Room.id;
    // Sparse pointer samples still expand into one continuous path while
    // retaining each corner instead of shortcutting across the gesture.
    TileGesture(w, room, false, Points((1, 1), (5, 1), (5, 5), (2, 5)));
    var cells = w.Canvas.Room.foreground.Select(cell => (cell.x, cell.y)).ToHashSet();
    var expected = new HashSet<(int, int)>();
    for (int x = 1; x <= 5; x++) expected.Add((x, 1));
    for (int y = 1; y <= 5; y++) expected.Add((5, y));
    for (int x = 2; x <= 5; x++) expected.Add((x, 5));
    Check(cells.SetEquals(expected), "A batched gesture must retain each turn in its sampled path.");
    string painted = Snapshot(w);
    TileGesture(w, room, true, Points((3, 1), (5, 1), (5, 3)));
    foreach (var erased in new[] { (3, 1), (4, 1), (5, 1), (5, 2), (5, 3) }) expected.Remove(erased);
    Check(w.Canvas.Room.foreground.Select(cell => (cell.x, cell.y)).ToHashSet().SetEquals(expected),
        "A batched erase must follow its complete sampled path.");
    Send(w, "undo");
    Check(Snapshot(w) == painted, "One Undo must restore the complete erased path.");
    Send(w, "undo");
    Check(w.Canvas.Room.foreground.Count == 0 && !w.Session.CanUndo, "The whole batched path must be one Undo step.");
}

static void TileGesturePreservesSelection(EditorWorkspace w)
{
    string room = w.Canvas.Room.id;
    var selected = new MetroidvaniaStudio.Primitives.RectInt(2, 3, 4, 5);
    w.Canvas.SelectArea(selected);
    Check(w.Canvas.Selection == selected, "The fixture must begin with a tile selection.");
    Send(w, "options", ("tool", (int)MetroidvaniaStudioTool.Brush));

    TileGesture(w, room, false, Points((1, 1), (3, 1)));

    Check(w.Canvas.Selection == selected,
        "Painting in the already-active room must not clear selection behind a compact response.");
}
static void TileGestureRasterBoundary(EditorWorkspace w)
{
    string room = w.Canvas.Room.id;
    TileGesture(w, room, false, Points((0, 0), (EditorWorkspace.MaximumTileGesturePoints - 1, 0)));
    Check(w.Canvas.Room.foreground.Count == w.Canvas.Room.width,
        "A raster path exactly at the gesture limit must be accepted and clipped only by the room bounds.");
    Send(w, "undo");
    Check(w.Canvas.Room.foreground.Count == 0 && !w.Session.CanUndo,
        "Undo must restore the document after an exact-limit gesture.");

    int brush = MapBrushGeometry.MaximumBrushSize;
    int safeRasterLength = (EditorWorkspace.MaximumTileGestureWork - 3 * brush * brush) / (2 * brush - 1) + 1;
    Send(w, "options", ("brushSize", brush), ("tool", (int)MetroidvaniaStudioTool.Line));
    TileGesture(w, room, false, Points((0, 0), (safeRasterLength - 1, 0)));
    Check(!w.Session.IsEditing && w.Session.CanUndo,
        "An exact-budget line and maximum brush must finish within the perimeter-expanded work budget.");
    Send(w, "undo");
    Send(w, "options", ("brushSize", 1), ("tool", (int)MetroidvaniaStudioTool.Brush));

    string before = Snapshot(w); long revision = w.Revision;
    Throws<ArgumentException>(() => TileGesture(w, room, false,
        Points((0, 0), (EditorWorkspace.MaximumTileGesturePoints, 0))));
    Throws<ArgumentException>(() => TileGesture(w, room, false,
        Points((0, 0), (EditorWorkspace.MaximumTileGesturePoints / 2, 0),
            (EditorWorkspace.MaximumTileGesturePoints / 2, EditorWorkspace.MaximumTileGesturePoints / 2))));
    Check(Snapshot(w) == before && w.Revision == revision && w.Canvas.ActiveRoomId == room
        && !w.Session.IsEditing && !w.Session.CanUndo,
        "Single and cumulative paths above the raster limit must preserve document, revision, room, and history.");
}
static void BucketWorkBoundary(EditorWorkspace w)
{
    var room = new MapRoom { id = "large", name = "large", width = 300, height = 300 };
    w.Session.New(new MapDocument { rooms = new List<MapRoom> { room } });
    w.Canvas.SelectRoom(room.id);
    Send(w, "options", ("tool", (int)MetroidvaniaStudioTool.Bucket), ("layer", (int)MapLayer.ForegroundTiles));
    string before = Snapshot(w); long revision = w.Revision;
    Throws<InvalidOperationException>(() => TileGesture(w, room.id, false, Points((0, 0))));
    Check(Snapshot(w) == before && w.Canvas.Room.foreground.Count == 0 && !w.Session.IsEditing && !w.Session.CanUndo
        && w.Revision > revision,
        $"An oversized bucket must roll back streamed partial work without history. "
        + $"same={Snapshot(w) == before}; cells={w.Canvas.Room.foreground.Count}; editing={w.Session.IsEditing}; undo={w.Session.CanUndo}; revision={w.Revision}/{revision}.");
}
static void TileGestureInvalid(EditorWorkspace w)
{
    string before = Snapshot(w); string room = w.Canvas.Room.id; long revision = w.Revision;
    Throws<ArgumentException>(() => TileGesture(w, room, false, Points((1, 1), ("not-an-integer", 2))));
    Check(Snapshot(w) == before && w.Canvas.ActiveRoomId == room && !w.Session.IsEditing && !w.Session.CanUndo,
        "A malformed point late in the batch must not leave earlier tiles or an active transaction.");
    Throws<WorkspaceConflict>(() => TileGesture(w, room, false, Points((1, 1)), revision - 1));
    Check(Snapshot(w) == before && w.Canvas.ActiveRoomId == room && !w.Session.IsEditing && !w.Session.CanUndo,
        "A stale gesture base revision must be rejected before changing the canvas.");
    Throws<ArgumentException>(() => TileGesture(w, room, false, Points()));
    Throws<ArgumentException>(() => TileGesture(w, "missing-room", false, Points((1, 1))));
    Throws<ArgumentException>(() => TileGesture(w, room, false,
        Enumerable.Range(0, EditorWorkspace.MaximumTileGesturePoints + 1).Select(index => (object)new { x = index, y = 0 }).ToArray()));
    Check(Snapshot(w) == before && w.Canvas.ActiveRoomId == room && !w.Session.IsEditing && !w.Session.CanUndo,
        "Empty, unknown-room, and oversized point arrays must be rejected without changing the active room or document.");
    Send(w, "options", ("layer", (int)MapLayer.Entities)); before = Snapshot(w);
    Throws<ArgumentException>(() => TileGesture(w, room, false, Points((1, 1))));
    Check(Snapshot(w) == before && w.Canvas.ActiveRoomId == room && !w.Session.IsEditing && !w.Session.CanUndo,
        "A batch on a non-tile layer must be rejected atomically.");

    Send(w, "options", ("layer", (int)MapLayer.ForegroundTiles));
    w.Canvas.Shape = (TileShape)999; before = Snapshot(w);
    Throws<ArgumentOutOfRangeException>(() => TileGesture(w, room, false, Points((1, 1), (3, 1))));
    Check(Snapshot(w) == before && !w.Session.IsEditing && !w.Session.CanUndo,
        "A failure after gesture capture must roll back the complete batch and release its transaction.");
    Send(w, "options", ("shape", (int)TileShape.Solid), ("clientId", "B"));
}
static void TileGestureRoomSwitch(EditorWorkspace w)
{
    string first = w.Canvas.Room.id;
    Send(w, "roomAdd", ("x", 50), ("y", 0), ("width", 12), ("height", 12), ("name", "batch target"));
    string target = w.Canvas.Room.id;
    Send(w, "selectRoom", ("id", first));
    TileGesture(w, target, false, Points((2, 3), (4, 3)));
    Check(w.Canvas.ActiveRoomId == target, "The batch must select its explicit room before painting.");
    Check(w.Session.Document.rooms.Single(room => room.id == first).foreground.Count == 0,
        "The previously selected room must remain untouched.");
    Check(w.Session.Document.rooms.Single(room => room.id == target).foreground.Select(cell => cell.x).Order().SequenceEqual(new[] { 2, 3, 4 }),
        "The requested room receives the complete batched stroke.");
}

static void ObjectGesture(EditorWorkspace w, string roomId, bool erase, object[] points, long? baseRevision = null)
    => Send(w, "objectGesture", ("roomId", roomId), ("erase", erase), ("points", points),
        ("baseRevision", baseRevision ?? w.Revision));
static void ObjectGestureErase(EditorWorkspace w)
{
    var first = new MapRoom { id = "first", name = "first", width = 20, height = 12 };
    first.objects.Add(new MapObject { id = "tiny-horizontal", definition = "trigger", layer = MapLayer.Triggers,
        x = 4.45f, y = 1.45f, width = 0.1f, height = 0.1f });
    first.objects.Add(new MapObject { id = "tiny-vertical", definition = "trigger", layer = MapLayer.Triggers,
        x = 8.45f, y = 4.45f, width = 0.1f, height = 0.1f });
    first.objects.Add(new MapObject { id = "survivor", definition = "trigger", layer = MapLayer.Triggers,
        x = 12, y = 9, width = 1, height = 1 });
    var second = new MapRoom { id = "second", name = "second", x = 30, width = 20, height = 12 };
    second.objects.Add(new MapObject { id = "other-room", definition = "trigger", layer = MapLayer.Triggers,
        x = 4.45f, y = 1.45f, width = 0.1f, height = 0.1f });
    w.Session.New(new MapDocument { rooms = new List<MapRoom> { first, second } });
    w.Canvas.SelectRoom(first.id);
    Send(w, "options", ("layer", (int)MapLayer.Triggers));
    string before = Snapshot(w);

    ObjectGesture(w, first.id, true, Points((1.5f, 1.5f), (8.5f, 1.5f), (8.5f, 7.5f)));
    Check(w.Canvas.ActiveRoomId == first.id
        && first.id == w.Session.Document.rooms[0].id
        && w.Session.Document.rooms[0].objects.Select(item => item.id).SequenceEqual(new[] { "survivor" })
        && w.Session.Document.rooms[1].objects.Single().id == "other-room",
        "Sparse curved samples must erase tiny crossed triggers only in the explicitly requested room.");
    Send(w, "undo");
    Check(Snapshot(w) == before && !w.Session.CanUndo,
        "One Undo must restore every object removed along the complete curved gesture.");

    ObjectGesture(w, second.id, true, Points((1.5f, 1.5f), (8.5f, 1.5f)));
    Check(w.Canvas.ActiveRoomId == second.id && w.Session.Document.rooms[1].objects.Count == 0
        && w.Session.Document.rooms[0].objects.Count == 3,
        "An object gesture may target another room without leaking erasure into the previous room.");
}
static void ObjectGesturePlacement(EditorWorkspace w)
{
    Directory.CreateDirectory(Path.GetDirectoryName(w.Files.CatalogPath)!);
    File.WriteAllText(w.Files.CatalogPath, """
        {"materials":[],"objects":[{"id":"rect","name":"Rectangle","color":"#808080","layer":2,"width":4,"height":4,
        "minimumWidth":4,"minimumHeight":4,"placement":1,"minimumNodes":0,"maximumNodes":0,
        "resizable":true,"rotatable":true,"flippable":true,"sprite":null,"properties":[]}],
        "camera":{"ppu":16,"referenceWidth":320,"referenceHeight":180,"orthographicSize":5.625}}
        """);
    Check(w.Catalog.Refresh(), "Placement fixture catalog must load.");
    Send(w, "options", ("tool", (int)MetroidvaniaStudioTool.Placement), ("layer", (int)MapLayer.Entities),
        ("objectDefinition", "rect"));
    ObjectGesture(w, w.Canvas.Room.id, false, Points((2.2f, 3.2f), (5.8f, 6.8f)));
    MapObject placed = w.Canvas.Room.objects.Single();
    Check(placed.definition == "rect" && placed.layer == MapLayer.Entities
        && placed.x == 2 && placed.y == 3 && placed.width == 4 && placed.height == 4,
        "One placement batch must create the intended snapped rectangle.");
    Send(w, "undo");
    Check(w.Canvas.Room.objects.Count == 0 && !w.Session.CanUndo,
        "Batched placement must be one Undo step.");
    ObjectGesture(w, w.Canvas.Room.id, false,
        Points((w.Canvas.Room.width - .8f, w.Canvas.Room.height - .8f)));
    Check(w.Canvas.Room.objects.Count == 0 && !w.Session.CanUndo,
        "Minimum-size clamping must reject an edge placement whose final object would leave the room.");
}
static void ObjectGestureNodePlacement(EditorWorkspace w)
{
    Directory.CreateDirectory(Path.GetDirectoryName(w.Files.CatalogPath)!);
    File.WriteAllText(w.Files.CatalogPath, """
        {"materials":[],"objects":[{"id":"path","name":"Path","color":"#808080","layer":2,"width":1,"height":1,
        "minimumWidth":1,"minimumHeight":1,"placement":2,"minimumNodes":2,"maximumNodes":-1,
        "resizable":true,"rotatable":true,"flippable":true,"sprite":null,"properties":[]}],
        "camera":{"ppu":16,"referenceWidth":320,"referenceHeight":180,"orthographicSize":5.625}}
        """);
    Check(w.Catalog.Refresh(), "Node placement fixture catalog must load.");
    Send(w, "options", ("tool", (int)MetroidvaniaStudioTool.Placement), ("layer", (int)MapLayer.Entities),
        ("objectDefinition", "path"));
    ObjectGesture(w, w.Canvas.Room.id, false, Points((2.25f, 3.5f), (8.75f, 7.25f)));
    MapObject placed = w.Canvas.Room.objects.Single();
    Check(placed.x == 2.25f && placed.y == 3.5f && placed.nodes.Count == 2
        && placed.nodes[^1] == new MetroidvaniaStudio.Primitives.Vector2(8.75f, 7.25f),
        "A node placement must preserve its start and replace the final default node with the dragged endpoint.");
    Send(w, "undo");
    Check(w.Canvas.Room.objects.Count == 0 && !w.Session.CanUndo, "Node placement must remain one Undo step.");
}
static void ObjectGestureInvalid(EditorWorkspace w)
{
    string room = w.Canvas.Room.id;
    Send(w, "options", ("layer", (int)MapLayer.Entities));
    string before = Snapshot(w); long revision = w.Revision;
    Throws<ArgumentException>(() => ObjectGesture(w, room, true, new object[] { new { y = 1 } }));
    Throws<ArgumentException>(() => ObjectGesture(w, "missing-room", true, Points((1, 1))));
    Throws<WorkspaceConflict>(() => ObjectGesture(w, room, true, Points((1, 1)), revision - 1));
    Check(Snapshot(w) == before && w.Canvas.ActiveRoomId == room && w.Revision == revision
        && !w.Session.IsEditing && !w.Session.CanUndo,
        "Malformed, unknown-room, and stale object batches must preserve document, room, revision, and history.");
}
static void ObjectGestureWorkBoundary(EditorWorkspace w)
{
    const int objectCount = 513, pointCount = 512;
    var room = new MapRoom { id = "work-room", name = "work-room", width = 20, height = 20 };
    for (int index = 0; index < objectCount; index++)
        room.objects.Add(new MapObject { id = "work-" + index, definition = "trigger", layer = MapLayer.Triggers,
            x = index % 20, y = index / 20 % 20, width = 0.1f, height = 0.1f });
    w.Session.New(new MapDocument { rooms = new List<MapRoom> { room } });
    w.Canvas.SelectRoom(room.id);
    Send(w, "options", ("layer", (int)MapLayer.Triggers));
    string before = Snapshot(w); long revision = w.Revision;
    object[] points = Enumerable.Range(0, pointCount).Select(index => (object)new { x = index % 20 + 0.5f, y = index % 2 + 0.5f }).ToArray();
    Check((long)objectCount * pointCount > EditorWorkspace.MaximumObjectGestureWork,
        "The regression fixture must exceed the advertised object intersection budget.");
    Throws<ArgumentException>(() => ObjectGesture(w, room.id, true, points));
    Check(Snapshot(w) == before && w.Revision == revision && w.Canvas.ActiveRoomId == room.id
        && !w.Session.IsEditing && !w.Session.CanUndo,
        "An excessive object path must be rejected before scanning, selecting, deleting, or opening history.");
}

static void StandaloneWorkspace()
{
    string project = Path.GetFullPath("../../..", AppContext.BaseDirectory);
    string directory = Path.Combine(project, ".test-output", Guid.NewGuid().ToString("N"));
    string install = Path.Combine(directory, "installation"), workspace = Path.Combine(directory, "workspace");
    EditorWorkspace? editor = null;
    try
    {
        Directory.CreateDirectory(Path.Combine(install, "Samples", "Textures"));
        File.WriteAllText(Path.Combine(install, "Samples", "catalog.json"),
            """{"materials":[],"objects":[],"camera":{"ppu":16,"referenceWidth":320,"referenceHeight":180,"orthographicSize":5.625}}""");
        File.WriteAllBytes(Path.Combine(install, "Samples", "Textures", "tile.png"), [1, 2, 3]);
        var files = new ProjectFiles(workspace, studioRoot: install);
        Check(files.MapsLabel == "Maps", "Empty directories must use standalone map storage.");
        Check(files.CatalogPath == Path.Combine(install, "Samples", "catalog.json"), "Sample catalog must come from the installation.");
        Check(File.Exists(files.Asset("Textures/tile.png")), "Sample texture must resolve without the game assets.");
        Directory.CreateDirectory(Path.Combine(workspace, "Textures"));
        File.WriteAllBytes(Path.Combine(workspace, "Textures", "tile.png"), [4]);
        Check(files.Asset("Textures/tile.png") == Path.Combine(workspace, "Textures", "tile.png"), "Workspace textures override samples.");
        Throws<ArgumentException>(() => files.Asset("Textures/../../outside.png"));
        Throws<ArgumentException>(() => files.Asset("Images/tile.png"));
        editor = new EditorWorkspace(files);
        editor.FlushAutoExports();
        Check(editor.State().workspace.mapsPath == "Maps",
            "The browser must receive a standalone identity without a required engine connection.");
        string exported = AutoFiles(editor).Single();
        Check(editor.State().export.path!.StartsWith("Maps/AutoExport/", StringComparison.Ordinal), "Standalone export receipts use the workspace map root.");
        Check(!File.Exists(exported + ".meta"), "Standalone exports must not create engine metadata.");
    }
    finally { editor?.StopAutoExports(); editor?.FlushRecovery(); editor?.Canvas.Dispose(); DeleteFixtureDirectory(directory); }
}

static void WorkspaceConfiguration()
{
    string project = Path.GetFullPath("../../..", AppContext.BaseDirectory);
    string directory = Path.Combine(project, ".test-output", Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(Path.Combine(directory, ".studio"));
        string settings = Path.Combine(directory, ".studio", "workspace.json");
        File.WriteAllText(settings, """{"mapsRoot":"Content/Maps","catalogPath":"Content/catalog.json","texturesRoot":"Content/Textures"}""");
        var files = new ProjectFiles(directory);
        Check(files.MapsLabel == "Content/Maps" && files.Map("room.json") == Path.Combine(directory, "Content", "Maps", "room.json"),
            "Configured map roots must be applied consistently.");
        Throws<ArgumentException>(() => files.Map("../escape.json"));
        File.WriteAllText(settings, """{"mapsRoot":"../outside"}""");
        Throws<ArgumentException>(() => new ProjectFiles(directory));

    }
    finally { DeleteFixtureDirectory(directory); }
}

static void GracefulShutdown(EditorWorkspace workspace)
{
    workspace.BeginShutdown();
    Throws<InvalidOperationException>(() => Send(workspace, "options", ("brushSize", 2)));
    workspace.FlushRecovery(); workspace.FlushAutoExports(); workspace.ValidateShutdown();
    Check(File.Exists(workspace.Files.RecoveryPath) && AutoFiles(workspace).Length > 0, "Shutdown must persist recovery and room output.");
}

static void FailedShutdown(EditorWorkspace workspace)
{
    workspace.FlushRecovery();
    Directory.CreateDirectory(Path.GetDirectoryName(workspace.Files.RecoveryPath)!);
    File.Delete(workspace.Files.RecoveryPath);
    Directory.CreateDirectory(workspace.Files.RecoveryPath);
    workspace.BeginShutdown(); workspace.FlushRecovery(); workspace.FlushAutoExports();
    Throws<IOException>(() => workspace.ValidateShutdown());
    workspace.AbortShutdown();
    Send(workspace, "options", ("brushSize", 2));
    Check(workspace.Canvas.BrushSize == 2, "A failed flush must allow editing to continue.");
    Directory.Delete(workspace.Files.RecoveryPath);
}

static void JsonImport(EditorWorkspace workspace)
{
    string before = Snapshot(workspace);
    Throws<WorkspaceConflict>(() => Send(workspace, "import", ("document", JsonSerializer.SerializeToElement(new { }))));
    Throws<InvalidDataException>(() => Send(workspace, "import", ("discard", true), ("document", JsonSerializer.SerializeToElement(new { formatVersion = 999 }))));
    Check(Snapshot(workspace) == before, "Malformed imports must preserve the current map and history.");
    var imported = MapDocument.CreateDefault(); imported.name = "Imported document";
    Send(workspace, "import", ("discard", true), ("document", JsonDocument.Parse(MapDocumentStore.Serialize(imported)).RootElement));
    Check(workspace.Session.Document.name == "Imported document" && workspace.Session.FilePath == null && workspace.State().dirty,
        "Imported browser JSON opens as an unsaved authoring document.");
}

static void CameraSettings(EditorWorkspace w)
{
    Check(StateElement(w).GetProperty("camera").GetProperty("ppu").GetInt32() == 16, "HTTP camera fields must serialize.");
    Check(w.State().camera.ppu == 16 && w.State().camera.referenceWidth == 320 && w.State().camera.referenceHeight == 180, "Default profile must be portable.");
    string before = Snapshot(w);
    Send(w, "cameraSettings", ("ppu", 32), ("referenceWidth", 400), ("referenceHeight", 224));
    Check(w.State().camera.orthographicSize == 3.5f && w.State(false, false, false).camera.ppu == 32, "Partial replies carry effective world-unit camera settings.");
    w.FlushAutoExports();
    MapDocument output = MapDocumentStore.Deserialize(File.ReadAllText(AutoFiles(w).Single()));
    Check(MapCameraSettings.Resolve(output).referenceWidth == 400 && output.formatVersion == 2, "Room export preserves settings without changing the JSON envelope.");
    foreach (int value in new[] { 0, -1, 8193 })
    {
        string unchanged = Snapshot(w);
        Throws<InvalidOperationException>(() => Send(w, "cameraSettings", ("ppu", value), ("referenceWidth", 320), ("referenceHeight", 180)));
        Check(Snapshot(w) == unchanged, "Invalid setting must not partially change the map.");
    }
    Send(w, "undo"); Check(Snapshot(w) == before && w.State().camera.ppu == 16, "One Undo restores all settings.");
    Send(w, "redo"); Check(w.State().camera.ppu == 32, "Redo restores profile.");
    Send(w, "save", ("path", "camera.map.json"));
    Check(MapCameraSettings.Resolve(MapDocumentStore.Deserialize(File.ReadAllText(w.Session.FilePath!))).referenceHeight == 224, "Saved JSON includes reference resolution.");
}

static void RoomCommands(EditorWorkspace w)
{
    Send(w, "options", ("tool", 3), ("layer", 0));
    string initial = Snapshot(w); string id = w.Canvas.ActiveRoomId;
    Send(w, "roomCopy"); Send(w, "roomPaste", ("x", -100), ("y", -50));
    Check(w.Session.Document.rooms.Count == 2 && w.Canvas.ActiveRoomId != id, "Room paste activates a new room from Brush mode.");
    Send(w, "roomFlip", ("horizontal", true)); Send(w, "roomFlip", ("horizontal", false)); Send(w, "roomRotate");
    Send(w, "roomDeleteSelected"); Check(w.Session.Document.rooms.Count == 1, "Room deletion is independent of the tile layer.");
    Send(w, "undo"); Check(w.Session.Document.rooms.Count == 2, "Room delete undo.");
    Send(w, "selectRoom", ("id", id)); long revision = w.DocumentRevision;
    Send(w, "selectArea", ("x", 0), ("y", 0), ("width", 3), ("height", 2));
    Check(w.Canvas.Selection?.width == 3 && w.DocumentRevision == revision, "Selecting an area does not clone, mutate or republish the document.");
}

static void RoomFiles(EditorWorkspace w)
{
    string source = Snapshot(w); string firstId = w.Canvas.ActiveRoomId;
    var extra = MapDocumentStore.Deserialize(source); extra.rooms[0].id = "imported-room"; extra.rooms[0].name = "Imported"; extra.rooms[0].x = -100;
    foreach (var item in extra.rooms[0].objects) item.id = Guid.NewGuid().ToString("N");
    Send(w, "importRooms", ("documents", new[] { JsonSerializer.Deserialize<JsonElement>(MapDocumentStore.Serialize(extra)) }));
    Check(w.Session.Document.rooms.Count == 2 && w.Canvas.ActiveRoomId == "imported-room", "Import appends exported rooms without discarding current rooms.");
    string imported = Snapshot(w);
    Throws<WorkspaceConflict>(() => Send(w, "importRooms", ("documents", new[] { JsonSerializer.Deserialize<JsonElement>(MapDocumentStore.Serialize(extra)) })));
    Check(Snapshot(w) == imported, "Existing room IDs need an explicit replacement.");
    Send(w, "exportRooms", ("directory", "Selected"), ("scope", "selected"));
    Check(Directory.GetFiles(Path.Combine(w.Files.MapsPath, "Selected"), "*.json").Length == 1, "Selected export writes only selected rooms.");
    Send(w, "exportRooms", ("directory", "AllRooms"), ("scope", "all"));
    string[] files = Directory.GetFiles(Path.Combine(w.Files.MapsPath, "AllRooms"), "*.json");
    var times = files.ToDictionary(f => f, File.GetLastWriteTimeUtc);
    Send(w, "exportRooms", ("directory", "AllRooms"), ("scope", "changed"));
    Check(files.All(f => File.GetLastWriteTimeUtc(f) == times[f]) && Snapshot(w) == imported, "Unchanged exports do not rewrite files or alter the map.");
    Send(w, "roomProperties", ("id", "imported-room"), ("y", 12));
    Send(w, "exportRooms", ("directory", "AllRooms"), ("scope", "changed"), ("overwrite", true));
    foreach (string file in files)
    {
        var room = MapDocumentStore.Deserialize(File.ReadAllText(file)).rooms[0];
        if (room.id == firstId) Check(File.GetLastWriteTimeUtc(file) == times[file], "Unchanged room file remains untouched.");
        else Check(room.y == 12, "Changed room exports current data.");
    }
    Send(w, "undo"); Send(w, "undo"); Check(Snapshot(w) == source, "Multi-room import is one Undo, independent of exporting.");
}
