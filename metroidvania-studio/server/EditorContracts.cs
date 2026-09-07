using System.Text.Json;
using System.Text.Json.Serialization;
using MetroidvaniaStudio;

namespace MetroidvaniaStudio.Server;

// Raw JSON is retained on the hot path. This annotation gives the generator its
// actual serialized type without parsing/copying the document for every reply.
[AttributeUsage(AttributeTargets.Property)]
public sealed class WireTypeAttribute(Type type) : Attribute
{
    public Type Type { get; } = type;
}

public sealed record EditorRect(float x, float y, float width, float height);
public sealed record EditorNodeSelection(string id, int index);
public sealed record EditorSelection(string? roomId, int tool, int layer, int shape,
    string material, int brushSize, bool filled, string objectDefinition, string groupId,
    int[] hiddenLayers, int[] lockedLayers, string[] objects, EditorNodeSelection[] nodes, EditorRect? area, string[] roomIds);
public sealed record EditorConnection(string roomAId, string roomBId, bool vertical, double coordinate, double start, double end);
public sealed record EditorExportStatus(string phase, long version, string? error,
    string? roomId, string? path, string? hash);

public sealed record EditorWorkspaceInfo(string name, string mapsPath);

public sealed class EditorState
{
    public CameraProfile camera { get; init; } = new();
    public long revision { get; init; }
    public long documentRevision { get; init; }
    public long catalogRevision { get; init; }
    public string instanceId { get; init; } = "";
    public string? file { get; init; }
    public bool dirty { get; init; }
    public bool canUndo { get; init; }
    public bool canRedo { get; init; }
    public string? notice { get; init; }
    public required EditorWorkspaceInfo workspace { get; init; }
    public required EditorExportStatus export { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EditorSelection? selection { get; init; }
    [WireType(typeof(MapDocument)), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ValidatedJson? document { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EditorConnection[]? connections { get; init; }
    [WireType(typeof(CatalogData)), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? catalog { get; init; }
}
