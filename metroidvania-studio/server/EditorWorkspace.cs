using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using MetroidvaniaStudio;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio.Server;

/// <summary>Embeds an immutable JSON snapshot without reparsing it under the workspace lock.</summary>
[JsonConverter(typeof(ValidatedJsonConverter))]
public readonly struct ValidatedJson(string text)
{
    internal string Text { get; } = text ?? throw new ArgumentNullException(nameof(text));
}

public sealed class ValidatedJsonConverter : JsonConverter<ValidatedJson>
{
    public override ValidatedJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Validated JSON values are write-only.");

    public override void Write(Utf8JsonWriter writer, ValidatedJson value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.Text, skipInputValidation: true);
}

public sealed partial class EditorWorkspace
{
    public const int MaximumTileGesturePoints = 16384;
    public const int MaximumTileGestureWork = 65_536;
    public const int MaximumObjectGestureWork = 262144;
    public const int MaximumMiniMapConnections = MiniMapGeometry.DefaultMaximumConnections;
    private const int CompletedCommandLimit = 4096;
    public readonly object Gate = new();
    public ProjectFiles Files { get; }
    public Catalog Catalog { get; }
    public MapEditSession Session { get; }
    public MapCanvasController Canvas { get; }
    private readonly MapObjectErasing eraser;
    public long Revision { get; private set; } = 1;
    public long DocumentRevision { get; private set; } = 1;
    public long CatalogRevision { get; private set; } = 1;
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    private string documentJson;
    private EditorConnection[] connectionData = [];
    private RoomGeometry[] connectionGeometry = [];
    private bool dirty;
    private ProjectFiles.DiskFingerprint? diskFingerprint;
    private ProjectFiles.DiskFingerprint? observedDiskFingerprint;
    private ProjectFiles.DiskFingerprint? rejectedDiskFingerprint;
    private bool observedDiskMissing;
    private Task<DiskContentProbe>? diskContentProbe;
    private long nextDiskContentProbeAt;
    private long diskContentProbeVersion;
    // Latest state revision that can affect a mutation's meaning. Connection
    // notices and export progress may advance Revision without making a
    // gesture or shortcut stale.
    private long conflictRevision = 1;
    private readonly Dictionary<(string ClientId, string CommandId), string> completedCommands = new();
    private readonly Queue<(string ClientId, string CommandId)> completedCommandOrder = new();
    private long observedExportStatusRevision;
    private string? gestureOwner;
    private DateTime gestureAt;
    private Vector2? placementStart;
    private string? notice;
    private string? diskHealthNotice;
    private string? catalogHealthNotice;
    private string? recoveryNotice;
    private string? autoExportNotice;
    private readonly AutoRoomExporter autoExporter;
    private string? autoExportSavedPath;
    private readonly object recoveryGate = new();
    private readonly object recoveryIoGate = new();
    private readonly string recoveryPath;
    private RecoveryRequest? pendingRecovery;
    private Task? recoveryWorker;
    private long recoveryVersion;
    private const int DiskContentProbeIntervalMilliseconds = 5000;
    public string? Notice => recoveryNotice ?? autoExportNotice ?? diskHealthNotice ?? catalogHealthNotice ?? notice;

    private sealed class RecoveryRequest
    {
        public string? Json;
        public bool IsDirty;
        public string? SavedPath;
        public string? SavedDocumentHash;
        public long Version;
    }

    private sealed class DiskContentProbe
    {
        public string Path = "";
        public ProjectFiles.DiskMetadata Expected;
        public ProjectFiles.DiskFingerprint? Fingerprint;
        public MapDocument? Document;
        public Exception? DocumentError;
        public Exception? Error;
        public long Version;
        public long ConflictRevision;
    }

    public EditorWorkspace(ProjectFiles files)
    {
        Files = files; Catalog = new Catalog(files);
        string? initialCatalogNotice = null;
        try { Catalog.Refresh(); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException
            or JsonException or KeyNotFoundException or FormatException or OverflowException)
        { initialCatalogNotice = "Using the built-in catalog because EditorCatalog.json is invalid: " + error.Message; }
        Catalog.RefreshTextures();
        recoveryPath = files.RecoveryPath;
        Session = LoadInitialSession(files, recoveryPath, out string? initialNotice, out diskFingerprint);
        notice = initialNotice;
        catalogHealthNotice = initialCatalogNotice;
        Canvas = new MapCanvasController(Session)
        {
            ResolveTerrainTileSet = id => Catalog.Materials.GetValueOrDefault(id),
            ResolveObjectDefinition = id => Catalog.Objects.GetValueOrDefault(id),
            MaximumPaintWork = MaximumTileGestureWork
        };
        eraser = new MapObjectErasing(Canvas);
        documentJson = Session.CurrentJson;
        connectionData = Connections(Session.Document);
        connectionGeometry = CaptureRoomGeometry(Session.Document);
        dirty = Session.FilePath == null || documentJson != Session.SavedJson;
        observedDiskFingerprint = diskFingerprint;
        Session.Changed += DocumentChanged;
        autoExporter = new AutoRoomExporter(files, message =>
        {
            lock (Gate)
            {
                if (autoExportNotice == message) return;
                autoExportNotice = message;
                // Output health changes must not invalidate an in-flight brush command.
                Revision++;
            }
        });
        autoExportSavedPath = Session.FilePath;
        QueueAutoExport();
    }

