using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetroidvaniaStudio;

namespace MetroidvaniaStudio.Server;

public sealed record ProjectBundleSnapshot(string DocumentJson, JsonElement Catalog, long CatalogRevision);

public sealed partial class EditorWorkspace
{
    public ProjectBundleSnapshot CaptureProjectBundle(string instanceId, long documentRevision, long catalogRevision)
    {
        lock (Gate)
        {
            RequireAutomationReady();
            if (instanceId != InstanceId || documentRevision != DocumentRevision || catalogRevision != CatalogRevision)
                throw new WorkspaceConflict("@bundleChanged");
            // Both values are immutable. Asset reads and compression happen after releasing Gate.
            return new(documentJson, Catalog.Data, CatalogRevision);
        }
    }
}

public static class ProjectBundleExporter
{
    public const long MaximumBytes = 256L * 1024 * 1024;
    public const long MaximumImageBytes = 32L * 1024 * 1024;
    public const int MaximumImages = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly DateTimeOffset EntryTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private sealed record ImageCopy(string Asset, long Length, string Hash);

    // Call on a worker. Only the supplied snapshot and explicitly referenced images are exported.
    public static void Write(ProjectBundleSnapshot snapshot, ProjectFiles files, Stream output, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MapDocument document = MapDocumentStore.Deserialize(snapshot.DocumentJson);
        JsonElement catalog = snapshot.Catalog;
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var portableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? asset)
        {
            if (string.IsNullOrEmpty(asset)) return;
            ValidateAsset(asset);
            if (names.Contains(asset)) return;
            if (!portableNames.Add(asset.Normalize(NormalizationForm.FormC))) throw new InvalidOperationException("@bundlePathCollision");
            names.Add(asset);
            if (names.Count > MaximumImages) throw new InvalidOperationException("@bundleTooLarge");
        }
        void Sprite(JsonElement sprite)
        {
            if (sprite.ValueKind == JsonValueKind.Object && sprite.TryGetProperty("asset", out var asset)) Add(asset.GetString());
        }
        foreach (var material in catalog.GetProperty("materials").EnumerateArray())
        {
            foreach (var sprite in material.GetProperty("sprites").EnumerateArray()) Sprite(sprite);
            if (!material.TryGetProperty("editorTileset", out var settings) || settings.ValueKind != JsonValueKind.Object) continue;
            if (settings.TryGetProperty("source", out var source)) Add(source.GetString());
            if (settings.TryGetProperty("slots", out var slots)) foreach (var slot in slots.EnumerateArray()) Sprite(slot);
        }
        foreach (var item in catalog.GetProperty("objects").EnumerateArray())
            if (item.TryGetProperty("sprite", out var sprite)) Sprite(sprite);
        foreach (var style in document.stylegrounds) Add(style.texture);
        foreach (string asset in names)
        {
            for (int slash = asset.LastIndexOf('/'); slash > 0; slash = asset.LastIndexOf('/', slash - 1))
                if (portableNames.Contains(asset[..slash].Normalize(NormalizationForm.FormC))) throw new InvalidOperationException("@bundlePathCollision");
        }

        long total = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Utf8);
        void Entry(string name, byte[] bytes, CompressionLevel compression = CompressionLevel.Optimal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += bytes.LongLength;
            if (total > MaximumBytes) throw new InvalidOperationException("@bundleTooLarge");
            var entry = archive.CreateEntry(name, compression); entry.LastWriteTime = EntryTime; entry.ExternalAttributes = 0;
            using var target = entry.Open();
            for (int start = 0; start < bytes.Length; start += 65536)
            {
                cancellationToken.ThrowIfCancellationRequested();
                target.Write(bytes, start, Math.Min(65536, bytes.Length - start));
            }
        }
        Entry("Maps/world.map.json", Utf8.GetBytes(snapshot.DocumentJson));
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                // A catalog may carry a legacy machine path. Export only the portable contract.
                writer.WriteString("projectPath", ".");
                foreach (string key in new[] { "camera", "materials", "objects", "editorPaletteGroups" })
                    if (catalog.TryGetProperty(key, out var value)) { writer.WritePropertyName(key); value.WriteTo(writer); }
                writer.WriteEndObject();
            }
            Entry(".studio/catalog.json", buffer.ToArray());
        }
        var copies = new List<ImageCopy>();
        foreach (string asset in names)
        {
            byte[] bytes = ReadImage(files, asset, cancellationToken);
            if (asset.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                try { _ = PngRaster.Decode(bytes); }
                catch (Exception error) when (error is ArgumentException or InvalidDataException or OverflowException)
                { throw new InvalidOperationException("@bundleAssetUnavailable:" + asset); }
            }
            Entry(asset, bytes, CompressionLevel.NoCompression);
            copies.Add(new(asset, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        // Reject mid-export image changes instead of handing out a mixed resource set.
        foreach (var copy in copies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] current = ReadImage(files, copy.Asset, cancellationToken);
            if (current.LongLength != copy.Length || Convert.ToHexString(SHA256.HashData(current)) != copy.Hash)
                throw new WorkspaceConflict("@bundleImagesChanged");
        }
        Entry("readme.txt", Utf8.GetBytes("Metroidvania Studio project bundle\n\n"
            + "Maps/world.map.json: the complete map, including all rooms.\n"
            + ".studio/catalog.json: palettes, tile rules and object definitions.\n"
            + "Textures/: referenced images, including palette source PNGs.\n\n"
            + "Extract the ZIP into a new folder. Start the studio with --project pointing\n"
            + "to that folder to continue editing with these palettes and images.\n"
            + "For an engine import, select Maps/world.map.json, .studio/catalog.json\n"
            + "and this extracted folder as the resource root.\n"));
    }

    private static byte[] ReadImage(ProjectFiles files, string asset, CancellationToken token)
    {
        try
        {
            string path = files.Asset(asset);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length <= 0 || input.Length > MaximumImageBytes) throw new InvalidOperationException("@bundleTooLarge");
            var bytes = new byte[(int)input.Length];
            for (int offset = 0; offset < bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                int count = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                if (count == 0) throw new IOException();
                offset += count;
            }
            if (input.ReadByte() != -1) throw new WorkspaceConflict("@bundleImagesChanged");
            return bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new InvalidOperationException("@bundleAssetUnavailable:" + asset); }
    }

    private static void ValidateAsset(string asset)
    {
        if (!asset.StartsWith("Textures/", StringComparison.Ordinal) || asset.Length > 240) throw new InvalidOperationException("@bundleInvalidPath");
        foreach (string part in asset.Split('/'))
        {
            string stem = part.Split('.')[0].ToUpperInvariant();
            bool device = stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4
                && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9';
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || device
                || part.Any(c => char.IsControl(c) || "<>:\"\\|?*".Contains(c))) throw new InvalidOperationException("@bundleInvalidPath");
        }
    }
}
