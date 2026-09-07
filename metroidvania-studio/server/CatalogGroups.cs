using System.Text.Json;
using System.Text.Json.Nodes;

namespace MetroidvaniaStudio.Server;

public sealed record EditorPaletteGroup(string id, string name, string[] materials);
public sealed record PalettePlacement(string groupId, string? beforeId, EditorPaletteGroup[] expectedGroups);

public sealed partial class Catalog
{
    public const string DefaultPaletteGroup = "default";
    private const int MaximumPaletteGroups = 128;
    private JsonElement groupSource;
    private EditorPaletteGroup[]? groupCache;
    public EditorPaletteGroup[] PaletteGroups
    {
        get
        {
            if (groupCache == null || !groupSource.Equals(Data)) { groupCache = ReadPaletteGroups(Data); groupSource = Data; }
            return groupCache;
        }
    }
    private static string GroupName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 80 || name.Any(char.IsControl)) throw new ArgumentException("@paletteGroupInvalidName");
        return name;
    }
    private static EditorPaletteGroup[] ReadPaletteGroups(JsonElement catalog)
    {
        string[] ids = catalog.GetProperty("materials").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToArray();
        var remaining = new HashSet<string>(ids, StringComparer.Ordinal);
        var groups = new List<EditorPaletteGroup>();
        if (catalog.TryGetProperty("editorPaletteGroups", out var metadata))
        {
            if (metadata.ValueKind != JsonValueKind.Array || metadata.GetArrayLength() > MaximumPaletteGroups) throw new InvalidDataException("@paletteGroupInvalidData");
            var groupIds = new HashSet<string>(StringComparer.Ordinal); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assigned = new HashSet<string>(StringComparer.Ordinal); int count = 0;
            foreach (var item in metadata.EnumerateArray())
            {
                string id = item.GetProperty("id").GetString()!, name = GroupName(item.GetProperty("name").GetString()!);
                if (string.IsNullOrWhiteSpace(id) || id.Length > 80 || id.Any(char.IsControl) || !groupIds.Add(id) || !names.Add(name)) throw new InvalidDataException("@paletteGroupInvalidData");
                var entries = item.GetProperty("materials");
                if (entries.ValueKind != JsonValueKind.Array || (count += entries.GetArrayLength()) > MaximumMaterialDefinitions) throw new InvalidDataException("@paletteGroupInvalidData");
                var members = new List<string>();
                foreach (var entry in entries.EnumerateArray())
                {
                    string? value = entry.GetString();
                    if (string.IsNullOrEmpty(value) || value.Length > 1024 || !assigned.Add(value)) throw new InvalidDataException("@paletteGroupInvalidData");
                    if (remaining.Remove(value)) members.Add(value);
                }
                groups.Add(new(id, name, members.ToArray()));
            }
        }
        int defaultIndex = groups.FindIndex(g => g.id == DefaultPaletteGroup);
        if (defaultIndex < 0)
        {
            if (groups.Count >= MaximumPaletteGroups || groups.Any(g => g.name.Equals("Default", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("@paletteGroupInvalidData");
            groups.Insert(0, new(DefaultPaletteGroup, "Default", [])); defaultIndex = 0;
        }
        var fallback = groups[defaultIndex];
        groups[defaultIndex] = fallback with { materials = fallback.materials.Concat(ids.Where(remaining.Contains)).ToArray() };
        return groups.ToArray();
    }
    public string AddPaletteGroup(string name)
    {
        name = GroupName(name); var groups = PaletteGroups;
        if (groups.Length >= MaximumPaletteGroups) throw new ArgumentException("@paletteGroupLimit");
        if (groups.Any(g => g.name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("@paletteGroupDuplicateName");
        string id = "group-" + Guid.NewGuid().ToString("N");
        SavePaletteGroups([.. groups, new(id, name, [])]); return id;
    }
    public void MovePaletteGroup(string id, string? beforeId, string? name = null, EditorPaletteGroup[]? expectedGroups = null)
    {
        CheckGroupExpectation(expectedGroups);
        var groups = PaletteGroups.ToList(); int index = groups.FindIndex(g => g.id == id);
        if (index < 0) throw new ArgumentException("@paletteGroupMissing");
        var group = groups[index];
        if (name != null)
        {
            name = GroupName(name);
            if (id == DefaultPaletteGroup && name != group.name) throw new ArgumentException("@paletteGroupInvalidName");
            if (groups.Any(g => g.id != id && g.name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("@paletteGroupDuplicateName");
            group = group with { name = name };
        }
        if (beforeId == id) { groups[index] = group; SavePaletteGroups(groups.ToArray()); return; }
        groups.RemoveAt(index);
        int target = beforeId == null ? groups.Count : groups.FindIndex(g => g.id == beforeId);
        if (target < 0) throw new ArgumentException("@paletteGroupMissing");
        groups.Insert(target, group); SavePaletteGroups(groups.ToArray());
    }
    public void MovePalette(string id, string groupId, string? beforeId, EditorPaletteGroup[]? expectedGroups = null)
    {
        CheckGroupExpectation(expectedGroups);
        SavePaletteGroups(PlacePalette(PaletteGroups, id, groupId, beforeId));
    }
    private void CheckGroupExpectation(EditorPaletteGroup[]? expected)
    {
        if (expected != null && JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(PaletteGroups)) throw new WorkspaceConflict("@paletteGroupsChanged");
    }
    private static EditorPaletteGroup[] PlacePalette(EditorPaletteGroup[] groups, string id, string groupId, string? beforeId)
    {
        int target = Array.FindIndex(groups, g => g.id == groupId);
        if (target < 0 || !groups.Any(g => g.materials.Contains(id))) throw new ArgumentException("@paletteGroupMissing");
        if (beforeId == id)
        {
            if (!groups[target].materials.Contains(id)) throw new ArgumentException("@paletteGroupMissing");
            return groups;
        }
        var result = groups.Select(g => g with { materials = g.materials.Where(m => m != id).ToArray() }).ToArray();
        var members = result[target].materials.ToList();
        int index = beforeId == null ? members.Count : members.IndexOf(beforeId);
        if (index < 0) throw new ArgumentException("@paletteGroupMissing");
        members.Insert(index, id); result[target] = result[target] with { materials = members.ToArray() }; return result;
    }
    private void SavePaletteGroups(EditorPaletteGroup[] groups)
    {
        var expected = CatalogWriteExpectation();
        var node = JsonNode.Parse(Data.GetRawText())!; node["editorPaletteGroups"] = JsonSerializer.SerializeToNode(groups);
        string serialized = node.ToJsonString();
        if (Utf8.GetByteCount(serialized) > MaximumCatalogBytes) throw new InvalidDataException("@tilesetCatalogFull");
        ValidateComplexity(ParseClone(serialized));
        files.PublishTextureCatalog(serialized, expected); Refresh();
    }
    private ProjectFiles.DiskFingerprint? CatalogWriteExpectation()
    {
        if (!File.Exists(files.CatalogPath))
        {
            if (loadedFromFile) throw new WorkspaceConflict("@paletteGroupsChanged");
            return null;
        }
        var info = new FileInfo(files.CatalogPath); var content = ReadBounded(info.FullName, info.LastWriteTimeUtc, info.Length);
        if (!loadedFromFile || content.Fingerprint != modifiedFingerprint) throw new WorkspaceConflict("@paletteGroupsChanged");
        return string.Equals(files.CatalogPath, files.CatalogWritePath, ProjectFiles.PathComparison)
            ? new ProjectFiles.DiskFingerprint(info.Length, info.LastWriteTimeUtc.Ticks, content.Fingerprint) : null;
    }
}
