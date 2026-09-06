using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using MetroidvaniaStudio;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio.Server;

public sealed class Catalog(ProjectFiles files)
{
    public const long MaximumCatalogBytes = 32L * 1024 * 1024;
    public const int MaximumInitialObjectNodes = 4096;
    public const int MaximumMaterialDefinitions = 4096;
    public const int MaximumObjectDefinitions = 4096;
    public const int MaximumSpriteEntriesPerMaterial = 1280; // 256 masks x 5 supported shapes.
    public const int MaximumFieldsPerObjectDefinition = 256;
    public const int MaximumChoicesPerField = 1024;
    public const int MaximumCatalogWork = 65_536;
    private const string BuiltInJson = "{\"materials\":[],\"objects\":[],\"camera\":{\"ppu\":16,\"referenceWidth\":320,\"referenceHeight\":180,\"orthographicSize\":5.625,\"x\":0,\"y\":0}}";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static readonly JsonSerializerOptions Json = new() { IncludeFields = true, PropertyNameCaseInsensitive = true };
    public JsonElement Data { get; private set; } = ParseClone(BuiltInJson);
    public readonly Dictionary<string, TerrainTileSet> Materials = new(StringComparer.Ordinal);
    public readonly Dictionary<string, MapObjectDefinition> Objects = new(StringComparer.OrdinalIgnoreCase);
    private DateTime modified;
    private long modifiedLength = -1;
    private DateTime rejectedModified;
    private long rejectedLength = -1;
    private string? modifiedFingerprint;
    private string? rejectedFingerprint;
    private Task<ContentProbe>? contentProbe;
    private long nextContentProbeAt;
    private bool loadedFromFile;

    // File timestamps and lengths are only a fast path: tools can replace a
    // catalog while deliberately preserving both. A full content check runs on
    // a worker so the one-second workspace Tick never reads up to 32 MiB while
    // holding EditorWorkspace.Gate.
    private const int ContentProbeIntervalMilliseconds = 5000;
    private readonly record struct ContentProbe(DateTime Modified, long Length, string? Fingerprint, Exception? Error);
    private readonly record struct StableContent(byte[] Bytes, string Fingerprint);

