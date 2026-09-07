using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using MetroidvaniaStudio;

namespace MetroidvaniaStudio.Server;

public sealed partial class Catalog
{
    public string AddPalette(string name, string color)
    {
        name = name.Trim(); color = color.Trim().ToUpperInvariant();
        if (name.Length is 0 or > 80 || name.Any(char.IsControl))
            throw new ArgumentException("A palette name must contain 1 to 80 characters without line breaks.");
        if (color.Length != 7 || color[0] != '#' || !color[1..].All(char.IsAsciiHexDigit))
            throw new ArgumentException("Choose a six-digit HEX color such as #9655CF.");
        if (Data.GetProperty("materials").EnumerateArray().Any(m => string.Equals(m.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A palette with that name already exists.");

        // Never overwrite a changed or invalid catalog, including a newly created workspace override.
        string source = files.CatalogPath, destination = files.CatalogWritePath;
        ProjectFiles.DiskFingerprint? expected = null;
        if (File.Exists(source))
        {
            var info = new FileInfo(source);
            StableContent current = ReadBounded(source, info.LastWriteTimeUtc, info.Length);
            if (!loadedFromFile || current.Fingerprint != modifiedFingerprint)
                throw new WorkspaceConflict("The palette catalog changed. Wait for it to reload before adding a palette.");
            if (string.Equals(source, destination, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                expected = new ProjectFiles.DiskFingerprint(info.Length, info.LastWriteTimeUtc.Ticks, current.Fingerprint);
        }
        else if (loadedFromFile) throw new WorkspaceConflict("The palette catalog was removed. Wait for it to reload before adding a palette.");

        string id = "palette-" + Guid.NewGuid().ToString("N"), asset = "Textures/palettes/" + id + ".png";
        PaletteAtlas.Result atlas = PaletteAtlas.Create(asset, color);
        var material = new MaterialData { id = id, name = name, color = color, themeId = id, sprites = atlas.Sprites };
        var nextNode = JsonNode.Parse(Data.GetRawText())!;
        nextNode["materials"]!.AsArray().Add(JsonSerializer.SerializeToNode(material, Json));
        string serialized = nextNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        byte[] bytes = Utf8.GetBytes(serialized);
        if (bytes.LongLength > MaximumCatalogBytes) throw new InvalidDataException("The palette catalog is full.");
        JsonElement next = ParseClone(serialized); ValidateComplexity(next);
        files.PublishPalette(asset, atlas.Png, serialized, expected);

        // Publish memory only after the texture and catalog are durable. Existing opaque fields remain intact.
        Data = next; Materials.Add(id, MapResourceFactory.CreateTerrainMaterial(id, name, id, color));
        var saved = new FileInfo(destination);
        modified = saved.LastWriteTimeUtc; modifiedLength = bytes.LongLength;
        modifiedFingerprint = Convert.ToHexString(SHA256.HashData(bytes)); loadedFromFile = true;
        rejectedModified = default; rejectedLength = -1; rejectedFingerprint = null;
        contentProbe = null; nextContentProbeAt = 0;
        return id;
    }
}
