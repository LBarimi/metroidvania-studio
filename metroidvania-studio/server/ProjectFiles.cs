using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetroidvaniaStudio;

namespace MetroidvaniaStudio.Server;

/// <summary>Bounds all durable documents and assets to the selected workspace.</summary>
public sealed partial class ProjectFiles
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    // Unix volumes can distinguish case-only names, including optional macOS volumes.
    // Keep Windows identities compatible while never merging distinct Unix paths.
    internal static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string NormalizeSeparators(string value) => value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    private const FileShare CooperativeReadShare = FileShare.ReadWrite | FileShare.Delete;
    private const int ErrorUnableToRemoveReplaced = 1175;
    private const int RecoveryEnvelopeVersion = 1;
    private const long MaximumRecoveryEnvelopeBytes = MapDocumentStore.MaximumFileBytes + 64L * 1024;
    private readonly Action<string>? beforeMapPublish;
    private readonly Action<string>? beforeConditionalRollback;
    private readonly Action<string>? beforeConflictDestinationCheck;
    private readonly Action<string>? beforeMapProbe;

    public readonly struct DiskFingerprint
    {
        public readonly long Length;
        public readonly long LastWriteUtcTicks;
        public readonly string Hash;
        public DiskFingerprint(long length, long lastWriteUtcTicks, string hash)
        { Length = length; LastWriteUtcTicks = lastWriteUtcTicks; Hash = hash; }
    }

    public readonly struct DiskMetadata
    {
        public readonly long Length;
        public readonly long LastWriteUtcTicks;
        public DiskMetadata(long length, long lastWriteUtcTicks)
        { Length = length; LastWriteUtcTicks = lastWriteUtcTicks; }
    }

    internal readonly struct MapProbe
    {
        public readonly DiskFingerprint Fingerprint;
        public readonly MapDocument? Document;
        public readonly Exception? DocumentError;
        public MapProbe(DiskFingerprint fingerprint, MapDocument? document, Exception? documentError)
        { Fingerprint = fingerprint; Document = document; DocumentError = documentError; }
    }

    public readonly struct StableMap
    {
        public readonly MapDocument Document;
        public readonly DiskFingerprint Fingerprint;
        public StableMap(MapDocument document, DiskFingerprint fingerprint)
        { Document = document; Fingerprint = fingerprint; }
    }

    public readonly struct RecoveryMap
    {
        public readonly MapDocument Document;
        public readonly string DocumentJson;
        public readonly bool IsDirty;
        public readonly string? SavedPath;
        public readonly string? SavedDocumentHash;
        public readonly bool IsLegacy;
        public RecoveryMap(MapDocument document, string documentJson, bool isDirty, string? savedPath,
            string? savedDocumentHash, bool isLegacy)
        {
            Document = document;
            DocumentJson = documentJson;
            IsDirty = isDirty;
            SavedPath = savedPath;
            SavedDocumentHash = savedDocumentHash;
            IsLegacy = isLegacy;
        }
    }

    private readonly string mapsRelative;
    private readonly string catalogRelative;
    private readonly string texturesRelative;
    private readonly string? studioRoot;
    public string ProjectPath { get; }
    public string MapsPath { get; }
    public string AutoExportDirectory => Resolve(mapsRelative, "AutoExport");
    public string TexturesPath => Resolve("", texturesRelative);
    public string MapsLabel => mapsRelative.Replace('\\', '/');
    public string CatalogWritePath => Resolve("", catalogRelative);
    public string CatalogPath
    {
        get
        {
            string configured = Resolve("", catalogRelative);
            if (File.Exists(configured) || studioRoot == null) return configured;
            return ResolveWithin(studioRoot, "samples", "catalog.json");
        }
    }
    public string? SampleWorldPath => studioRoot == null ? null : ResolveWithin(studioRoot, "samples/maps", "starter-world.map.json");
    public string RecoveryPath => Resolve(mapsRelative, ".Recovery/Workspace.map.json");
    public ProjectFiles(string project, Action<string>? beforeMapPublish = null,
        Action<string>? beforeConditionalRollback = null, Action<string>? beforeConflictDestinationCheck = null,
        Action<string>? beforeMapProbe = null, string? studioRoot = null)
    {
        this.beforeMapPublish = beforeMapPublish;
        this.beforeConditionalRollback = beforeConditionalRollback;
        this.beforeConflictDestinationCheck = beforeConflictDestinationCheck;
        this.beforeMapProbe = beforeMapProbe;
        ProjectPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        this.studioRoot = studioRoot == null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(studioRoot));
        WorkspaceSettings settings = WorkspaceSettings.Load(ProjectPath);
        mapsRelative = settings.MapsRoot ?? "Maps";
        catalogRelative = settings.CatalogPath ?? ".studio/catalog.json";
        texturesRelative = settings.TexturesRoot ?? "Textures";
        MapsPath = Resolve("", mapsRelative);
        _ = Resolve("", catalogRelative);
        _ = Resolve("", texturesRelative);
        Directory.CreateDirectory(MapsPath);
    }

    public string Map(string relative)
    {
        if (!relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Map files need a .json extension.");
        string path = Resolve(mapsRelative, relative);
        RejectGeneratedPath(path);
        return path;
    }
    public string ExportDirectory(string relative)
    {
        string path = Resolve(mapsRelative, relative);
        RejectGeneratedPath(path);
        return path;
    }
    public string TextureFolder(string? asset)
    {
        if (string.IsNullOrEmpty(asset)) return TexturesPath;
        string file = Asset(asset);
        if (!File.Exists(file)) throw new FileNotFoundException("The texture was not found.");
        return Path.GetDirectoryName(file)!;
    }
    public string Asset(string relative)
    {
        if (!relative.StartsWith("Textures/", StringComparison.Ordinal)) throw new ArgumentException("Resources must use a Textures/ asset identifier.");
        string path = Resolve(texturesRelative, relative[9..]);
        if (!File.Exists(path))
        {
            string retained = Resolve(".studio/texture-backup", relative[9..]);
            if (File.Exists(retained)) path = retained;
        }
        if (!File.Exists(path) && studioRoot != null)
            {
            path = ResolveWithin(studioRoot, "samples/textures", relative[9..]);
            if (!File.Exists(path)) path = ResolveWithin(studioRoot, "samples/legacy-textures", relative[9..]);
        }
        if (!new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new ArgumentException("Only image resources can be read through the asset endpoint.");
        return path;
    }
    internal void PublishPalette(string asset, byte[] png, string catalog, DiskFingerprint? expected,
        IReadOnlyDictionary<string, byte[]>? additional = null)
    {
        string? staged = null;
        try
        {
            void CreateTexture(string name, byte[] bytes)
            {
                if (!name.StartsWith("Textures/palettes/", StringComparison.Ordinal)) throw new ArgumentException("Expected a palette texture.");
                string path = Resolve(texturesRelative, name[9..]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                path = Resolve(texturesRelative, name[9..]);
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(bytes); stream.Flush(true); }
            }
            if (additional != null) foreach (var pair in additional) CreateTexture(pair.Key, pair.Value);
            CreateTexture(asset, png);
            string destination = CatalogWritePath; Directory.CreateDirectory(Path.GetDirectoryName(destination)!); destination = CatalogWritePath;
            staged = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(new UTF8Encoding(false, true).GetBytes(catalog)); stream.Flush(true); }
            beforeMapPublish?.Invoke(destination);
            if (expected.HasValue) PublishIfUnchanged(staged, destination, expected.Value, FingerprintUtf8(catalog));
            else File.Move(staged, destination);
        }
        finally { if (staged != null) DeleteBestEffort(staged); }
    }

    internal void PublishDerivedTexture(string asset, byte[] png, string expectedHash)
    {
        if (!asset.StartsWith("Textures/palettes/", StringComparison.Ordinal) || !Path.GetFileName(asset).StartsWith("atlas-", StringComparison.Ordinal)
            || !asset.EndsWith(".png", StringComparison.Ordinal)) throw new ArgumentException("Expected a generated palette atlas.");
        string destination = Resolve(texturesRelative, asset[9..]);
        DiskFingerprint? current = Fingerprint(destination);
        DiskFingerprint desired = new(png.LongLength, 0, Convert.ToHexString(SHA256.HashData(png)));
        if (current?.Hash == desired.Hash) return;
        if (current?.Hash != expectedHash) throw new IOException("An externally edited atlas was preserved.");
        string staged = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(png); stream.Flush(true); }
            PublishIfUnchanged(staged, destination, current.Value, desired);
        }
        finally { DeleteBestEffort(staged); }
    }


    public string Relative(string absolute) => Path.GetRelativePath(MapsPath, absolute).Replace('\\', '/');
    public string[] List() => Directory.EnumerateFiles(MapsPath, "*.json", new EnumerationOptions
        { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive })
        .Select(Relative).Where(path => !path.StartsWith(".Recovery/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("AutoExport/", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    internal string AutoExportPath(string relative) => Resolve(mapsRelative + "/AutoExport", relative);

    /// <summary>Delete only the exact generated bytes captured at the rename boundary.</summary>
    internal void DeleteAutoExport(string path, DiskFingerprint expected)
    {
        string relative = Relative(path);
        if (!relative.StartsWith("AutoExport/", StringComparison.Ordinal)) throw new ArgumentException("Expected an automatic room export.");
        string destination = AutoExportPath(relative["AutoExport/".Length..]);
        string displaced = destination + "." + Guid.NewGuid().ToString("N") + ".delete.tmp";
        try { File.Move(destination, displaced); }
        catch (FileNotFoundException) { return; }
        if (!SameContent(Fingerprint(displaced), expected))
        {
            try { File.Move(displaced, destination); }
            catch (IOException) when (File.Exists(destination)) { }
            throw new IOException("An old auto-export changed during cleanup; its bytes were preserved: " + relative);
        }
        File.Delete(displaced);
    }

    /// <summary>Reads only the bounded file-system marker used by the polling fast path.</summary>
    public static DiskMetadata? Metadata(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return info.Exists ? new DiskMetadata(info.Length, info.LastWriteTimeUtc.Ticks) : null;
    }
    /// <summary>
    /// Returns a content fingerprint while reusing the prior hash when size and
    /// last-write time are unchanged. A metadata-only touch is hashed once and
    /// then recognized as the same content by <see cref="SameContent"/>.
    /// </summary>
    public static DiskFingerprint? Fingerprint(string path, DiskFingerprint? known = null)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists) return null;
        long length = info.Length, ticks = info.LastWriteTimeUtc.Ticks;
        if (known.HasValue && known.Value.Length == length && known.Value.LastWriteUtcTicks == ticks)
            return known;
        // An oversized file can never become a valid map. The distinct marker
        // makes save conflict checks fail safely without hashing arbitrary bytes.
        if (length > MapDocumentStore.MaximumFileBytes)
            return new DiskFingerprint(length, ticks, "OVERSIZED:" + length.ToString("X16"));
        try { return FingerprintAtState(path, length, ticks, known?.Hash); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>
    /// Reads, fingerprints, and when needed parses one stable map snapshot for a
    /// polling request. All work is intended to run outside the workspace gate.
    /// Known hashes avoid repeatedly materializing an unchanged or already
    /// rejected document during periodic same-metadata verification.
    /// </summary>
    internal MapProbe ProbeMap(string path, DiskMetadata expected, bool parseChangedContent,
        string? knownHash, string? secondKnownHash)
    {
        beforeMapProbe?.Invoke(path);
        if (expected.Length > MapDocumentStore.MaximumFileBytes)
        {
            var oversized = new DiskFingerprint(expected.Length, expected.LastWriteUtcTicks,
                "OVERSIZED:" + expected.Length.ToString("X16"));
            bool known = string.Equals(oversized.Hash, knownHash, StringComparison.Ordinal)
                || string.Equals(oversized.Hash, secondKnownHash, StringComparison.Ordinal);
            return new MapProbe(oversized, null, known || !parseChangedContent
                ? null : new InvalidDataException("Map file exceeds the 32 MiB limit."));
        }

        IOException? instability = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                byte[] bytes = ReadMapBytesOnce(path, expected.Length, expected.LastWriteUtcTicks);
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                string confirmation = ReadMapHashOnce(path, expected.Length, expected.LastWriteUtcTicks);
                if (!string.Equals(hash, confirmation, StringComparison.Ordinal))
                    throw new IOException("The map changed while its background snapshot was being read.");

                var fingerprint = new DiskFingerprint(expected.Length, expected.LastWriteUtcTicks, hash);
                if (!parseChangedContent || string.Equals(hash, knownHash, StringComparison.Ordinal)
                    || string.Equals(hash, secondKnownHash, StringComparison.Ordinal))
                    return new MapProbe(fingerprint, null, null);

                try
                {
                    string json = DecodeMapUtf8(bytes);
                    return new MapProbe(fingerprint, MapDocumentStore.Deserialize(json), null);
                }
                catch (DecoderFallbackException error)
                { return new MapProbe(fingerprint, null, new InvalidDataException("Map file is not valid UTF-8.", error)); }
                catch (InvalidDataException error)
                { return new MapProbe(fingerprint, null, error); }
            }
            catch (FileNotFoundException) { throw new IOException("The map file no longer exists."); }
            catch (DirectoryNotFoundException) { throw new IOException("The map folder no longer exists."); }
            catch (IOException error) { instability = error; }
        }
        throw instability ?? new IOException("The map could not be read consistently in the background.");
    }

    /// <summary>
    /// Loads and fingerprints the same source bytes, then verifies that the path
    /// still names those bytes. This prevents an open/reload race from publishing
    /// one document with another file version as its overwrite baseline.
    /// </summary>
    public static StableMap LoadStable(string path)
    {
        IOException? instability = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var info = new FileInfo(path);
                info.Refresh();
                if (!info.Exists) throw new IOException("The map file no longer exists.");
                long length = info.Length, ticks = info.LastWriteTimeUtc.Ticks;
                byte[] bytes = ReadMapBytesOnce(path, length, ticks);
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                string confirmation = ReadMapHashOnce(path, length, ticks);
                if (!string.Equals(hash, confirmation, StringComparison.Ordinal))
                    throw new IOException("The map changed while it was being opened. Try again.");

                string json;
                try
                {
                    json = DecodeMapUtf8(bytes);
                }
                catch (DecoderFallbackException error)
                { throw new InvalidDataException("Map file is not valid UTF-8.", error); }
                MapDocument document = MapDocumentStore.Deserialize(json);
                return new StableMap(document, new DiskFingerprint(length, ticks, hash));
            }
            catch (InvalidDataException) { throw; }
            catch (FileNotFoundException) { instability = new IOException("The map file no longer exists."); }
            catch (DirectoryNotFoundException) { instability = new IOException("The map folder no longer exists."); }
            catch (IOException error) { instability = error; }
        }
        throw instability ?? new IOException("The map could not be opened consistently.");
    }

    private static string DecodeMapUtf8(byte[] bytes)
    {
        // BOM auto-detection would silently reinterpret UTF-16/32 map files.
        // Decode with the throwing UTF-8 codec and consume only a UTF-8 BOM.
        using var memory = new MemoryStream(bytes, false);
        using var reader = new StreamReader(memory, StrictUtf8, detectEncodingFromByteOrderMarks: false);
        if (reader.Peek() == '\uFEFF') reader.Read();
        return reader.ReadToEnd();
    }

    private static DiskFingerprint FingerprintAtState(string path, long length, long ticks, string? expectedHash)
    {
        string first = ReadMapHashOnce(path, length, ticks);
        if (expectedHash != null && string.Equals(first, expectedHash, StringComparison.Ordinal))
            return new DiskFingerprint(length, ticks, first);
        string second = ReadMapHashOnce(path, length, ticks);
        if (!string.Equals(first, second, StringComparison.Ordinal))
            throw new IOException("The map changed while its disk marker was being read.");
        return new DiskFingerprint(length, ticks, first);
    }

    private static string ReadMapHashOnce(string path, long expectedLength, long expectedTicks)
    {
        ValidateMapFileState(path, expectedLength, expectedTicks);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            CooperativeReadShare, 65536, FileOptions.SequentialScan);
        if (stream.Length != expectedLength)
            throw new IOException("The map changed while its disk marker was being read.");
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Dispose();
        ValidateMapFileState(path, expectedLength, expectedTicks);
        return hash;
    }

    private static byte[] ReadMapBytesOnce(string path, long expectedLength, long expectedTicks)
    {
        ValidateMapFileState(path, expectedLength, expectedTicks);
        if (expectedLength > MapDocumentStore.MaximumFileBytes)
            throw new InvalidDataException("Map file exceeds the 32 MiB limit.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            CooperativeReadShare, 65536, FileOptions.SequentialScan);
        if (stream.Length != expectedLength)
            throw new IOException("The map changed while it was being opened.");
        byte[] bytes = new byte[checked((int)expectedLength)];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new IOException("The map changed while it was being opened.");
            offset += read;
        }
        if (stream.ReadByte() >= 0) throw new IOException("The map changed while it was being opened.");
        stream.Dispose();
        ValidateMapFileState(path, expectedLength, expectedTicks);
        return bytes;
    }

    private static void ValidateMapFileState(string path, long expectedLength, long expectedTicks)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists || info.Length != expectedLength || info.LastWriteTimeUtc.Ticks != expectedTicks)
            throw new IOException("The map changed while it was being read.");
    }

    /// <summary>Fingerprints the exact no-BOM UTF-8 text handed to the atomic writer.</summary>
    public static DiskFingerprint FingerprintUtf8(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        byte[] bytes = StrictUtf8.GetBytes(text);
        return new DiskFingerprint(bytes.LongLength, 0, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static string HashDocumentJson(string documentJson) => FingerprintUtf8(documentJson).Hash;

    /// <summary>
    /// Publishes a prepared, validated map snapshot. A same-path save is an
    /// optimistic compare-and-swap: File.Replace captures the exact bytes at the
    /// replacement boundary in a backup, which is verified and restored on a
    /// conflict. A destination absent from the caller's baseline uses one
    /// create-new move, so a concurrent create can never be overwritten.
    /// </summary>
    public void PublishMap(string path, string persistedJson, DiskFingerprint? expected)
    {
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Choose a map file path.", nameof(path));
        Directory.CreateDirectory(directory);
        string staged = Path.Combine(directory, "." + Path.GetFileName(destination) + "."
            + Guid.NewGuid().ToString("N") + ".save.tmp");
        try
        {
            MapDocumentStore.SaveValidatedSnapshot(staged, persistedJson);
            beforeMapPublish?.Invoke(destination);
            if (expected.HasValue)
                PublishIfUnchanged(staged, destination, expected.Value, FingerprintUtf8(persistedJson));
            else
            {
                try { File.Move(staged, destination); }
                catch (IOException error) when (File.Exists(destination))
                { throw new MapWriteConflictException("This map was created outside the editor before Save As completed.", error); }
            }
        }
        finally { DeleteBestEffort(staged); }
    }

    private void PublishIfUnchanged(string staged, string destination, DiskFingerprint expected,
        DiskFingerprint replacement)
    {
        string backup = Path.Combine(Path.GetDirectoryName(destination)!, "." + Path.GetFileName(destination) + "."
            + Guid.NewGuid().ToString("N") + ".preserved.tmp");
        try { ReplaceFile(staged, destination, backup); }
        catch (FileNotFoundException error)
        { throw new MapWriteConflictException("The map disappeared before the save could be published.", error); }
        catch (IOException error) when (!File.Exists(destination))
        { throw new MapWriteConflictException("The map changed before the save could be published.", error); }

        DiskFingerprint? displaced = Fingerprint(backup);
        if (SameContent(displaced, expected))
        {
            DeleteBestEffort(backup);
            return;
        }

        // The atomic replacement captured an unexpected external version.
        // Restore it only while the destination still contains our exact
        // prepared bytes. If a third writer has already changed the path,
        // leave that path and the captured version untouched.
        beforeConflictDestinationCheck?.Invoke(destination);
        DiskFingerprint? current = Fingerprint(destination);
        if (SameContent(current, replacement))
        {
            beforeConditionalRollback?.Invoke(destination);
            string rejected = Path.Combine(Path.GetDirectoryName(destination)!, "." + Path.GetFileName(destination) + "."
                + Guid.NewGuid().ToString("N") + ".rejected.tmp");
            try
            {
                ReplaceFile(backup, destination, rejected);
                // A third writer may have changed destination after the hash
                // above. File.Replace captures that exact boundary version in
                // rejected; delete it only when it is demonstrably our staged
                // snapshot, otherwise retain the unique external bytes.
                if (SameContent(Fingerprint(rejected), replacement)) DeleteBestEffort(rejected);
            }
            catch
            {
                // backup retains the external bytes if restoration itself is
                // interrupted or denied; never delete it in this path.
                throw;
            }
        }
        else if (!current.HasValue)
        {
            try { File.Move(backup, destination); }
            catch (IOException) when (File.Exists(destination)) { }
        }
        // A surviving backup belongs to a conflict or failed rollback path and
        // may be the only copy of externally authored bytes. Never decide to
        // delete it from two non-atomic fingerprint reads: the destination can
        // change between those reads. Success deletes/consumes its backup in the
        // branches above, so conflict artifacts are intentionally retained.
        throw new MapWriteConflictException("The map changed at the save boundary. Its external bytes were preserved.");
    }

    public void SaveRecovery(string documentJson, bool isDirty, string? savedPath, string? savedDocumentHash)
    {
        if (string.IsNullOrWhiteSpace(documentJson)) throw new ArgumentException("A recovery document is required.", nameof(documentJson));
        if ((savedPath == null) != (savedDocumentHash == null))
            throw new ArgumentException("Recovery target path and hash must be provided together.");
        if (savedPath != null)
        {
            string normalized = Relative(Map(savedPath));
            if (!string.Equals(normalized, savedPath.Replace('\\', '/'), PathComparison))
                throw new ArgumentException("Recovery target path is not canonical.", nameof(savedPath));
            ValidateSha256(savedDocumentHash!);
        }
        else if (!isDirty) throw new ArgumentException("A clean recovery marker needs a saved target.");

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("recoveryFormatVersion", RecoveryEnvelopeVersion);
            writer.WriteBoolean("dirty", isDirty);
            if (savedPath == null)
            {
                writer.WriteNull("savedPath");
                writer.WriteNull("savedDocumentHash");
            }
            else
            {
                writer.WriteString("savedPath", savedPath.Replace('\\', '/'));
                writer.WriteString("savedDocumentHash", savedDocumentHash);
            }
            writer.WritePropertyName("document");
            writer.WriteRawValue(documentJson, skipInputValidation: true);
            writer.WriteEndObject();
        }
        if (buffer.Length > MaximumRecoveryEnvelopeBytes)
            throw new InvalidDataException("Recovery snapshot exceeds the supported map size.");
        WriteAtomicBytes(RecoveryPath, buffer.ToArray());
    }

    public RecoveryMap LoadRecovery()
    {
        byte[] bytes = ReadBounded(RecoveryPath, MaximumRecoveryEnvelopeBytes, "Recovery snapshot");
        using JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 40 });
        JsonElement root = json.RootElement;
        if (!root.TryGetProperty("recoveryFormatVersion", out JsonElement version))
        {
            string legacyJson = StrictUtf8.GetString(bytes);
            MapDocument legacy = MapDocumentStore.Deserialize(legacyJson);
            return new RecoveryMap(legacy, MapDocumentStore.Serialize(legacy), true, null, null, true);
        }
        if (version.ValueKind != JsonValueKind.Number || version.GetInt32() != RecoveryEnvelopeVersion)
            throw new InvalidDataException("Unsupported recovery snapshot version.");
        if (!root.TryGetProperty("dirty", out JsonElement dirtyValue)
            || dirtyValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Recovery snapshot is missing its dirty state.");
        if (!root.TryGetProperty("document", out JsonElement documentValue) || documentValue.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Recovery snapshot is missing its map document.");
        string documentJson = documentValue.GetRawText();
        MapDocument document = MapDocumentStore.Deserialize(documentJson);
        string? savedPath = ReadOptionalString(root, "savedPath");
        string? savedHash = ReadOptionalString(root, "savedDocumentHash");
        if ((savedPath == null) != (savedHash == null))
            throw new InvalidDataException("Recovery target path and hash are incomplete.");
        if (savedPath != null)
        {
            savedPath = Relative(Map(savedPath));
            ValidateSha256(savedHash!);
        }
        bool isDirty = dirtyValue.GetBoolean();
        if (!isDirty && savedPath == null)
            throw new InvalidDataException("A clean recovery marker has no saved target.");
        return new RecoveryMap(document, documentJson, isDirty, savedPath, savedHash, false);
    }

    private static string? ReadOptionalString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Recovery field '" + property + "' must be text or null.");
        return value.GetString();
    }

    private static void ValidateSha256(string hash)
    {
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Recovery target hash is invalid.");
    }

    private static byte[] ReadBounded(string path, long maximumBytes, string label)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (stream.Length > maximumBytes) throw new InvalidDataException(label + " exceeds its size limit.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new IOException(label + " changed while it was being read.");
            offset += read;
        }
        if (stream.ReadByte() >= 0) throw new IOException(label + " changed while it was being read.");
        try { StrictUtf8.GetCharCount(bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException(label + " is not valid UTF-8.", error); }
        return bytes;
    }

    private static void WriteAtomicBytes(string path, byte[] bytes)
    {
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "."
            + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(destination)) ReplaceFile(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally { DeleteBestEffort(temporary); }
    }

    private static void DeleteBestEffort(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void ReplaceFile(string replacement, string destination, string? backup)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.Replace(replacement, destination, backup); return; }
            catch (IOException error) when (attempt < 2
                && (error.HResult & 0xffff) == ErrorUnableToRemoveReplaced
                && File.Exists(replacement) && File.Exists(destination)
                && (backup == null || !File.Exists(backup)))
            {
                // ERROR_UNABLE_TO_REMOVE_REPLACED guarantees that replacement
                // and destination kept their original names. Only that known
                // atomic pre-publication state is retried.
                Thread.Sleep(attempt == 0 ? 5 : 15);
            }
        }
    }

    public static bool SameContent(DiskFingerprint? left, DiskFingerprint? right) =>
        left.HasValue == right.HasValue && (!left.HasValue
            || string.Equals(left.Value.Hash, right!.Value.Hash, StringComparison.Ordinal));

    public static bool SameMetadata(DiskFingerprint? left, DiskFingerprint? right) =>
        left.HasValue == right.HasValue && (!left.HasValue
            || left.Value.Length == right!.Value.Length
            && left.Value.LastWriteUtcTicks == right.Value.LastWriteUtcTicks);

    public static bool SameMetadata(DiskFingerprint fingerprint, DiskMetadata metadata) =>
        fingerprint.Length == metadata.Length && fingerprint.LastWriteUtcTicks == metadata.LastWriteUtcTicks;

    public static bool SameMetadata(DiskMetadata left, DiskMetadata right) =>
        left.Length == right.Length && left.LastWriteUtcTicks == right.LastWriteUtcTicks;

    private string Resolve(string basePath, string relative) => ResolveWithin(ProjectPath, basePath, relative);

    private static string ResolveWithin(string ownerRoot, string basePath, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            throw new ArgumentException("Use a relative path inside the selected workspace.");
        relative = NormalizeSeparators(relative);
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new ArgumentException("Use a relative path inside the selected workspace.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(ownerRoot, NormalizeSeparators(basePath))));
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison))
            throw new ArgumentException("The requested path leaves its permitted workspace folder.");
        // Do not follow junctions or symlinks through otherwise valid lexical paths.
        for (var part = path; part != null && part.Length >= ownerRoot.Length; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Linked files and folders are not supported for map storage.");
        return path;
    }

    private void RejectGeneratedPath(string path)
    {
        foreach (string name in new[] { ".Recovery", "AutoExport" })
        {
            string reserved = Path.Combine(MapsPath, name);
            if (string.Equals(path, reserved, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(reserved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The " + name + " folder is reserved for automatic editor output.");
        }
    }

}

public sealed class MapWriteConflictException : IOException
{
    public MapWriteConflictException(string message) : base(message) { }
    public MapWriteConflictException(string message, Exception innerException) : base(message, innerException) { }
}