    private MapEditSession LoadInitialSession(ProjectFiles files, string recovery, out string? startupNotice,
        out ProjectFiles.DiskFingerprint? initialFingerprint)
    {
        startupNotice = null;
        initialFingerprint = null;
        if (File.Exists(recovery))
        {
            try
            {
                ProjectFiles.RecoveryMap recovered = files.LoadRecovery();
                if (!recovered.IsLegacy && recovered.SavedPath != null)
                {
                    string savedPath = files.Map(recovered.SavedPath);
                    try
                    {
                        ProjectFiles.StableMap saved = ProjectFiles.LoadStable(savedPath);
                        string savedJson = MapDocumentStore.Serialize(saved.Document);
                        if (string.Equals(ProjectFiles.HashDocumentJson(savedJson), recovered.SavedDocumentHash,
                            StringComparison.Ordinal))
                        {
                            initialFingerprint = saved.Fingerprint;
                            if (!recovered.IsDirty)
                            {
                                startupNotice = "Completed recovery of the latest named map save.";
                                return new MapEditSession(saved.Document, savedPath);
                            }
                            startupNotice = "Recovered unsaved edits on " + recovered.SavedPath + ".";
                            return new MapEditSession(recovered.Document, savedPath, savedJson);
                        }
                    }
                    catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
                    {
                        startupNotice = "The recovery target could not be validated (" + error.Message
                            + "). Recovered the latest document as unsaved.";
                    }
                    startupNotice ??= "The recovery target changed before saving completed. Recovered the latest document as unsaved.";
                    return new MapEditSession(recovered.Document);
                }
                startupNotice = recovered.IsLegacy
                    ? "Recovered a legacy unsaved editing session. Save to keep it as a named map."
                    : "Recovered the previous unsaved editing session. Save to keep it as a named map.";
                return new MapEditSession(recovered.Document);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException
                or JsonException or ArgumentException or FormatException or OverflowException)
            {
                string quarantine = Path.Combine(Path.GetDirectoryName(recovery)!,
                    "Workspace.corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".map.json");
                string disposition;
                try { File.Move(recovery, quarantine); disposition = "It was preserved as " + Path.GetFileName(quarantine) + "."; }
                catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
                { disposition = "It remains at the recovery path because it could not be quarantined."; }
                startupNotice = "Ignored an invalid recovery snapshot (" + error.Message + "). " + disposition;
            }
        }
        string[] savedMaps = files.List();
        foreach (string relative in savedMaps)
        {
            string path = files.Map(relative);
            try
            {
                ProjectFiles.StableMap loaded = ProjectFiles.LoadStable(path);
                initialFingerprint = loaded.Fingerprint;
                return new MapEditSession(loaded.Document, path);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                startupNotice ??= "Skipped an unreadable saved map (" + relative + "): " + error.Message;
            }
        }
        if (savedMaps.Length == 0 && startupNotice == null && files.SampleWorldPath is { } samplePath && File.Exists(samplePath))
        {
            try { return new MapEditSession(LoadSampleWorld()); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        return new MapEditSession(MapDocument.CreateDefault());
    }

    private void DocumentChanged()
    {
        if (Session.LastChangeKind is MapEditChangeKind.Reset or MapEditChangeKind.Save) browserFile = null;
        string nextJson = Session.CurrentJson;
        bool contentChanged = nextJson != documentJson;
        if (contentChanged)
        {
            documentJson = nextJson;
            if (!SameRoomGeometry(Session.Document, connectionGeometry))
            {
                connectionData = Connections(Session.Document);
                connectionGeometry = CaptureRoomGeometry(Session.Document);
            }
            DocumentRevision++;
        }
        bool nextDirty = Session.FilePath == null || documentJson != Session.SavedJson;
        bool dirtyChanged = nextDirty != dirty;
        dirty = nextDirty;
        bool fileChanged = !string.Equals(autoExportSavedPath, Session.FilePath, ProjectFiles.PathComparison);
        if (contentChanged || fileChanged || Session.LastChangeKind == MapEditChangeKind.Reset)
        {
            autoExportSavedPath = Session.FilePath;
            QueueAutoExport();
        }
        Revision++;
        conflictRevision = Revision;
        if (!contentChanged && !dirtyChanged) return;
        // A probe captured against an earlier document/dirty generation must
        // never replace or reinterpret the state produced by this change.
        InvalidateDiskContentProbe();
        QueueRecovery();
    }

    private void QueueAutoExport() => autoExporter.Queue(documentJson,
        Session.FilePath == null ? null : Files.Relative(Session.FilePath));

    /// <summary>Call outside Gate. Complete the latest room export without the idle delay.</summary>
    public void FlushAutoExports() => autoExporter.Flush();

    /// <summary>Call outside Gate during teardown; no worker may outlive its workspace.</summary>
    public void StopAutoExports() => autoExporter.Dispose();

    private void QueueRecovery()
    {
        lock (recoveryGate)
        {
            pendingRecovery = dirty ? CreateRecoveryRequest(documentJson, true) : new RecoveryRequest { Version = ++recoveryVersion };
            recoveryWorker ??= Task.Run(WriteRecoveryLoop);
        }
    }

    private RecoveryRequest CreateRecoveryRequest(string json, bool isDirty)
    {
        string? savedPath = Session.FilePath == null ? null : Files.Relative(Session.FilePath);
        string? savedHash = savedPath == null || string.IsNullOrEmpty(Session.SavedJson)
            ? null : ProjectFiles.HashDocumentJson(Session.SavedJson);
        return new RecoveryRequest
        {
            Json = json,
            IsDirty = isDirty,
            SavedPath = savedPath,
            SavedDocumentHash = savedHash,
            Version = ++recoveryVersion
        };
    }

    private void PersistSaveIntent(string path)
    {
        RecoveryRequest request;
        lock (recoveryGate)
        {
            request = new RecoveryRequest
            {
                Json = documentJson,
                IsDirty = false,
                SavedPath = Files.Relative(path),
                SavedDocumentHash = ProjectFiles.HashDocumentJson(documentJson),
                Version = ++recoveryVersion
            };
            pendingRecovery = null;
        }
        // Serialize this write with the worker. An older dirty snapshot either
        // finishes before this marker or notices its stale generation and skips.
        lock (recoveryIoGate)
            Files.SaveRecovery(request.Json, request.IsDirty, request.SavedPath, request.SavedDocumentHash);
    }

    private void WriteRecoveryLoop()
    {
        while (true)
        {
            RecoveryRequest request;
            lock (recoveryGate)
            {
                if (pendingRecovery == null) { recoveryWorker = null; return; }
                request = pendingRecovery; pendingRecovery = null;
            }
            string? errorMessage = null;
            try
            {
                lock (recoveryIoGate)
                {
                    lock (recoveryGate)
                        if (request.Version != recoveryVersion) continue;
                    if (request.Json != null)
                        Files.SaveRecovery(request.Json, request.IsDirty, request.SavedPath, request.SavedDocumentHash);
                    else ClearRecovery();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                errorMessage = request.Json != null
                    ? "The edit succeeded, but recovery could not be saved: " + error.Message
                    : "The document is saved, but stale recovery cleanup failed: " + error.Message;
            }
            lock (Gate)
            {
                lock (recoveryGate)
                {
                    if (request.Version != recoveryVersion) continue;
                    if (recoveryNotice == errorMessage) continue;
                    recoveryNotice = errorMessage;
                    Revision++;
                }
            }
        }
    }

    /// <summary>Waits until the latest immutable recovery snapshot is durable.</summary>
    public void FlushRecovery()
    {
        while (true)
        {
            Task? worker;
            lock (recoveryGate) worker = recoveryWorker;
            if (worker == null) return;
            worker.GetAwaiter().GetResult();
        }
    }

    private static EditorConnection[] Connections(MapDocument document) => MiniMapGeometry.GetConnections(document, MaximumMiniMapConnections)
        .Select(c => new EditorConnection(c.RoomAId, c.RoomBId, c.Vertical, c.Coordinate, c.Start, c.End)).ToArray();

    private readonly struct RoomGeometry
    {
        public readonly string Id;
        public readonly int X, Y, Width, Height;
        public RoomGeometry(MapRoom room)
        { Id = room.id; X = room.x; Y = room.y; Width = room.width; Height = room.height; }
        public bool Matches(MapRoom room) => room != null && Id == room.id
            && X == room.x && Y == room.y && Width == room.width && Height == room.height;
    }

    private static RoomGeometry[] CaptureRoomGeometry(MapDocument document) =>
        document.rooms.Select(room => new RoomGeometry(room)).ToArray();

    private static bool SameRoomGeometry(MapDocument document, RoomGeometry[] geometry)
    {
        if (document.rooms.Count != geometry.Length) return false;
        for (int index = 0; index < geometry.Length; index++)
            if (!geometry[index].Matches(document.rooms[index])) return false;
        return true;
    }

    private void SetNotice(string message)
    {
        if (notice == message) return;
        notice = message;
        Revision++;
    }

    private void SetDiskHealthNotice(string? message)
    {
        if (diskHealthNotice == message) return;
        diskHealthNotice = message;
        Revision++;
    }

    private void SetCatalogHealthNotice(string? message)
    {
        if (catalogHealthNotice == message) return;
        catalogHealthNotice = message;
        Revision++;
    }

    public EditorState State(bool includeDocument = true, bool includeCatalog = true, bool includeSelection = true)
    {
        RefreshExportStatusRevision();
        EditorSelection? selection = null;
        if (includeSelection)
        {
            // This getter synchronizes and prunes both node and body selection.
            // Read it before SelectedIds so one response cannot expose stale IDs.
            var selectedNodes = Canvas.ObjectEditor.SelectedNodes.Select(n => new EditorNodeSelection(n.ObjectId, n.NodeIndex)).ToArray();
            var selectedObjects = Canvas.ObjectEditor.SelectedIds.ToArray();
            var area = Canvas.Selection;
            selection = new EditorSelection(Canvas.ActiveRoomId, (int)Canvas.Tool, (int)Canvas.Layer, (int)Canvas.Shape,
                Canvas.Material, Canvas.BrushSize, Canvas.Filled, Canvas.ObjectDefinition, Canvas.ActiveGroupId,
                Canvas.HiddenLayers.Select(x => (int)x).ToArray(), Canvas.LockedLayers.Select(x => (int)x).ToArray(),
                selectedObjects, selectedNodes,
                area.HasValue ? new EditorRect(area.Value.x, area.Value.y, area.Value.width, area.Value.height) : null,
                Session.Document.rooms.Where(room => Canvas.RoomEditor.SelectedIds.Contains(room.id)).Select(room => room.id).ToArray());
        }
        return new EditorState
        {
            camera = MapCameraSettings.Resolve(Session.Document, Catalog.Data.GetProperty("camera").Deserialize<CameraProfile>(Catalog.Json)),
            revision = Revision, documentRevision = DocumentRevision, catalogRevision = CatalogRevision,
            instanceId = InstanceId, file = browserFile?.Name ?? (Session.FilePath == null ? null : Files.Relative(Session.FilePath)), browserFileId = browserFile?.Id,
            dirty = HasUnsavedChanges, canUndo = Session.CanUndo, canRedo = Session.CanRedo, notice = Notice,
            workspace = new EditorWorkspaceInfo(Path.GetFileName(Files.ProjectPath), Files.MapsLabel),
            export = autoExporter.Status(Canvas.ActiveRoomId, Canvas.RoomEditor.SelectedIds), selection = selection,
            document = includeDocument ? new ValidatedJson(documentJson) : null,
            connections = includeDocument ? connectionData : null, catalog = includeCatalog ? Catalog.Data : null
        };
    }
    public bool IsStateCurrent(long? revision, string? instanceId, long? documentRevision, long? catalogRevision)
    {
        RefreshExportStatusRevision();
        return instanceId == InstanceId && revision == Revision
            && documentRevision == DocumentRevision && catalogRevision == CatalogRevision;
    }

    private void RefreshExportStatusRevision()
    {
        long current = autoExporter.StatusRevision;
        if (current == observedExportStatusRevision) return;
        observedExportStatusRevision = current;
        // Passive status changes do not advance conflictRevision, invalidate
        // gestures, or send/re-index the unchanged document and catalog.
        Revision++;
    }
    public void Tick()
    {
        if (shuttingDown) return;
        if (gestureOwner != null && DateTime.UtcNow - gestureAt > TimeSpan.FromSeconds(15))
        { Cancel(); Revision++; conflictRevision = Revision; }
        try
        {
            bool catalogChanged = Catalog.Refresh();
            if (catalogChanged)
            {
                SetCatalogHealthNotice(null);
                Revision++; conflictRevision = Revision; CatalogRevision++;
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException
            or JsonException or KeyNotFoundException or FormatException or OverflowException)
        { SetCatalogHealthNotice("EditorCatalog.json could not be reloaded: " + error.Message); }
        if (Catalog.RefreshTextures())
        {
            // Pixel changes affect rendering and engine resources, not the map or
            // the meaning of an in-flight stroke. Keep document/gesture revisions.
            Revision++; CatalogRevision++;
        }
        if (Session.FilePath != null && !Session.IsEditing && gestureOwner == null)
        {
            try
            {
            string pollingPath = Session.FilePath;
            ProjectFiles.DiskMetadata? metadata = ProjectFiles.Metadata(pollingPath);
            if (!metadata.HasValue)
            {
                observedDiskFingerprint = null;
                rejectedDiskFingerprint = null;
                if (!observedDiskMissing)
                {
                    InvalidateDiskContentProbe();
                    observedDiskMissing = true;
                    SetDiskHealthNotice("The saved map was deleted outside this editor. Save under a new name or reopen it.");
                }
                return;
            }
            bool metadataChanged = !observedDiskFingerprint.HasValue
                || !ProjectFiles.SameMetadata(observedDiskFingerprint.Value, metadata.Value);
            if (!TryConsumeDiskContentProbe(pollingPath, metadata.Value, out DiskContentProbe? probe))
            {
                ScheduleDiskContentProbe(pollingPath, metadata.Value, metadataChanged || observedDiskMissing);
                return;
            }

            ProjectFiles.DiskFingerprint current = probe!.Fingerprint!.Value;
            bool reappeared = observedDiskMissing;
            observedDiskMissing = false;
            observedDiskFingerprint = current;
            if (ProjectFiles.SameContent(current, diskFingerprint))
            {
                diskFingerprint = current; // metadata-only touch; content identity is unchanged.
                rejectedDiskFingerprint = null;
                SetDiskHealthNotice(null);
                if (reappeared) SetNotice("Reloaded a map restored in the workspace.");
            }
            else if (dirty)
                SetDiskHealthNotice("The saved map changed outside this editor. Save under a new name or reopen it.");
            else if (ProjectFiles.SameContent(current, rejectedDiskFingerprint)) return;
            else if (probe.DocumentError != null)
            {
                rejectedDiskFingerprint = current;
                SetDiskHealthNotice("The saved map changed outside this editor, but it could not be reloaded: "
                    + probe.DocumentError.Message);
            }
            else if (probe.Document == null)
            {
                // This can only occur if a captured skip-hash no longer agrees
                // with the workspace state. Treat it as stale instead of loading
                // an unrelated document; the next poll will prepare it again.
                nextDiskContentProbeAt = 0;
            }
            else
            {
                string path = Session.FilePath;
                Session.Load(probe.Document, path);
                TrackOpenedFile(current);
                Revision++;
                SetDiskHealthNotice(null);
                SetNotice("Reloaded a map changed in the workspace.");
            }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                observedDiskFingerprint = null;
                SetDiskHealthNotice("The saved map could not be checked for external changes: " + error.Message);
            }
        }
    }
    /// <returns>False when an already completed idempotent command was acknowledged.</returns>
    public bool Command(JsonElement command, string? requestFingerprint = null)
    {
        if (shuttingDown) throw new InvalidOperationException("The studio is saving and shutting down.");
        string owner = RequiredClientId(command);
        string commandId = RequiredCommandId(command);
        var commandKey = (owner, commandId);
        requestFingerprint ??= FingerprintCommand(command);
        if (completedCommands.TryGetValue(commandKey, out string? completedFingerprint))
        {
            if (!string.Equals(completedFingerprint, requestFingerprint, StringComparison.Ordinal))
                throw new WorkspaceConflict("This commandId was already completed with a different command payload.");
            return false;
        }
        string? previousNotice = notice;
        try
        {
            CommandCore(command);
            completedCommands.Add(commandKey, requestFingerprint); completedCommandOrder.Enqueue(commandKey);
            if (completedCommandOrder.Count > CompletedCommandLimit)
                completedCommands.Remove(completedCommandOrder.Dequeue());
            return true;
        }
        catch
        {
            notice = previousNotice;
            // A failed pointer request must not strand its lease or partial stroke.
            if (gestureOwner == owner) { Cancel(); Revision++; conflictRevision = Revision; }
            throw;
        }
    }

    private void ScheduleDiskContentProbe(string path, ProjectFiles.DiskMetadata expected, bool urgent)
    {
        if (diskContentProbe != null || !urgent && Environment.TickCount64 < nextDiskContentProbeAt) return;
        long version = diskContentProbeVersion;
        long generation = conflictRevision;
        bool parseChangedContent = !dirty;
        string? knownHash = diskFingerprint?.Hash;
        string? rejectedHash = parseChangedContent ? rejectedDiskFingerprint?.Hash : null;
        diskContentProbe = Task.Run(() =>
        {
            var result = new DiskContentProbe
            {
                Path = path, Expected = expected, Version = version, ConflictRevision = generation
            };
            try
            {
                ProjectFiles.MapProbe snapshot = Files.ProbeMap(path, expected, parseChangedContent,
                    knownHash, rejectedHash);
                result.Fingerprint = snapshot.Fingerprint;
                result.Document = snapshot.Document;
                result.DocumentError = snapshot.DocumentError;
            }
            catch (Exception error) { result.Error = error; }
            return result;
        });
    }

    private bool TryConsumeDiskContentProbe(string path, ProjectFiles.DiskMetadata current,
        out DiskContentProbe? result)
    {
        result = null;
        Task<DiskContentProbe>? task = diskContentProbe;
        if (task == null || !task.IsCompleted) return false;
        DiskContentProbe completed = task.GetAwaiter().GetResult();
        diskContentProbe = null;
        if (completed.Version != diskContentProbeVersion
            || completed.ConflictRevision != conflictRevision
            || !string.Equals(completed.Path, path, ProjectFiles.PathComparison)
            || !ProjectFiles.SameMetadata(completed.Expected, current))
        {
            nextDiskContentProbeAt = 0;
            return false;
        }
        nextDiskContentProbeAt = Environment.TickCount64 + DiskContentProbeIntervalMilliseconds;
        if (completed.Error != null) throw completed.Error;
        if (!completed.Fingerprint.HasValue) return false;
        result = completed;
        return true;
    }

    private void InvalidateDiskContentProbe()
    {
        diskContentProbeVersion++;
        diskContentProbe = null;
        nextDiskContentProbeAt = 0;
    }

    /// <summary>
    /// Computes the request identity before entering <see cref="Gate"/> in the
    /// HTTP path. JsonElement.WriteTo normalizes insignificant whitespace while
    /// streaming through SHA-256, avoiding a second request-sized byte array.
    /// </summary>
    internal static string FingerprintCommand(JsonElement command)
    {
        using var algorithm = SHA256.Create();
        using var crypto = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write, leaveOpen: true);
        using (var writer = new Utf8JsonWriter(crypto))
        {
            command.WriteTo(writer);
            writer.Flush();
        }
        crypto.FlushFinalBlock();
        return Convert.ToHexString(algorithm.Hash!);
    }
    private void CommandCore(JsonElement command)
    {
        string action = S(command, "action");
        string owner = RequiredClientId(command);
        if (!command.TryGetProperty("expectedInstanceId", out var expectedInstance)
            || expectedInstance.ValueKind != JsonValueKind.String || expectedInstance.GetString() != InstanceId)
            throw new WorkspaceConflict("The editor server restarted. Reload its current document before editing.");
        if (!command.TryGetProperty("expectedRevision", out var expected) || expected.ValueKind != JsonValueKind.Number
            || !expected.TryGetInt64(out long expectedRevision))
            throw new ArgumentException("Every editor command needs an integer expectedRevision.");
        RequireMutationRevision(expectedRevision);
        if (gestureOwner != null && gestureOwner != owner) throw new WorkspaceConflict("Another editor view is finishing a gesture.");
        if (gestureOwner != null && action is not ("drag" or "end" or "cancel" or "tileGesture" or "objectGesture")) Cancel();
        notice = null;
        if (action is "roomCopy" or "roomCut" or "roomFlip" or "roomRotate" or "roomDeleteSelected" or "roomDuplicate"
            && Canvas.RoomEditor.SelectedIds.Count == 0 && Canvas.Room != null) Canvas.RoomEditor.Select(Canvas.Room.id);
        switch (action)
        {
            case "options": Options(command); break;
            case "paletteAdd":
                string paletteId = Catalog.AddPalette(S(command, "name"), S(command, "color"));
                CatalogRevision++; SetCatalogHealthNotice(null);
                Canvas.Material = paletteId;
                if (Canvas.Tool is MetroidvaniaStudioTool.Rooms or MetroidvaniaStudioTool.Selection)
                    Canvas.Tool = MetroidvaniaStudioTool.Brush;
                Canvas.Deselect();
                break;
            case "paletteConfigure":
                Catalog.ConfigurePalette(S(command, "id"), S(command, "name"), S(command, "color"),
                    JsonSerializer.Deserialize<PaletteTileset>(command.GetProperty("settings"), Catalog.Json)!,
                    command.TryGetProperty("png", out var palettePng) ? palettePng.GetString() : null, command.GetProperty("expectedMaterial"),
                    command.TryGetProperty("uploads", out var paletteUploads) ? JsonSerializer.Deserialize<Dictionary<string, string>>(paletteUploads, Catalog.Json) : null);
                CatalogRevision++; SetCatalogHealthNotice(null);
                break;
            case "selectRoom":
                string selectedRoom = S(command, "id");
                RequireRoom(selectedRoom);
                Canvas.SelectRoom(selectedRoom, toggle: B(command, "toggle"));
                break;
            case "pick": Canvas.Pick(Cell(command)); break;
            case "begin":
                Cancel(); gestureOwner = owner; gestureAt = DateTime.UtcNow;
                if (B(command, "erase")) { if (Canvas.IsTileLayer) Canvas.BeginEraseGesture(Cell(command)); else eraser.Begin(Point(command)); }
                else if (!Canvas.IsTileLayer && Canvas.Tool == MetroidvaniaStudioTool.Placement) placementStart = Point(command);
                else Canvas.BeginGesture(Cell(command));
                break;
            case "drag":
                RequireGesture(owner); gestureAt = DateTime.UtcNow;
                if (eraser.IsActive) eraser.Drag(Point(command)); else if (!placementStart.HasValue) Canvas.DragGesture(Cell(command));
                break;
            case "end":
                RequireGesture(owner);
                if (eraser.IsActive) { eraser.Drag(Point(command)); eraser.End(); }
                else if (placementStart.HasValue) Place(Point(command));
                else Canvas.EndGesture(Cell(command));
                placementStart = null; gestureOwner = null;
                break;
            // Explicit cancellation also dismisses selection. Internal gesture cleanup
            // keeps it intact when starting another stroke or recovering from failure.
            case "cancel": Cancel(); Canvas.Deselect(); Canvas.RoomEditor.Clear(); break;
            case "tileGesture": TileGesture(command, owner); break;
            case "objectGesture": ObjectGesture(command, owner); break;
            case "undo": Session.Undo(); break;
            case "redo": Session.Redo(); break;
            case "roomAdd":
                Canvas.SelectRoom(Canvas.RoomEditor.Create(new RectInt(I(command, "x"), I(command, "y"), I(command, "width", 16), I(command, "height", 10)), command.TryGetProperty("name", out var roomName) ? roomName.GetString() : null));
                break;
            case "roomMove": MoveRoom(command); break;
            case "roomResize": Canvas.RoomEditor.Resize(S(command, "id"), new RectInt(I(command, "x"), I(command, "y"), I(command, "width"), I(command, "height")), B(command, "crop")); break;
            case "roomDelete": DeleteRoom(command); break;
            case "roomDuplicate": SelectRoomResult(Canvas.RoomEditor.DuplicateSelected()); break;
            case "roomCopy": Canvas.RoomEditor.CopySelected(); break;
            case "roomCut": Canvas.RoomEditor.CutSelected(); break;
            case "roomPaste": SelectRoomResult(Canvas.RoomEditor.Paste(Cell(command))); break;
            case "roomFlip": Canvas.RoomEditor.FlipSelected(B(command, "horizontal", true)); break;
            case "roomRotate": Canvas.RoomEditor.RotateSelected(B(command, "clockwise", true)); break;
            case "roomDeleteSelected": Canvas.RoomEditor.DeleteSelected(); break;
            case "selectArea": Canvas.SelectArea(new RectInt(I(command, "x"), I(command, "y"), I(command, "width"), I(command, "height"))); break;
            case "moveSelection": Canvas.MoveSelection(new Vector2Int(I(command, "dx"), I(command, "dy"))); break;
            case "roomProperties":
                ConfigureRoom(command); break;
            case "clearLayer": var layerResult = Canvas.ClearCurrentLayer(); notice = $"@cleared:{layerResult.Removed}:{layerResult.Protected}"; break;
            case "clearObjects": var objectsResult = Canvas.ClearRoomObjects(); notice = $"@cleared:{objectsResult.Removed}:{objectsResult.Protected}"; break;
            case "objectClick": Canvas.ObjectEditor.Click(Point(command), B(command, "additive")); break;
            case "objectMove": Canvas.ObjectEditor.Move(new Vector2(F(command, "dx"), F(command, "dy"))); break;
            case "objectResize": Canvas.ObjectEditor.Resize(new Vector2(F(command, "dx"), F(command, "dy"))); break;
            case "objectTransform": TransformObject(command); break;
            case "objectProperties": Canvas.ObjectEditor.ApplyProperties(Read<Dictionary<string, string>>(command.GetProperty("values"))); break;
            case "nodeSelect": Canvas.ObjectEditor.SelectNode(S(command, "id"), I(command, "index"), B(command, "additive")); break;
            case "nodeAdd": Canvas.ObjectEditor.AddNode(Point(command)); break;
            case "nodeMove": Canvas.ObjectEditor.MoveNode(Point(command)); break;
            case "nodeDelete": Canvas.ObjectEditor.DeleteNode(); break;
            case "copy": if (Canvas.IsTileLayer) Canvas.CopySelection(); else Canvas.ObjectEditor.Copy(); break;
            case "cut": if (Canvas.IsTileLayer) Canvas.CutSelection(); else Canvas.ObjectEditor.Cut(); break;
            case "delete": if (Canvas.IsTileLayer) Canvas.DeleteSelection(); else Canvas.ObjectEditor.Delete(); break;
            case "paste": if (Canvas.IsTileLayer) Canvas.Paste(Cell(command)); else Canvas.ObjectEditor.Paste(Point(command)); break;
            case "flip": if (Canvas.IsTileLayer) Canvas.FlipSelection(B(command, "horizontal", true)); else Canvas.ObjectEditor.Flip(B(command, "horizontal", true)); break;
            case "rotate": if (Canvas.IsTileLayer) Canvas.RotateSelection(B(command, "clockwise", true)); else Canvas.ObjectEditor.Rotate(B(command, "clockwise", true)); break;
            case "selectAll": if (Canvas.Tool == MetroidvaniaStudioTool.Rooms) Canvas.RoomEditor.SelectAll(); else if (Canvas.IsTileLayer && Canvas.Room != null) Canvas.SelectArea(new RectInt(0, 0, Canvas.Room.width, Canvas.Room.height)); else Canvas.ObjectEditor.SelectAll(); break;
            case "cameraSettings":
                Session.Execute("Camera settings", document => MapCameraSettings.Apply(document, I(command, "ppu"), I(command, "referenceWidth"), I(command, "referenceHeight")));
                break;
            case "documentProperties":
                Session.Execute("Document properties", document =>
                {
                    if (command.TryGetProperty("name", out var name)) document.name = name.GetString();
                    if (command.TryGetProperty("properties", out var props)) document.properties = Read<List<MapProperty>>(props);
                    if (command.TryGetProperty("stylegrounds", out var styles)) document.stylegrounds = Read<List<MapStyleground>>(styles);
                    if (command.TryGetProperty("layerGroups", out var groups)) document.layerGroups = Read<List<MapLayerGroup>>(groups);
                }); break;
            case "save": Save(command); break;
            case "open":
                if (HasUnsavedChanges && !B(command, "discard")) throw new WorkspaceConflict("Save current changes first, or confirm discarding them.");
                string openPath = Files.Map(S(command, "path"));
                ProjectFiles.StableMap opened = ProjectFiles.LoadStable(openPath);
                Session.Load(opened.Document, openPath);
                TrackOpenedFile(opened.Fingerprint);
                break;
            case "importRooms": ImportRooms(command); break;
            case "sampleWorld":
                if (HasUnsavedChanges && !B(command, "discard")) throw new WorkspaceConflict("Save current changes first, or confirm discarding them.");
                MapDocument sample = LoadSampleWorld();
                Session.New(sample);
                InvalidateDiskContentProbe();
                diskFingerprint = null; observedDiskFingerprint = null; rejectedDiskFingerprint = null; observedDiskMissing = false;
                SetDiskHealthNotice(null); break;
            case "browserSave": AcknowledgeBrowserSave(command); break;
            case "browserOpen":
            case "import":
                if (HasUnsavedChanges && !B(command, "discard")) throw new WorkspaceConflict("Save current changes first, or confirm discarding them.");
                if (!command.TryGetProperty("document", out var importedJson) || importedJson.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("The imported map must be a JSON object.");
                string? browserName = action == "browserOpen" ? BrowserFileName(command) : null;
                MapDocument imported = MapDocumentStore.Deserialize(importedJson.GetRawText());
                Session.New(imported);
                if (browserName != null) { browserFile = new BrowserFile(Guid.NewGuid().ToString("N"), browserName, documentJson); QueueRecovery(); }
                InvalidateDiskContentProbe();
                diskFingerprint = null; observedDiskFingerprint = null; rejectedDiskFingerprint = null; observedDiskMissing = false;
                SetDiskHealthNotice(null); break;
            case "new":
                if (HasUnsavedChanges && !B(command, "discard")) throw new WorkspaceConflict("Save current changes first, or confirm discarding them.");
                var created = MapDocument.CreateDefault(); created.name = S(command, "name", "Untitled"); Session.New(created);
                InvalidateDiskContentProbe();
                diskFingerprint = null; observedDiskFingerprint = null; rejectedDiskFingerprint = null; observedDiskMissing = false;
                SetDiskHealthNotice(null); break;
            case "exportRooms":
                string target = Files.ExportDirectory(S(command, "directory", "Exports"));
                string scope = S(command, "scope", "all");
                if (scope != "selected" && scope != "all" && scope != "changed") throw new ArgumentException("Unknown room export scope.");
                IEnumerable<string>? selectedIds = scope == "selected" ? Canvas.RoomEditor.SelectedIds.Count > 0
                    ? Canvas.RoomEditor.SelectedIds : new[] { Canvas.ActiveRoomId } : null;
                IReadOnlyList<MapRoomJsonExporter.Entry> exportPlan = MapRoomJsonExporter.Plan(Session.Document, selectedIds);
                // Validate every destination before reading any existing file.
                foreach (var entry in exportPlan) Files.Map(Path.Combine(S(command, "directory", "Exports"), entry.FileName));
                if (scope == "changed") exportPlan = exportPlan.Where(e => !ExportMatches(Path.Combine(target, e.FileName), e.Json)).ToArray();
                foreach (var entry in exportPlan) Files.Map(Path.Combine(S(command, "directory", "Exports"), entry.FileName));
                if (scope == "changed" && exportPlan.Count == 0) { notice = "@exportUnchanged"; break; }
                string? exportWarning = null;
                var exported = MapRoomJsonExporter.ExportPlanned(exportPlan, target, B(command, "overwrite"), Session.FilePath,
                    warning => exportWarning = warning);
                notice = $"@exportedRooms:{exported.Length}";
                if (exportWarning != null) notice += " Cleanup needs attention: " + exportWarning;
                break;
            default: throw new ArgumentException("Unknown editor command: " + action);
        }
        Revision++;
        conflictRevision = Revision;
    }
    private void Options(JsonElement command)
    {
        // Parse every fallible value before applying any UI state.
        foreach (string key in new[] { "tool", "layer", "shape" })
            if (command.TryGetProperty(key, out var value))
            {
                if (key == "tool") EnumValue<MetroidvaniaStudioTool>(value);
                else if (key == "layer") EnumValue<MapLayer>(value);
                else EnumValue<TileShape>(value);
            }
        foreach (string key in new[] { "material", "objectDefinition", "groupId" })
            if (command.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.String) throw new ArgumentException(key + " must be a string.");
        bool changesLayer = command.TryGetProperty("layer", out var targetLayerValue);
        MapLayer targetLayer = changesLayer ? EnumValue<MapLayer>(targetLayerValue) : Canvas.Layer;
        string targetGroup = command.TryGetProperty("groupId", out var targetGroupValue)
            ? targetGroupValue.GetString()! : changesLayer ? "" : Canvas.ActiveGroupId;
        if (targetGroup.Length > 0 && !Session.Document.layerGroups.Any(group => group.id == targetGroup && group.layer == targetLayer))
            throw new ArgumentException($"Unknown layer group '{targetGroup}' for {targetLayer}.", "groupId");
        if (command.TryGetProperty("brushSize", out var number))
        {
            int brushSize = number.GetInt32();
            if (brushSize < 1 || brushSize > MapBrushGeometry.MaximumBrushSize)
                throw new ArgumentOutOfRangeException("brushSize", $"Brush size must be between 1 and {MapBrushGeometry.MaximumBrushSize}.");
        }
        if (command.TryGetProperty("filled", out var flag)) flag.GetBoolean();
        foreach (string key in new[] { "hiddenLayers", "lockedLayers" })
            if (command.TryGetProperty(key, out var values)) foreach (var value in values.EnumerateArray()) EnumValue<MapLayer>(value);
        if (Canvas.RoomEditor.SelectedIds.Count > 1 && Canvas.ActiveRoomId != null
            && (command.TryGetProperty("tool", out var selectedTool) && EnumValue<MetroidvaniaStudioTool>(selectedTool) != MetroidvaniaStudioTool.Rooms
                || command.TryGetProperty("layer", out _))) Canvas.SelectRoom(Canvas.ActiveRoomId);
        if (command.TryGetProperty("tool", out var tool)) Canvas.Tool = EnumValue<MetroidvaniaStudioTool>(tool);
        if (command.TryGetProperty("layer", out var layer)) { Canvas.Layer = EnumValue<MapLayer>(layer); Canvas.ActiveGroupId = ""; }
        if (command.TryGetProperty("shape", out var shape)) Canvas.Shape = EnumValue<TileShape>(shape);
        if (command.TryGetProperty("material", out var material)) Canvas.Material = material.GetString();
        if (command.TryGetProperty("brushSize", out var size)) Canvas.BrushSize = size.GetInt32();
        if (command.TryGetProperty("filled", out var filled)) Canvas.Filled = filled.GetBoolean();
        if (command.TryGetProperty("objectDefinition", out var definition)) Canvas.ObjectDefinition = definition.GetString();
        if (command.TryGetProperty("groupId", out var group)) Canvas.ActiveGroupId = group.GetString();
        if (command.TryGetProperty("hiddenLayers", out var hidden)) { Canvas.HiddenLayers.Clear(); foreach (var value in hidden.EnumerateArray()) Canvas.HiddenLayers.Add(EnumValue<MapLayer>(value)); }
        if (command.TryGetProperty("lockedLayers", out var locked)) { Canvas.LockedLayers.Clear(); foreach (var value in locked.EnumerateArray()) Canvas.LockedLayers.Add(EnumValue<MapLayer>(value)); }
    }
    private void RequireGesture(string owner) { if (gestureOwner != owner) throw new WorkspaceConflict("The editing gesture expired or was cancelled."); }
    private bool shuttingDown;

    /// <summary>Call under Gate, then flush workers outside Gate before validating.</summary>
    public void BeginShutdown()
    {
        if (shuttingDown) throw new InvalidOperationException("Shutdown is already in progress.");
        Cancel();
        QueueRecovery();
        QueueAutoExport();
        shuttingDown = true;
    }
    public void ValidateShutdown()
    {
        if (recoveryNotice != null) throw new IOException(recoveryNotice);
        EditorExportStatus output = autoExporter.Status(Canvas.ActiveRoomId);
        if (output.phase != "saved") throw new IOException(output.error ?? "Room JSON export has not completed.");
    }
    public void AbortShutdown() => shuttingDown = false;

    public void Cancel() { eraser.Cancel(); Canvas.CancelGesture(); gestureOwner = null; placementStart = null; }
    private void TileGesture(JsonElement command, string owner)
    {
        if (!command.TryGetProperty("roomId", out var roomValue) || roomValue.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(roomValue.GetString()))
            throw new ArgumentException("tileGesture roomId must be a non-empty string.");
        string roomId = roomValue.GetString()!;
        if (!command.TryGetProperty("erase", out var eraseValue)
            || eraseValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("tileGesture erase must be a boolean.");
        bool erase = eraseValue.GetBoolean();
        if (!command.TryGetProperty("points", out var pointValues) || pointValues.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("tileGesture points must be an array.");
        int count = pointValues.GetArrayLength();
        if (count == 0) throw new ArgumentException("tileGesture needs at least one point.");
        if (count > MaximumTileGesturePoints)
            throw new ArgumentException($"tileGesture supports at most {MaximumTileGesturePoints} points.");

        var points = new List<Vector2Int>(count);
        foreach (JsonElement point in pointValues.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object
                || !point.TryGetProperty("x", out var x) || x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out int localX)
                || !point.TryGetProperty("y", out var y) || y.ValueKind != JsonValueKind.Number || !y.TryGetInt32(out int localY))
                throw new ArgumentException("Every tileGesture point needs integer x and y coordinates.");
            points.Add(new Vector2Int(localX, localY));
        }
        bool continuousBrush = erase || Canvas.Tool is MetroidvaniaStudioTool.Brush or MetroidvaniaStudioTool.Placement;
        long rasterPathLength = 1;
        if (continuousBrush) for (int index = 1; index < points.Count; index++)
        {
            long deltaX = Math.Abs((long)points[index].x - points[index - 1].x);
            long deltaY = Math.Abs((long)points[index].y - points[index - 1].y);
            rasterPathLength += Math.Max(deltaX, deltaY);
            if (rasterPathLength > MaximumTileGesturePoints)
                throw new ArgumentException($"tileGesture raster path supports at most {MaximumTileGesturePoints} points.");
        }
        if (Session.Document.rooms.All(room => room.id != roomId)) throw new ArgumentException("Unknown room.");
        if (!Canvas.IsTileLayer) throw new ArgumentException("tileGesture requires a tile layer.");
        RequireBaseRevision(command, "tileGesture");
        var rasterPath = new List<Vector2Int>();
        if (continuousBrush)
        {
            long brushSize = Canvas.BrushSize;
            long expandedWork = 3 * brushSize * brushSize + (rasterPathLength - 1) * (2 * brushSize - 1);
            if (expandedWork > MaximumTileGestureWork)
                throw new ArgumentException($"tileGesture expanded work cannot exceed {MaximumTileGestureWork} cells.");
            rasterPath.Capacity = (int)rasterPathLength;
            rasterPath.Add(points[0]);
            for (int index = 1; index < points.Count; index++)
            {
                bool first = true;
                foreach (Vector2Int point in MapBrushGeometry.Line(points[index - 1], points[index]))
                {
                    if (first) { first = false; continue; }
                    rasterPath.Add(point);
                }
            }
        }
        else
        {
            // Shape, selection, and bucket tools only consume their first/final
            // sample. Pointer jitter between them must not allocate a raster path.
            rasterPath.Add(points[0]);
            if (points[^1] != points[0]) rasterPath.Add(points[^1]);
        }

        // No state changes occur above this line. Once capture begins, Command's
        // failure path owns cancellation and restores the transaction snapshot.
        Cancel();
        // Selecting the already-active room clears tile/object selection. Compact
        // tile responses deliberately omit unchanged selection, so preserve it
        // when the gesture already targets this room. A real room switch still
        // resets selection and is included by the HTTP response.
        if (Canvas.ActiveRoomId != roomId) Canvas.SelectRoom(roomId);
        gestureOwner = owner;
        gestureAt = DateTime.UtcNow;
        if (erase) Canvas.BeginEraseGesture(points[0]); else Canvas.BeginGesture(points[0]);
        Canvas.DragGesturePath(rasterPath);
        Canvas.EndGesture(points[^1]);
        gestureOwner = null;
        placementStart = null;
    }
    private void ObjectGesture(JsonElement command, string owner)
    {
        if (!command.TryGetProperty("roomId", out var roomValue) || roomValue.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(roomValue.GetString()))
            throw new ArgumentException("objectGesture roomId must be a non-empty string.");
        string roomId = roomValue.GetString()!;
        MapRoom room = RequireRoom(roomId);
        if (!command.TryGetProperty("erase", out var eraseValue)
            || eraseValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("objectGesture erase must be a boolean.");
        bool erase = eraseValue.GetBoolean();
        if (!command.TryGetProperty("points", out var pointValues) || pointValues.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("objectGesture points must be an array.");
        int count = pointValues.GetArrayLength();
        if (count == 0) throw new ArgumentException("objectGesture needs at least one point.");
        if (count > MaximumTileGesturePoints)
            throw new ArgumentException($"objectGesture supports at most {MaximumTileGesturePoints} points.");
        if (Canvas.IsTileLayer) throw new ArgumentException("objectGesture requires an object layer.");
        if (!erase && Canvas.Tool != MetroidvaniaStudioTool.Placement)
            throw new ArgumentException("A non-erasing objectGesture requires the Placement tool.");
        RequireBaseRevision(command, "objectGesture");
        if (!erase && !Catalog.Objects.ContainsKey(Canvas.ObjectDefinition))
            throw new InvalidOperationException("Object definition is missing from the resource catalog.");
        if (erase && (long)count * room.objects.Count > MaximumObjectGestureWork)
            throw new ArgumentException($"objectGesture intersection work cannot exceed {MaximumObjectGestureWork} checks.");

        List<Vector2>? points = erase ? new List<Vector2>(count) : null;
        Vector2 firstPoint = default, lastPoint = default;
        int pointIndex = 0;
        foreach (JsonElement point in pointValues.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object
                || !point.TryGetProperty("x", out JsonElement x) || x.ValueKind != JsonValueKind.Number
                || !point.TryGetProperty("y", out JsonElement y) || y.ValueKind != JsonValueKind.Number)
                throw new ArgumentException("Every objectGesture point needs finite x and y coordinates.");
            Vector2 parsed = Point(point);
            if (pointIndex++ == 0) firstPoint = parsed;
            lastPoint = parsed;
            points?.Add(parsed);
        }

        Cancel();
        Canvas.SelectRoom(roomId);
        gestureOwner = owner;
        gestureAt = DateTime.UtcNow;
        if (erase)
        {
            eraser.Begin(firstPoint);
            if (points!.Count > 1) eraser.DragPath(points);
            eraser.End();
        }
        else
        {
            placementStart = firstPoint;
            Place(lastPoint);
        }
        gestureOwner = null;
        placementStart = null;
    }

    private void RequireBaseRevision(JsonElement command, string action)
    {
        if (!command.TryGetProperty("baseRevision", out var revisionValue) || revisionValue.ValueKind != JsonValueKind.Number
            || !revisionValue.TryGetInt64(out long baseRevision))
            throw new ArgumentException(action + " baseRevision must be an integer.");
        try { RequireMutationRevision(baseRevision); }
        catch (WorkspaceConflict) { throw new WorkspaceConflict("The canvas options changed after this " + action + " started."); }
    }
    private void RequireMutationRevision(long expectedRevision)
    {
        if (expectedRevision < conflictRevision || expectedRevision > Revision)
            throw new WorkspaceConflict("The document changed in another view. Reloaded state is required before editing.");
    }
    private static string RequiredClientId(JsonElement command)
    {
        if (!command.TryGetProperty("clientId", out JsonElement client) || client.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(client.GetString()) || client.GetString()!.Length > 128)
            throw new ArgumentException("Every editor command needs a non-empty clientId of at most 128 characters.");
        return client.GetString()!;
    }
    private static string RequiredCommandId(JsonElement command)
    {
        if (!command.TryGetProperty("commandId", out JsonElement id) || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 128)
            throw new ArgumentException("Every editor command needs a non-empty commandId of at most 128 characters.");
        return id.GetString()!;
    }
    private void Place(Vector2 end)
    {
        var room = Canvas.Room;
        if (room == null || !room.visible || room.locked || !Canvas.CanEditMember(Canvas.Layer, Canvas.ActiveGroupId)) return;
        var start = placementStart!.Value;
        var definition = Catalog.Objects.GetValueOrDefault(Canvas.ObjectDefinition) ?? throw new InvalidOperationException("Object definition is missing from the resource catalog.");
        var at = definition.placement == MapPlacementKind.Rectangle
            ? new Vector2(MathEx.Floor(MathEx.Min(start.x, end.x)), MathEx.Floor(MathEx.Min(start.y, end.y)))
            : start;
        var size = definition.placement == MapPlacementKind.Rectangle
            ? new Vector2(MathEx.Abs(MathEx.Floor(end.x) - MathEx.Floor(start.x)) + 1, MathEx.Abs(MathEx.Floor(end.y) - MathEx.Floor(start.y)) + 1) : definition.defaultSize;
        // ObjectEditor performs final definition clamps, room-bound validation and
        // records a compact incremental history entry.
        Canvas.ObjectEditor.Place(definition, at, size,
            definition.placement == MapPlacementKind.Nodes ? end : null);
    }
    private static bool ExportMatches(string path, string json)
    {
        if (!File.Exists(path)) return false;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        if (new FileInfo(path).Length != bytes.Length) return false;
        using var stream = File.OpenRead(path);
        return System.Security.Cryptography.SHA256.HashData(stream).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(bytes));
    }
    private void ImportRooms(JsonElement command)
    {
        if (!command.TryGetProperty("documents", out var documents) || documents.ValueKind != JsonValueKind.Array || documents.GetArrayLength() == 0)
            throw new ArgumentException("Choose at least one room JSON document.");
        var candidate = Session.Document.Clone();
        var incoming = new List<MapRoom>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var json in documents.EnumerateArray())
        {
            var imported = MapDocumentStore.Deserialize(json.GetRawText());
            foreach (var group in imported.layerGroups)
            {
                var existing = candidate.layerGroups.Find(g => g.id == group.id);
                if (existing == null) candidate.layerGroups.Add(group);
                else if (MapJson.ToJson(existing) != MapJson.ToJson(group)) throw new WorkspaceConflict("A layer group uses the same ID with different settings.");
            }
            foreach (var room in imported.rooms)
            {
                if (!ids.Add(room.id)) throw new ArgumentException("The chosen JSON files contain duplicate room IDs.");
                int index = candidate.rooms.FindIndex(r => r.id == room.id);
                if (index >= 0)
                {
                    if (!B(command, "overwrite")) throw new WorkspaceConflict("A room already exists. Confirm overwrite to replace it.");
                    if (candidate.rooms[index].locked) throw new InvalidOperationException("Unlock the existing room before importing a replacement.");
                    candidate.rooms[index] = room;
                }
                else candidate.rooms.Add(room);
                incoming.Add(room);
            }
        }
        candidate.Validate();
        Session.Execute("Import rooms", d => { d.rooms = candidate.rooms; d.layerGroups = candidate.layerGroups; });
        SelectRoomResult(incoming.Select(r => r.id).ToArray());
    }
    private void SelectRoomResult(string[] ids)
    {
        if (ids.Length == 0) return;
        Canvas.SelectRoom(ids[0]);
        for (int i = 1; i < ids.Length; i++) Canvas.RoomEditor.Select(ids[i], additive: true);
    }
    private void MoveRoom(JsonElement command)
    {
        string id = S(command, "id");
        var room = RequireRoom(id);
        var delta = new Vector2Int(I(command, "dx"), I(command, "dy"));
        if (B(command, "selected"))
        {
            if (!Canvas.RoomEditor.SelectedIds.Contains(id)) throw new ArgumentException("The drag target is not selected.");
            var selected = Session.Document.rooms.Where(r => Canvas.RoomEditor.SelectedIds.Contains(r.id)).ToArray();
            if (selected.Any(r => r.locked)) throw new InvalidOperationException("@roomSelectionLocked");
            delta = MapRoomCollision.Resolve(selected, delta, Session.Document.rooms);
            Canvas.RoomEditor.MoveSelected(delta);
            return;
        }
        delta = MapRoomCollision.Resolve(room, delta, Session.Document.rooms);
        if (room.locked) throw new InvalidOperationException("Room '" + room.name + "' is locked. Unlock it before moving it.");
        long x = (long)room.x + delta.x, y = (long)room.y + delta.y;
        if (x < int.MinValue || y < int.MinValue || x + room.width > int.MaxValue || y + room.height > int.MaxValue)
            throw new ArgumentOutOfRangeException("room", "The moved room exceeds the supported coordinate range.");
        Canvas.SelectRoom(id);
        Canvas.RoomEditor.MoveSelected(delta);
    }
    private MapRoom RequireRoom(string id) => Session.Document.rooms.Find(room => room.id == id)
        ?? throw new ArgumentException("Unknown room: " + id, nameof(id));
    private void DeleteRoom(JsonElement command)
    {
        string id = S(command, "id");
        MapRoom room = RequireRoom(id);
        if (room.locked) throw new InvalidOperationException("Unlock the room before deleting it.");
        Canvas.SelectRoom(id);
        Canvas.RoomEditor.DeleteSelected();
    }
    private static MapRoomSnapBounds Bounds(MapRoom room) => new(room.id, new RectInt(room.x, room.y, room.width, room.height), room.visible);
    private void ConfigureRoom(JsonElement command)
    {
        // Prepare on a detached session so the whole form remains one atomic Undo.
        // When size and position arrive together, plan against the complete final
        // rectangle while retaining the inspector's local-content semantics.
        var candidate = new MapEditSession(Session.Document);
        string id = S(command, "id");
        var room = candidate.Document.rooms.Single(r => r.id == id);
        int x = I(command, "x", room.x), y = I(command, "y", room.y);
        int width = I(command, "width", room.width), height = I(command, "height", room.height);
        bool locked = B(command, "locked", room.locked);
        if (room.locked && locked && (x != room.x || y != room.y || width != room.width || height != room.height))
            throw new InvalidOperationException("Unlock the room before moving or resizing it.");
        if (!locked) room.locked = false;
        if (width != room.width || height != room.height)
        {
            using var editing = new MapRoomEditing(candidate);
            editing.ResizeKeepingLocalContents(id, new RectInt(x, y, width, height), B(command, "crop"));
            room = candidate.Document.rooms.Single(r => r.id == id);
        }
        else { room.x = x; room.y = y; }
        room.locked = locked;
        if (command.TryGetProperty("name", out var name)) room.name = name.GetString();
        if (command.TryGetProperty("visible", out var visible)) room.visible = visible.GetBoolean();
        if (command.TryGetProperty("properties", out var props)) room.properties = Read<List<MapProperty>>(props);
        candidate.Document.Validate();
        Session.Execute("Room properties", document => document.rooms = candidate.Document.rooms);
    }
    private void TransformObject(JsonElement command)
    {
        var item = Canvas.ObjectEditor.SelectedObjects.SingleOrDefault() ?? throw new ArgumentException("Select one object to edit its transform.");
        var definition = Catalog.Objects.GetValueOrDefault(item.definition);
        Session.Execute("Object transform", _ =>
        {
            float width = F(command, "width", item.width), height = F(command, "height", item.height);
            if (definition != null && (!definition.resizable && (width != item.width || height != item.height) || width < definition.minimumSize.x || height < definition.minimumSize.y))
                throw new InvalidOperationException("This size is not supported by the object definition.");
            float rotation = F(command, "rotation", item.rotation), sx = F(command, "scaleX", item.scaleX), sy = F(command, "scaleY", item.scaleY);
            if (definition != null && (!definition.rotatable && rotation != item.rotation || !definition.flippable && (Math.Sign(sx) != Math.Sign(item.scaleX) || Math.Sign(sy) != Math.Sign(item.scaleY))))
                throw new InvalidOperationException("This transform is not supported by the object definition.");
            float x = F(command, "x", item.x), y = F(command, "y", item.y);
            if (x < 0 || y < 0 || x + width > Canvas.Room.width || y + height > Canvas.Room.height || sx == 0 || sy == 0) throw new ArgumentException("The object must fit inside its room and have a nonzero scale.");
            var delta = new Vector2(x - item.x, y - item.y); for (int n = 0; n < item.nodes.Count; n++) item.nodes[n] += delta;
            item.x = x; item.y = y; item.width = width; item.height = height; item.rotation = rotation; item.scaleX = sx; item.scaleY = sy;
        });
    }
    private void Save(JsonElement command)
    {
        string path = Files.Map(S(command, "path", Session.FilePath == null ? "Untitled.map.json" : Files.Relative(Session.FilePath)));
        bool same = string.Equals(path, Session.FilePath, ProjectFiles.PathComparison);
        // Saving is the destructive conflict boundary, so always hash the current
        // bytes. The metadata-gated hash cache is intentionally limited to polling:
        // another process can preserve a file's length and timestamp while changing
        // its contents.
        ProjectFiles.DiskFingerprint? current = ProjectFiles.Fingerprint(path);
        if (same && (observedDiskMissing || !ProjectFiles.SameContent(current, diskFingerprint)))
            throw new WorkspaceConflict("The map changed on disk. Reopen it or save under a different name.");
        bool overwrite = B(command, "overwrite");
        if (!same && current.HasValue && !overwrite)
            throw new WorkspaceConflict("This map already exists. Choose another name or confirm overwrite.");
        // Oversized markers deliberately do not hash file contents. They are
        // safe for conflict rejection, but can never serve as an exact Save As
        // compare-and-swap baseline.
        if (!same && current.HasValue && current.Value.Length > MapDocumentStore.MaximumFileBytes)
            throw new WorkspaceConflict("The existing map is too large to overwrite safely. Choose another name.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // This durable intent always contains the latest document. If the
        // process stops before asynchronous stale-recovery cleanup, startup can
        // validate the named target and can never reopen an older dirty copy.
        PersistSaveIntent(path);
        string persistedJson;
        try
        {
            persistedJson = Session.SaveAndGetPersistedJson(path,
                (destination, json) => Files.PublishMap(destination, json, current));
        }
        catch (MapWriteConflictException error)
        { throw new WorkspaceConflict(error.Message); }
        TrackSavedFile(persistedJson);

    }
    private void TrackOpenedFile(ProjectFiles.DiskFingerprint fingerprint)
    {
        InvalidateDiskContentProbe();
        diskFingerprint = fingerprint;
        observedDiskFingerprint = fingerprint;
        rejectedDiskFingerprint = null;
        observedDiskMissing = false;
        SetDiskHealthNotice(null);
    }
    private void TrackSavedFile(string persistedJson)
    {
        InvalidateDiskContentProbe();
        // Use the bytes we actually handed to the atomic writer. A replacement
        // immediately after Save remains different from this baseline and cannot
        // be overwritten by a later save without an explicit reopen/save-as.
        diskFingerprint = ProjectFiles.FingerprintUtf8(persistedJson);
        observedDiskFingerprint = null;
        rejectedDiskFingerprint = null;
        observedDiskMissing = false;
        SetDiskHealthNotice(null);
    }
    private void ClearRecovery() { if (File.Exists(recoveryPath)) File.Delete(recoveryPath); }
    private static T Read<T>(JsonElement json) => JsonSerializer.Deserialize<T>(json, Catalog.Json) ?? throw new ArgumentException("Missing data.");
    private static T EnumValue<T>(JsonElement value) where T : struct, Enum { var result = (T)Enum.ToObject(typeof(T), value.GetInt32()); return Enum.IsDefined(result) ? result : throw new ArgumentException("Unknown " + typeof(T).Name); }
    private static string S(JsonElement c, string key, string fallback = "") => c.TryGetProperty(key, out var v) ? v.GetString() ?? fallback : fallback;
    private static int I(JsonElement c, string key, int fallback = 0) => c.TryGetProperty(key, out var v) ? v.GetInt32() : fallback;
    private static float F(JsonElement c, string key, float fallback = 0) { float v = c.TryGetProperty(key, out var p) ? p.GetSingle() : fallback; return float.IsFinite(v) ? v : throw new ArgumentException("Coordinates must be finite."); }
    private static bool B(JsonElement c, string key, bool fallback = false) => c.TryGetProperty(key, out var v) ? v.GetBoolean() : fallback;
    private static Vector2 Point(JsonElement c) => new(F(c, "x"), F(c, "y"));
    private static Vector2Int Cell(JsonElement c) => new(MathEx.FloorToInt(F(c, "x")), MathEx.FloorToInt(F(c, "y")));
}
public sealed class WorkspaceConflict(string message) : Exception(message);
