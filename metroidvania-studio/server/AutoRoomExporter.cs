using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetroidvaniaStudio;

namespace MetroidvaniaStudio.Server;

/// <summary>Debounces immutable snapshots; all parsing, hashing and durable IO stay off the editor gate.</summary>
public sealed class AutoRoomExporter : IDisposable
{
    public const int IdleMilliseconds = 1000;
    private const int RetryMilliseconds = 3000;
    private const int MaximumManifestBytes = 2 * 1024 * 1024;
    private readonly ProjectFiles files;
    private readonly Action<string?> report;
    private readonly object gate = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private Snapshot? pending;
    private Task? worker;
    private long version, due;
    private bool stopped, flushing;
    private long statusRevision;
    private string phase = "queued", publishedMapKey = "";
    private string? statusError;
    private Dictionary<string, PublishedRoom> publishedRooms = new(StringComparer.Ordinal);
    private Dictionary<string, long> measuredRoomBytes = new(StringComparer.Ordinal);
    private long? measuredTotalBytes;
    private long measuredVersion = -1;
    private sealed record PublishedRoom(string Path, string Hash);
    private sealed record ExportResult(string? Error, Dictionary<string, PublishedRoom> Rooms, Dictionary<string, long> RoomBytes, long TotalBytes);

    public long StatusRevision { get { lock (gate) return statusRevision; } }

    // Only selected identities and aggregate byte counts cross the wire.
    // State polling never serializes rooms, hashes tiles or reads their files.
    public EditorExportStatus Status(string? roomId, IReadOnlyCollection<string>? selectedIds = null)
    {
        lock (gate)
        {
            publishedRooms.TryGetValue(roomId ?? "", out PublishedRoom? room);
            long? selectedBytes = 0;
            if (selectedIds != null)
            {
                foreach (string id in selectedIds)
                {
                    if (!measuredRoomBytes.TryGetValue(id, out long bytes)) { selectedBytes = null; break; }
                    selectedBytes += bytes;
                }
            }
            else if (roomId != null) selectedBytes = measuredRoomBytes.TryGetValue(roomId, out long bytes) ? bytes : null;
            return new EditorExportStatus(phase, version, statusError, roomId, room?.Path, room?.Hash,
                selectedBytes, measuredTotalBytes, measuredVersion != version);
        }
    }
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record Snapshot(string Json, string? SavedPath, long Version);
    private sealed class Manifest
    {
        public int FormatVersion { get; set; } = 1;
        public List<Entry> Entries { get; set; } = [];
    }
    private sealed class Entry
    {
        public string RoomKey { get; set; } = "";
        public string FileName { get; set; } = "";
        public string SourceHash { get; set; } = "";
        public string Hash { get; set; } = "";
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string? PreviousHash { get; set; }
        public long PreviousLength { get; set; }
        public bool Pending { get; set; }
        public bool Obsolete { get; set; }
    }
    private sealed record Publication(Entry Entry, string Json, ProjectFiles.DiskFingerprint? Expected, string? Conflict);

    public AutoRoomExporter(ProjectFiles files, Action<string?> report)
    { this.files = files; this.report = report; }

    /// <summary>Constant work: retain one latest JSON reference and reset its idle deadline.</summary>
    public void Queue(string documentJson, string? savedPath)
    {
        lock (gate)
        {
            if (stopped) return;
            pending = new Snapshot(documentJson, savedPath, ++version);
            string key = MapKey(savedPath);
            if (key != publishedMapKey)
            {
                publishedRooms = new(StringComparer.Ordinal); publishedMapKey = key;
                measuredRoomBytes = new(StringComparer.Ordinal); measuredTotalBytes = null; measuredVersion = -1;
            }
            phase = "queued"; statusError = null; statusRevision++;
            due = Environment.TickCount64 + (flushing ? 0 : IdleMilliseconds);
            worker ??= Task.Run(Run);
        }
        Signal();
    }

    public void Flush()
    {
        Task? active;
        lock (gate) { flushing = true; due = 0; active = worker; }
        Signal();
        active?.GetAwaiter().GetResult();
        lock (gate) flushing = false;
    }

    public void Dispose()
    {
        Task? active;
        lock (gate) { stopped = true; pending = null; active = worker; }
        Signal();
        active?.GetAwaiter().GetResult();
    }

