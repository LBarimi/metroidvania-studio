using System.Text.Json;

namespace MetroidvaniaStudio.Automation;

public static partial class AutomationEngine
{
    public const int MaximumQueryCells = 4096;

    /// <summary>Returns occupied cells inside one bounded room-local rectangle without modifying the document.</summary>
    public static JsonElement QueryTiles(string documentJson, string roomId, string layer, int x, int y, int width, int height, CancellationToken cancellationToken = default)
        => CreateTileQuery(documentJson, cancellationToken).Query(roomId, layer, x, y, width, height, cancellationToken);

    /// <summary>Creates a private snapshot with reusable indexes for repeated bounded queries.</summary>
    public static AutomationTileQuery CreateTileQuery(string documentJson, CancellationToken cancellationToken = default)
        => new(Read(documentJson, cancellationToken));
}

public sealed class AutomationTileQuery
{
    private readonly Dictionary<string, MapRoom> rooms;
    private readonly Dictionary<(string Room, string Layer), Dictionary<int, MapCell>> indexes = new();
    private readonly object gate = new();
    internal AutomationTileQuery(MapDocument document) => rooms = document.rooms.ToDictionary(room => room.id, StringComparer.Ordinal);

    public JsonElement Query(string roomId, string layer, int x, int y, int width, int height, CancellationToken cancellationToken = default)
    {
        if (width <= 0 || height <= 0 || (long)width * height > AutomationEngine.MaximumQueryCells || x < 0 || y < 0)
            throw new ArgumentException("A tile query must cover between 1 and 4096 cells at nonnegative room-local coordinates.");
        if (layer is not ("foreground" or "background")) throw new ArgumentException("Tile query layer must be foreground or background.");
        if (roomId == null || !rooms.TryGetValue(roomId, out var room)) throw new ArgumentException("The requested room ID does not exist.");
        if ((long)x + width > room.width || (long)y + height > room.height) throw new ArgumentException("The query rectangle must be inside the room.");
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<int, MapCell> index;
        lock (gate)
        {
            if (!indexes.TryGetValue((roomId, layer), out index!))
            {
                index = new Dictionary<int, MapCell>();
                var source = layer == "foreground" ? room.foreground : room.background;
                int visited = 0;
                foreach (MapCell cell in source)
                {
                    if ((visited++ & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    index.Add(cell.x * MapDocument.MaximumRoomDimension + cell.y, cell);
                }
                indexes.Add((roomId, layer), index);
            }
        }
        var cells = new List<MapCell>();
        for (int row = y; row < y + height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int column = x; column < x + width; column++)
                if (index.TryGetValue(column * MapDocument.MaximumRoomDimension + row, out var cell)) cells.Add(cell);
        }
        return JsonSerializer.SerializeToElement(new
        {
            roomId, layer, x, y, width, height,
            cells = cells.Select(cell => new { cell.x, cell.y, shape = cell.shape switch
            {
                TileShape.Solid => "solid", TileShape.BottomLeft => "bottomLeft", TileShape.BottomRight => "bottomRight",
                TileShape.TopLeft => "topLeft", _ => "topRight"
            }, materialId = cell.material, cell.groupId })
        });
    }
}