    public bool Refresh()
    {
        if (!File.Exists(files.CatalogPath))
        {
            if (!loadedFromFile && rejectedLength < 0) return false;
            Data = ParseClone(BuiltInJson); Materials.Clear(); Objects.Clear();
            modified = rejectedModified = default; modifiedLength = rejectedLength = -1; loadedFromFile = false;
            modifiedFingerprint = rejectedFingerprint = null;
            contentProbe = null; nextContentProbeAt = 0;
            return true;
        }
        var info = new FileInfo(files.CatalogPath);
        DateTime stamp = info.LastWriteTimeUtc;
        long length = info.Length;
        bool matchesLoaded = stamp == modified && length == modifiedLength;
        bool matchesRejected = stamp == rejectedModified && length == rejectedLength;
        if (matchesLoaded || matchesRejected)
        {
            // Rejection wins when valid and invalid versions deliberately share
            // metadata; otherwise the same rejected bytes would be reparsed on
            // every probe because their hash differs from the older valid file.
            string? expectedFingerprint = matchesRejected ? rejectedFingerprint : modifiedFingerprint;
            if (contentProbe == null)
            {
                if (Environment.TickCount64 < nextContentProbeAt) return false;
                string path = files.CatalogPath;
                contentProbe = Task.Run(() => Probe(path, stamp, length, expectedFingerprint));
                return false;
            }
            if (!contentProbe.IsCompleted) return false;
            ContentProbe completed = contentProbe.GetAwaiter().GetResult();
            contentProbe = null;
            nextContentProbeAt = Environment.TickCount64 + ContentProbeIntervalMilliseconds;
            if (completed.Error != null) throw completed.Error;
            // A normal metadata change will be handled synchronously below.
            // A probe for an older same-metadata version is harmless: another
            // periodic probe will eventually observe the current bytes.
            if (completed.Modified != stamp || completed.Length != length
                || string.Equals(completed.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
                return false;
        }
        else
        {
            // Do not let a completed probe for the old metadata suppress an
            // ordinary timestamp/length change.
            contentProbe = null;
        }
        string? contentFingerprint = null;
        try
        {
            StableContent content = ReadBounded(files.CatalogPath, stamp, length);
            contentFingerprint = content.Fingerprint;
            JsonElement next = ParseClone(DecodeUtf8(content.Bytes));
            ValidateComplexity(next);
            ValidateCamera(next.GetProperty("camera"));
            var materials = new Dictionary<string, TerrainTileSet>(StringComparer.Ordinal);
            var objects = new Dictionary<string, MapObjectDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in RequiredArray(next, "materials", "Catalog").EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Every catalog material must be an object.");
                var set = new TerrainTileSet();
                string id = RequiredString(item, "id", "material");
                if (string.IsNullOrWhiteSpace(id) || materials.ContainsKey(id))
                    throw new InvalidDataException("Catalog material IDs must be nonempty and unique.");
                RequiredString(item, "name", "material");
                string colorText = RequiredString(item, "color", "material");
                if (!IsPortableHexColor(colorText) || !ColorText.TryParseHtmlString(colorText, out Color parsedColor))
                    throw new InvalidDataException("Catalog material color must be a #RGB, #RGBA, #RRGGBB, or #RRGGBBAA hex color.");
                JsonElement sprites = RequiredArray(item, "sprites", "Catalog material");
                foreach (JsonElement sprite in sprites.EnumerateArray()) ValidateSprite(sprite);
                string? themeId = null;
                if (item.TryGetProperty("themeId", out var theme))
                {
                    if (theme.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("Catalog material themeId must be a string.");
                    themeId = theme.GetString();
                }
                if (!string.IsNullOrWhiteSpace(themeId))
                {
                    set.SetTheme(themeId, parsedColor);
                }
                materials.Add(id, set);
            }
            foreach (var item in RequiredArray(next, "objects", "Catalog").EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Every catalog object must be an object.");
                var definition = new MapObjectDefinition();
                definition.id = RequiredString(item, "id", "object");
                definition.displayName = RequiredString(item, "name", "object");
                string objectColor = RequiredString(item, "color", "object");
                if (!IsPortableHexColor(objectColor) || !ColorText.TryParseHtmlString(objectColor, out _))
                    throw new InvalidDataException("Catalog object color must be a #RGB, #RGBA, #RRGGBB, or #RRGGBBAA hex color.");
                definition.layer = (MapLayer)item.GetProperty("layer").GetInt32();
                definition.defaultSize = new Vector2(FiniteNumber(item, "width"), FiniteNumber(item, "height"));
                definition.minimumSize = new Vector2(FiniteNumber(item, "minimumWidth"), FiniteNumber(item, "minimumHeight"));
                definition.placement = (MapPlacementKind)RequiredInteger(item, "placement");
                definition.minimumNodes = RequiredInteger(item, "minimumNodes");
                definition.maximumNodes = RequiredInteger(item, "maximumNodes");
                if (definition.minimumNodes < 0 || definition.minimumNodes > MaximumInitialObjectNodes
                    || definition.maximumNodes < -1 || definition.maximumNodes > MaximumInitialObjectNodes)
                    throw new InvalidDataException($"Catalog object node counts must be -1 or between 0 and {MaximumInitialObjectNodes}.");
                definition.resizable = RequiredBoolean(item, "resizable");
                definition.rotatable = RequiredBoolean(item, "rotatable");
                definition.flippable = RequiredBoolean(item, "flippable");
                JsonElement fields = RequiredArray(item, "properties", "Catalog object");
                definition.fields = JsonSerializer.Deserialize<List<MapFieldDefinition>>(fields, Json) ?? [];
                if (item.TryGetProperty("sprite", out JsonElement sprite) && sprite.ValueKind != JsonValueKind.Null) ValidateSprite(sprite);
                definition.ValidateDefinition();
                if (!objects.TryAdd(definition.id, definition))
                    throw new InvalidDataException("Catalog object IDs must be unique ignoring case.");
            }
            Materials.Clear(); foreach (var p in materials) Materials.Add(p.Key, p.Value);
            Objects.Clear(); foreach (var p in objects) Objects.Add(p.Key, p.Value);
            Data = next; modified = stamp; modifiedLength = length; modifiedFingerprint = contentFingerprint; loadedFromFile = true;
            rejectedModified = default; rejectedLength = -1; rejectedFingerprint = null;
            contentProbe = null; nextContentProbeAt = 0;
            return true;
        }
        catch (Exception error)
        {
            // Parse/schema failures are tied to these exact bytes, so an
            // equal-metadata rewrite can be retried as soon as its hash differs.
            // Transient I/O failures have no reliable byte identity and retry on
            // the next Tick instead of poisoning the metadata fast path.
            if (contentFingerprint != null)
            {
                rejectedModified = stamp; rejectedLength = length; rejectedFingerprint = contentFingerprint;
                contentProbe = null; nextContentProbeAt = 0;
            }
            if (error is InvalidDataException or IOException or UnauthorizedAccessException)
                throw;
            if (error is JsonException or InvalidOperationException or ArgumentException
                or KeyNotFoundException or FormatException or OverflowException)
                throw new InvalidDataException("EditorCatalog.json has an invalid shape: " + error.Message, error);
            throw;
        }
    }
    public static void ValidateCamera(JsonElement camera)
    {
        if (camera.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The camera profile must be an object.");
        PositiveInteger(camera, "ppu", 8192);
        PositiveInteger(camera, "referenceWidth", 65536);
        PositiveInteger(camera, "referenceHeight", 65536);
        PositiveNumber(camera, "orthographicSize", 1000000);
        OptionalFiniteNumber(camera, "x");
        OptionalFiniteNumber(camera, "y");
    }

    private static void ValidateComplexity(JsonElement catalog)
    {
        if (catalog.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("EditorCatalog.json must contain an object.");
        JsonElement materials = RequiredArray(catalog, "materials", "Catalog");
        JsonElement objects = RequiredArray(catalog, "objects", "Catalog");
        RequireCount(materials, MaximumMaterialDefinitions, "Catalog materials");
        RequireCount(objects, MaximumObjectDefinitions, "Catalog objects");
        int work = 0;
        AddWork(ref work, materials.GetArrayLength());
        AddWork(ref work, objects.GetArrayLength());
        foreach (JsonElement material in materials.EnumerateArray())
        {
            if (material.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Every catalog material must be an object.");
            JsonElement sprites = RequiredArray(material, "sprites", "Catalog material");
            RequireCount(sprites, MaximumSpriteEntriesPerMaterial, "Catalog material sprites");
            AddWork(ref work, sprites.GetArrayLength());
        }
        foreach (JsonElement definition in objects.EnumerateArray())
        {
            if (definition.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Every catalog object must be an object.");
            JsonElement fields = RequiredArray(definition, "properties", "Catalog object");
            RequireCount(fields, MaximumFieldsPerObjectDefinition, "Catalog object properties");
            AddWork(ref work, fields.GetArrayLength());
            foreach (JsonElement field in fields.EnumerateArray())
            {
                if (field.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Catalog object properties must contain objects.");
                if (!field.TryGetProperty("choices", out JsonElement choices)) continue;
                if (choices.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Catalog object property choices must be an array.");
                RequireCount(choices, MaximumChoicesPerField, "Catalog object property choices");
                AddWork(ref work, choices.GetArrayLength());
            }
        }
    }

    private static JsonElement RequiredArray(JsonElement owner, string key, string context)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(key, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(context + " " + key + " must be an array.");
        return value;
    }

    private static void RequireCount(JsonElement array, int maximum, string context)
    {
        if (array.GetArrayLength() > maximum)
            throw new InvalidDataException(context + " cannot contain more than " + maximum + " entries.");
    }

    private static void AddWork(ref int work, int amount)
    {
        if ((long)work + amount > MaximumCatalogWork)
            throw new InvalidDataException("EditorCatalog.json exceeds the " + MaximumCatalogWork + " entry processing budget.");
        work += amount;
    }
    private static JsonElement ParseClone(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.Clone();
    }
    private static StableContent ReadBounded(string path, DateTime expectedModified, long expectedLength)
    {
        IOException? instability = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                byte[] bytes = ReadBytesOnce(path, expectedModified, expectedLength);
                string first = Convert.ToHexString(SHA256.HashData(bytes));
                string second = ReadFingerprintOnce(path, expectedModified, expectedLength);
                if (string.Equals(first, second, StringComparison.Ordinal)) return new StableContent(bytes, first);
                instability = new IOException("EditorCatalog.json changed during its stable read.");
            }
            catch (IOException error) { instability = error; }
        }
        throw instability ?? new IOException("EditorCatalog.json could not be read consistently.");
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        try
        {
            string text = Utf8.GetString(bytes);
            return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
        }
        catch (DecoderFallbackException error) { throw new InvalidDataException("EditorCatalog.json is not valid UTF-8.", error); }
    }

    private static byte[] ReadBytesOnce(string path, DateTime expectedModified, long expectedLength)
    {
        ValidateFileState(path, expectedModified, expectedLength);
        if (expectedLength > MaximumCatalogBytes)
            throw new InvalidDataException("EditorCatalog.json exceeds the 32 MiB limit.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        if (stream.Length != expectedLength)
            throw new IOException("EditorCatalog.json changed while it was being read.");
        int length = checked((int)expectedLength), offset = 0;
        var bytes = new byte[length];
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new IOException("EditorCatalog.json changed while it was being read.");
            offset += read;
        }
        if (stream.ReadByte() != -1) throw new IOException("EditorCatalog.json changed while it was being read.");
        stream.Dispose();
        ValidateFileState(path, expectedModified, expectedLength);
        return bytes;
    }

    private static string ReadFingerprintOnce(string path, DateTime expectedModified, long expectedLength)
    {
        ValidateFileState(path, expectedModified, expectedLength);
        if (expectedLength > MaximumCatalogBytes)
            throw new InvalidDataException("EditorCatalog.json exceeds the 32 MiB limit.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        if (stream.Length != expectedLength)
            throw new IOException("EditorCatalog.json changed during its background content check.");
        using var hash = SHA256.Create();
        string fingerprint = Convert.ToHexString(hash.ComputeHash(stream));
        stream.Dispose();
        ValidateFileState(path, expectedModified, expectedLength);
        return fingerprint;
    }

    private static ContentProbe Probe(string path, DateTime expectedModified, long expectedLength, string? expectedFingerprint)
    {
        try
        {
            string fingerprint = ReadFingerprintOnce(path, expectedModified, expectedLength);
            if (!string.Equals(fingerprint, expectedFingerprint, StringComparison.Ordinal))
            {
                // A differing pass may have overlapped an in-place writer. Only
                // publish the change signal after two consecutive hashes agree;
                // atomic replace/delete remains unblocked by the shared handles.
                string confirmation = ReadFingerprintOnce(path, expectedModified, expectedLength);
                if (!string.Equals(fingerprint, confirmation, StringComparison.Ordinal))
                    throw new IOException("EditorCatalog.json changed during its background content check.");
            }
            return new ContentProbe(expectedModified, expectedLength, fingerprint, null);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new ContentProbe(expectedModified, expectedLength, null, error);
        }
    }

    private static void ValidateFileState(string path, DateTime expectedModified, long expectedLength)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists || info.LastWriteTimeUtc != expectedModified || info.Length != expectedLength)
            throw new IOException("EditorCatalog.json changed while it was being read.");
    }
    private static void PositiveInteger(JsonElement value, string key, int maximum)
    {
        if (!value.TryGetProperty(key, out JsonElement property) || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out int number) || number <= 0 || number > maximum)
            throw new InvalidDataException($"Camera {key} must be an integer between 1 and {maximum}.");
    }
    private static void PositiveNumber(JsonElement value, string key, double maximum)
    {
        if (!value.TryGetProperty(key, out JsonElement property) || property.ValueKind != JsonValueKind.Number
            || !property.TryGetDouble(out double number) || !double.IsFinite(number) || number <= 0 || number > maximum)
            throw new InvalidDataException($"Camera {key} must be a finite number greater than zero and at most {maximum}.");
    }
    private static void OptionalFiniteNumber(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out JsonElement property)) return;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out double number) || !double.IsFinite(number))
            throw new InvalidDataException($"Camera {key} must be a finite number.");
    }
    private static string RequiredString(JsonElement value, string key, string owner)
    {
        if (!value.TryGetProperty(key, out JsonElement property) || property.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Catalog {owner} {key} must be a string.");
        return property.GetString()!;
    }
    private static int RequiredInteger(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out JsonElement property) || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out int result))
            throw new InvalidDataException($"Catalog object {key} must be a 32-bit integer.");
        return result;
    }
    private static float FiniteNumber(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out JsonElement property) || property.ValueKind != JsonValueKind.Number
            || !property.TryGetSingle(out float result) || float.IsNaN(result) || float.IsInfinity(result))
            throw new InvalidDataException($"Catalog object {key} must be a finite number.");
        return result;
    }
    private static bool RequiredBoolean(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Catalog object {key} must be a boolean.");
        return property.GetBoolean();
    }
    private static bool IsPortableHexColor(string value)
    {
        if (value.Length is not (4 or 5 or 7 or 9) || value[0] != '#') return false;
        for (int index = 1; index < value.Length; index++)
            if (!Uri.IsHexDigit(value[index])) return false;
        return true;
    }
    private static void ValidateSprite(JsonElement sprite)
    {
        if (sprite.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Catalog sprites must be objects or null.");
        string asset = RequiredString(sprite, "asset", "sprite");
        int x = RequiredInteger(sprite, "x"), y = RequiredInteger(sprite, "y");
        int width = RequiredInteger(sprite, "width"), height = RequiredInteger(sprite, "height");
        // Exported color-only definitions intentionally use an all-zero sprite
        // placeholder. Any partially populated placeholder is malformed.
        bool placeholder = asset.Length == 0 && x == 0 && y == 0 && width == 0 && height == 0;
        if (!placeholder && (!asset.StartsWith("Textures/", StringComparison.Ordinal) || asset.Contains('\\')))
            throw new InvalidDataException("Catalog sprite assets must use an Textures/ resource path.");
        if (!placeholder && (x < 0 || y < 0 || width <= 0 || height <= 0 || x > 1000000 || y > 1000000 || width > 1000000 || height > 1000000))
            throw new InvalidDataException("Catalog sprite rectangles must be positive and bounded.");
        if (sprite.TryGetProperty("mask", out JsonElement mask)
            && (mask.ValueKind != JsonValueKind.Number || !mask.TryGetInt32(out int maskValue) || maskValue < 0 || maskValue > 255))
            throw new InvalidDataException("Catalog sprite mask must be between 0 and 255.");
        if (sprite.TryGetProperty("shape", out JsonElement shape)
            && (shape.ValueKind != JsonValueKind.Number || !shape.TryGetInt32(out int shapeValue) || shapeValue < 0 || shapeValue > 4))
            throw new InvalidDataException("Catalog sprite shape must be between 0 and 4.");
    }
}