    private void Signal()
    {
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private bool Current(Snapshot snapshot)
    { lock (gate) return !stopped && snapshot.Version == version; }

    private async Task Run()
    {
        while (true)
        {
            Snapshot snapshot;
            int delay;
            lock (gate)
            {
                if (stopped || pending == null) { worker = null; return; }
                delay = (int)Math.Clamp(due - Environment.TickCount64, 0, RetryMilliseconds);
                snapshot = pending;
                if (delay == 0) { pending = null; phase = "exporting"; statusRevision++; }
            }
            if (delay > 0) { await wake.WaitAsync(delay).ConfigureAwait(false); continue; }
            string? error = null;
            ExportResult? result = null;
            try { result = Export(snapshot); error = result?.Error; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or ArgumentException or InvalidOperationException)
            { error = exception.Message; }
            if (!Current(snapshot)) continue;
            lock (gate)
            {
                // A newer queue may arrive between IO completion and publication.
                if (stopped || snapshot.Version != version) continue;
                if (result != null)
                {
                    publishedRooms = result.Rooms; measuredRoomBytes = result.RoomBytes;
                    measuredTotalBytes = result.TotalBytes; measuredVersion = snapshot.Version;
                }
                phase = error == null ? "saved" : "error"; statusError = error; statusRevision++;
            }
            report(error == null ? null : "Room JSON auto-export needs attention: " + error);
            lock (gate)
            {
                if (error != null && !stopped && !flushing && snapshot.Version == version && pending == null)
                { pending = snapshot; due = Environment.TickCount64 + RetryMilliseconds; }
            }
        }
    }

    public static string MapKey(string? savedRelativePath)
    {
        if (savedRelativePath == null) return "Workspace";
        string identity = savedRelativePath.Replace('\\', '/');
        // Preserve existing Windows export paths. Case and Unicode spelling may
        // identify separate files on Unix, so folding there would mix their exports.
        if (OperatingSystem.IsWindows()) identity = identity.Normalize(NormalizationForm.FormC).ToUpperInvariant();
        return "Map-" + Hash(identity)[..20];
    }

    private ExportResult? Export(Snapshot snapshot)
    {
        string key = MapKey(snapshot.SavedPath);
        string manifestPath = files.AutoExportPath(key + "/.manifest");
        ProjectFiles.DiskFingerprint? manifestFingerprint = ProjectFiles.Fingerprint(manifestPath);
        Manifest previous = ReadManifest(manifestPath, manifestFingerprint);
        var previousNames = previous.Entries.ToDictionary(entry => entry.FileName, StringComparer.OrdinalIgnoreCase);
        using JsonDocument parsed = JsonDocument.Parse(snapshot.Json);
        JsonElement root = parsed.RootElement;
        var roomElements = root.GetProperty("rooms").EnumerateArray().ToArray();
        if (roomElements.Length > MapDocument.MaximumRoomCount) throw new InvalidDataException("Too many rooms to auto-export.");
        string sharedHash = Hash(WriteDocument(root, null));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var next = new Manifest();
        var publications = new List<Publication>();
        var roomIds = new Dictionary<string, string>(StringComparer.Ordinal);
        long totalBytes = 0;
        // IDs make duplicate-name suffixes stable when room ordering changes.
        foreach (JsonElement room in roomElements.OrderBy(room => room.GetProperty("id").GetString(), StringComparer.Ordinal))
        {
            if (!Current(snapshot)) return null;
            string roomKey = Hash(room.GetProperty("id").GetString()!);
            string fileName = UniqueName(room.GetProperty("name").GetString()!, names);
            roomIds[fileName] = room.GetProperty("id").GetString()!;
            string path = files.AutoExportPath(key + "/" + fileName);
            string sourceHash = Hash(sharedHash + room.GetRawText());
            previousNames.TryGetValue(fileName, out Entry? old);
            if (old != null && !old.Pending && !old.Obsolete && old.RoomKey == roomKey
                && old.SourceHash == sourceHash && UnchangedFile(path, old))
            {
                next.Entries.Add(old); totalBytes += old.Length;
                if (totalBytes > MapDocumentStore.MaximumFileBytes) throw SizeError();
                continue;
            }
            string json = WriteDocument(root, room);
            ProjectFiles.DiskFingerprint desired = ProjectFiles.FingerprintUtf8(json);
            totalBytes += desired.Length;
            if (totalBytes > MapDocumentStore.MaximumFileBytes) throw SizeError();
            ProjectFiles.DiskFingerprint? current = ProjectFiles.Fingerprint(path);
            bool owned = !current.HasValue || old != null && Owns(old, current.Value);
            var entry = new Entry { RoomKey = roomKey, FileName = fileName, SourceHash = sourceHash,
                Hash = desired.Hash, Length = desired.Length, Pending = true,
                PreviousHash = owned ? current?.Hash : old?.Hash,
                PreviousLength = owned ? current?.Length ?? 0 : old?.Length ?? 0 };
            next.Entries.Add(entry);
            publications.Add(new Publication(entry, json, current, owned ? null : "An externally changed file was preserved: " + fileName));
        }
        foreach (Entry old in previous.Entries)
            if (!names.Contains(old.FileName)) { old.Obsolete = true; next.Entries.Add(old); }
        if (next.Entries.Count > MapDocument.MaximumRoomCount * 2)
            throw new InvalidDataException("Too many preserved auto-export conflicts; resolve the reported old files first.");
        if (publications.Count == 0 && next.Entries.All(entry => !entry.Obsolete)) return Completed(null);
        if (!Current(snapshot)) return null;
        // Record both the prior and intended bytes BEFORE publication. A crash can
        // then resume an interrupted export without treating our own files as foreign.
        manifestFingerprint = WriteManifest(manifestPath, next, manifestFingerprint);
        var errors = new List<string>();
        foreach (Publication publication in publications)
        {
            if (!Current(snapshot)) return null;
            Entry entry = publication.Entry;
            try
            {
                if (publication.Conflict != null) throw new IOException(publication.Conflict);
                string path = files.AutoExportPath(key + "/" + entry.FileName);
                if (publication.Expected?.Hash != entry.Hash)
                    files.PublishMap(path, publication.Json, publication.Expected);
                entry.LastWriteUtcTicks = File.GetLastWriteTimeUtc(path).Ticks;
                entry.Pending = false; entry.PreviousHash = null; entry.PreviousLength = 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { errors.Add(error.Message); }
        }
        foreach (Entry entry in next.Entries.Where(entry => entry.Obsolete).ToArray())
        {
            if (!Current(snapshot)) return null;
            try
            {
                string path = files.AutoExportPath(key + "/" + entry.FileName);
                ProjectFiles.DiskFingerprint? current = ProjectFiles.Fingerprint(path);
                if (current.HasValue && !Owns(entry, current.Value))
                    throw new IOException("An externally changed old file was preserved: " + entry.FileName);
                if (current.HasValue) files.DeleteAutoExport(path, current.Value);
                next.Entries.Remove(entry);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { errors.Add(error.Message); }
        }
        if (!Current(snapshot)) return null;
        WriteManifest(manifestPath, next, manifestFingerprint);
        return Completed(errors.Count == 0 ? null : string.Join("; ", errors.Take(4)));

        ExportResult Completed(string? error)
        {
            var rooms = new Dictionary<string, PublishedRoom>(StringComparer.Ordinal);
            var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (Entry entry in next.Entries)
                if (!entry.Obsolete && roomIds.TryGetValue(entry.FileName, out string? id))
                {
                    sizes[id] = entry.Length;
                    if (!entry.Pending) rooms[id] = new PublishedRoom(files.MapsLabel + "/AutoExport/" + key + "/" + entry.FileName,
                        Convert.ToBase64String(Convert.FromHexString(entry.Hash)));
                }
            return new ExportResult(error, rooms, sizes, totalBytes);
        }
    }

    private static bool Owns(Entry entry, ProjectFiles.DiskFingerprint fingerprint) =>
        entry.Hash == fingerprint.Hash && entry.Length == fingerprint.Length
        || entry.PreviousHash == fingerprint.Hash && entry.PreviousLength == fingerprint.Length;

    private static bool UnchangedFile(string path, Entry entry)
    {
        ProjectFiles.DiskMetadata? metadata = ProjectFiles.Metadata(path);
        if (!metadata.HasValue || metadata.Value.Length != entry.Length) return false;
        if (metadata.Value.LastWriteUtcTicks == entry.LastWriteUtcTicks) return true;
        ProjectFiles.DiskFingerprint? current = ProjectFiles.Fingerprint(path);
        if (!current.HasValue || current.Value.Hash != entry.Hash) return false;
        entry.LastWriteUtcTicks = current.Value.LastWriteUtcTicks;
        return true;
    }

    private static Manifest ReadManifest(string path, ProjectFiles.DiskFingerprint? expected)
    {
        if (!expected.HasValue) return new Manifest();
        if (expected.Value.Length > MaximumManifestBytes) throw new InvalidDataException("Auto-export manifest exceeds its size limit.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaximumManifestBytes) throw new InvalidDataException("Auto-export manifest exceeds its size limit.");
        using var memory = new MemoryStream();
        byte[] buffer = new byte[8192]; int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (memory.Length + read > MaximumManifestBytes) throw new InvalidDataException("Auto-export manifest exceeds its size limit.");
            memory.Write(buffer, 0, read);
        }
        string json = Utf8.GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
        if (!ProjectFiles.SameContent(ProjectFiles.FingerprintUtf8(json), expected))
            throw new IOException("Auto-export manifest changed while being read.");
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(json, Json) ?? throw new InvalidDataException("Invalid auto-export manifest.");
        if (manifest.FormatVersion != 1 || manifest.Entries == null || manifest.Entries.Count > MapDocument.MaximumRoomCount * 2)
            throw new InvalidDataException("Unsupported auto-export manifest.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Entry entry in manifest.Entries)
            if (entry == null || string.IsNullOrWhiteSpace(entry.FileName) || entry.FileName.Length > 120
                || entry.FileName != Path.GetFileName(entry.FileName) || entry.FileName.Contains(':')
                || !entry.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !names.Add(entry.FileName)
                || !ValidHash(entry.Hash) || !ValidHash(entry.RoomKey) || !ValidHash(entry.SourceHash)
                || entry.PreviousHash != null && !ValidHash(entry.PreviousHash)
                || entry.Length < 0 || entry.Length > MapDocumentStore.MaximumFileBytes
                || entry.PreviousLength < 0 || entry.PreviousLength > MapDocumentStore.MaximumFileBytes)
                throw new InvalidDataException("Invalid auto-export ownership record.");
        return manifest;
    }

    private ProjectFiles.DiskFingerprint WriteManifest(string path, Manifest manifest, ProjectFiles.DiskFingerprint? expected)
    {
        string json = JsonSerializer.Serialize(manifest, Json);
        if (Utf8.GetByteCount(json) > MaximumManifestBytes) throw new InvalidDataException("Auto-export manifest exceeds its size limit.");
        files.PublishMap(files.AutoExportPath(files.Relative(path)["AutoExport/".Length..]), json, expected);
        return ProjectFiles.FingerprintUtf8(json);
    }

    private static bool ValidHash(string value) => value != null && value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(value)));
    private static InvalidDataException SizeError() => new("Combined room JSON exports exceed the 32 MiB limit.");

    private static string WriteDocument(JsonElement root, JsonElement? room)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Name != "rooms") { property.WriteTo(writer); continue; }
                writer.WriteStartArray("rooms");
                if (room.HasValue) room.Value.WriteTo(writer);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return Utf8.GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
    }

    private static string UniqueName(string name, HashSet<string> names)
    {
        var text = new StringBuilder();
        foreach (Rune value in name.EnumerateRunes())
            text.Append(Rune.IsControl(value) || "<>:\"/\\|?*".Contains(value.ToString(), StringComparison.Ordinal) ? "_" : value.ToString());
        string stem = text.ToString().Normalize(NormalizationForm.FormC).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(stem)) stem = "room";
        if (stem.StartsWith('.')) stem = "_" + stem;
        string device = stem.Split('.')[0].TrimEnd(' ');
        if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(device, StringComparer.OrdinalIgnoreCase)
            || device.Length == 4 && device[3] is >= '1' and <= '9'
                && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
            stem = "_" + stem;
        for (int number = 1; ; number++)
        {
            string suffix = number == 1 ? "" : " (" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            int length = Math.Min(stem.Length, 115 - suffix.Length);
            if (length > 0 && char.IsHighSurrogate(stem[length - 1])) length--;
            string prefix = stem[..length].TrimEnd(' ', '.');
            // Truncation followed by trimming can reveal a reserved device name.
            if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(prefix, StringComparer.OrdinalIgnoreCase)
                || prefix.Length == 4 && prefix[3] is >= '1' and <= '9'
                    && (prefix.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || prefix.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
                prefix = "_" + prefix;
            string fileName = prefix + suffix + ".json";
            if (names.Add(fileName)) return fileName;
        }
    }
}
