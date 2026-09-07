using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio.Automation;

public sealed record AutomationResult(string DocumentJson, string[] CreatedIds, int OperationCount, bool Changed);

/// <summary>Engine-neutral, bounded map edits with no filesystem or network access.</summary>
public static partial class AutomationEngine
{
    public const int ApiVersion = 1;
    public const int MaximumOperations = 1024;
    public const int MaximumCellVisits = 1_048_576;
    public const long MaximumWorkUnits = 8_388_608;
    public const int MaximumRequestBytes = 8 * 1024 * 1024;
    public const int MaximumDocumentBytes = 32 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static AutomationResult Apply(string documentJson, JsonElement request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ValueKind != JsonValueKind.Object) throw new ArgumentException("The request must be an object.");
        Fields(request, "apiVersion", "operations");
        if (Integer(request, "apiVersion") != ApiVersion) throw new ArgumentException("Unsupported API version. Expected 1.");
        if (Utf8.GetByteCount(request.GetRawText()) > MaximumRequestBytes) throw new ArgumentException("The request exceeds 8 MiB.");
        JsonElement operations = Required(request, "operations");
        if (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() > MaximumOperations)
            throw new ArgumentException("operations must be an array of at most 1024 operations.");
        MapDocument document = Read(documentJson, cancellationToken);
        string before = MapDocumentStore.Serialize(document);
        var context = new Batch(document, cancellationToken);
        int index = 0;
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { context.Apply(operation); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException or InvalidDataException)
            { throw new ArgumentException($"Operation {index}: {error.Message}", nameof(request), error); }
            index++;
        }
        context.Flush();
        cancellationToken.ThrowIfCancellationRequested();
        string after;
        try { after = MapDocumentStore.Serialize(context.Document); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        { throw new InvalidDataException(error.Message, error); }
        cancellationToken.ThrowIfCancellationRequested();
        return new AutomationResult(after, context.CreatedIds.ToArray(), index, before != after);
    }

    public static JsonElement Inspect(string documentJson, CancellationToken cancellationToken = default)
    {
        MapDocument document = Read(documentJson, cancellationToken);
        var bounds = MiniMapGeometry.GetBounds(document);
        var connections = MiniMapGeometry.GetConnections(document, MiniMapGeometry.DefaultMaximumConnections, out bool truncated);
        cancellationToken.ThrowIfCancellationRequested();
        var camera = MapCameraSettings.Resolve(document);
        return JsonSerializer.SerializeToElement(new
        {
            apiVersion = ApiVersion, formatVersion = document.formatVersion, tileSize = document.tileSize, name = document.name,
            camera = new { ppu = camera.ppu, width = camera.referenceWidth, height = camera.referenceHeight },
            properties = document.properties.ToDictionary(item => item.key, item => item.value, StringComparer.Ordinal),
            rooms = document.rooms.Select(room => new
            {
                roomId = room.id, room.name, room.x, room.y, room.width, room.height, room.visible, room.locked,
                foregroundCount = room.foreground.Count, backgroundCount = room.background.Count, objectCount = room.objects.Count,
                properties = room.properties.ToDictionary(item => item.key, item => item.value, StringComparer.Ordinal),
                objects = room.objects.Select(item => new { objectId = item.id, definitionId = item.definition, layer = LayerName(item.layer), item.x, item.y, item.width, item.height })
            }),
            minimap = new
            {
                bounds = new { empty = bounds.IsEmpty, x = bounds.XMin, y = bounds.YMin, width = bounds.Width, height = bounds.Height },
                connections = connections.Select(connection => new
                {
                    roomAId = connection.RoomAId, roomBId = connection.RoomBId, vertical = connection.Vertical,
                    coordinate = connection.Coordinate, start = connection.Start, end = connection.End
                }),
                truncated
            }
        });
    }

    private static MapDocument Read(string json, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (json == null) throw new ArgumentNullException(nameof(json));
        if (json.Length > MaximumDocumentBytes || Utf8.GetByteCount(json) > MaximumDocumentBytes)
            throw new InvalidDataException("The document exceeds 32 MiB.");
        MapDocument document = MapDocumentStore.Deserialize(json);
        cancellationToken.ThrowIfCancellationRequested();
        return document;
    }

    private sealed class Batch
    {
        public MapDocument Document;
        public readonly List<string> CreatedIds = new();
        private readonly CancellationToken cancellation;
        private readonly Dictionary<(string Room, MapLayer Layer), Dictionary<int, MapCell>> tileIndexes = new();
        private readonly Dictionary<string, (MapLayer Layer, bool Locked)> groups = new(StringComparer.Ordinal);
        private readonly HashSet<string> ids = new(StringComparer.Ordinal);
        private long work;
        private int visits;

        public Batch(MapDocument document, CancellationToken cancellation)
        {
            Document = document;
            this.cancellation = cancellation;
            foreach (MapRoom room in document.rooms)
            {
                ids.Add(room.id);
                foreach (MapObject item in room.objects) ids.Add(item.id);
            }
            foreach (MapStyleground style in document.stylegrounds) ids.Add(style.id);
            var groupDefinitions = document.layerGroups.ToDictionary(group => group.id, StringComparer.Ordinal);
            foreach (MapLayerGroup group in document.layerGroups)
            {
                ids.Add(group.id);
                if (groups.ContainsKey(group.id)) continue;
                var pending = new Stack<MapLayerGroup>();
                string current = group.id;
                while (current.Length > 0 && !groups.ContainsKey(current))
                {
                    Charge(1);
                    MapLayerGroup ancestor = groupDefinitions[current];
                    pending.Push(ancestor); current = ancestor.parentId;
                }
                bool locked = current.Length > 0 && groups[current].Locked;
                while (pending.TryPop(out MapLayerGroup? ancestor))
                {
                    locked |= ancestor.locked;
                    groups.Add(ancestor.id, (ancestor.layer, locked));
                }
            }
        }

        public void Apply(JsonElement operation)
        {
            cancellation.ThrowIfCancellationRequested();
            string op = Text(operation, "op");
            switch (op)
            {
                case "room.add": AddRoom(operation); break;
                case "room.update": UpdateRoom(operation); break;
                case "room.move": MoveRoom(operation); break;
                case "room.resize": ResizeRoom(operation); break;
                case "room.rotate": RotateRoom(operation); break;
                case "room.duplicate": DuplicateRoom(operation); break;
                case "room.delete": DeleteRoom(operation); break;
                case "tiles.paint": case "tiles.erase": Paint(operation, op == "tiles.erase"); break;
                case "tiles.rectangle": Rectangle(operation); break;
                case "tiles.fill": Fill(operation); break;
                case "object.add": AddObject(operation); break;
                case "object.update": UpdateObject(operation); break;
                case "object.delete": DeleteObject(operation); break;
                case "properties.set": SetProperties(operation); break;
                case "document.update": Fields(operation, "op", "name"); Document.name = Text(operation, "name"); break;
                case "camera.set":
                    Fields(operation, "op", "ppu", "width", "height");
                    MapCameraSettings.Apply(Document, Integer(operation, "ppu"), Integer(operation, "width"), Integer(operation, "height"));
                    break;
                default: throw new ArgumentException("Unknown operation '" + op + "'.");
            }
        }

        public void Flush()
        {
            foreach (var pair in tileIndexes)
            {
                cancellation.ThrowIfCancellationRequested();
                MapRoom room = Room(pair.Key.Room, false);
                List<MapCell> cells = pair.Value.Values.ToList();
                if (pair.Key.Layer == MapLayer.ForegroundTiles) room.foreground = cells; else room.background = cells;
            }
            tileIndexes.Clear();
        }

        private void Charge(long units)
        {
            cancellation.ThrowIfCancellationRequested();
            if (units < 0 || (work += units) > MaximumWorkUnits) throw new ArgumentException("The batch exceeds its work limit. Split it into smaller batches.");
        }

        private void Visit()
        {
            Charge(1);
            if (++visits > MaximumCellVisits) throw new ArgumentException("The batch exceeds 1048576 visited cells.");
        }

        private void CoreRoom(string? roomId, Action<MapRoomEditing> edit, bool layout = false)
        {
            Flush();
            long snapshotWeight = Document.properties.Sum(property => (long)property.key.Length + property.value.Length) / 8;
            foreach (MapRoom room in Document.rooms)
            {
                cancellation.ThrowIfCancellationRequested();
                snapshotWeight += room.foreground.Count + room.background.Count + room.name.Length / 8 + 1;
                snapshotWeight += room.properties.Sum(property => (long)property.key.Length + property.value.Length) / 8;
                snapshotWeight += room.objects.Sum(item => 1L + item.nodes.Count + item.definition.Length / 8
                    + item.properties.Sum(property => (long)property.key.Length + property.value.Length) / 8);
            }
            snapshotWeight += Document.stylegrounds.Sum(style => 1L + style.name.Length / 8 + style.texture.Length / 8
                + style.properties.Sum(property => (long)property.key.Length + property.value.Length) / 8);
            Charge(snapshotWeight * 4 + 1);
            if (layout) Charge((long)Document.rooms.Count * Document.rooms.Count * 4);
            var session = new MapEditSession(Document, historyByteLimit: 1);
            using var rooms = new MapRoomEditing(session);
            if (roomId != null) rooms.Select(roomId);
            edit(rooms);
            Document = session.Document;
            cancellation.ThrowIfCancellationRequested();
        }

        private MapRoom Room(JsonElement operation) => Room(Text(operation, "roomId"), true);
        private MapRoom Room(string id, bool editable)
        {
            Charge(Document.rooms.Count);
            MapRoom room = Document.rooms.Find(item => item.id == id) ?? throw new ArgumentException("Unknown room ID '" + id + "'.");
            if (editable && room.locked) throw new ArgumentException("The room is locked.");
            return room;
        }

        private string NewId(JsonElement operation, string key = "id")
        {
            string id = OptionalText(operation, key, Guid.NewGuid().ToString("N"));
            if (!ids.Add(id)) throw new ArgumentException("The ID is already in use: '" + id + "'.");
            CreatedIds.Add(id);
            return id;
        }

        private void RequireGroup(string id, MapLayer layer)
        {
            if (id.Length == 0) return;
            if (!groups.TryGetValue(id, out var group) || group.Layer != layer) throw new ArgumentException("The group must exist in the requested layer.");
            if (group.Locked) throw new ArgumentException("The layer group is locked.");
        }

        private void RequireContentsUnlocked(MapRoom room)
        {
            Flush();
            foreach (MapCell cell in room.foreground) RequireGroup(cell.groupId, MapLayer.ForegroundTiles);
            foreach (MapCell cell in room.background) RequireGroup(cell.groupId, MapLayer.BackgroundTiles);
            foreach (MapObject item in room.objects) RequireGroup(item.groupId, item.layer);
        }

        private void AddRoom(JsonElement operation)
        {
            Fields(operation, "op", "id", "name", "x", "y", "width", "height");
            string id = NewId(operation);
            string? name = operation.TryGetProperty("name", out _) ? Text(operation, "name") : null;
            var bounds = Bounds(operation);
            string created = "";
            CoreRoom(null, rooms => created = rooms.Create(bounds, name));
            Room(created, false).id = id;
        }

        private void UpdateRoom(JsonElement operation)
        {
            Fields(operation, "op", "roomId", "name", "visible");
            MapRoom room = Room(operation);
            if (operation.TryGetProperty("name", out _)) room.name = Text(operation, "name");
            if (operation.TryGetProperty("visible", out _)) room.visible = Boolean(operation, "visible");
        }

        private void MoveRoom(JsonElement operation)
        {
            Fields(operation, "op", "roomId", "x", "y");
            MapRoom room = Room(operation);
            var desired = new Vector2Int(checked(Integer(operation, "x") - room.x), checked(Integer(operation, "y") - room.y));
            var delta = MapRoomCollision.Resolve(room, desired, Document.rooms);
            CoreRoom(room.id, rooms => rooms.MoveSelected(delta));
        }

        private void ResizeRoom(JsonElement operation)
        {
            Fields(operation, "op", "roomId", "x", "y", "width", "height", "crop", "keepLocalContents");
            MapRoom room = Room(operation);
            RequireContentsUnlocked(room);
            var bounds = Bounds(operation, room.x, room.y);
            bool crop = Boolean(operation, "crop", false), local = Boolean(operation, "keepLocalContents", false);
            CoreRoom(room.id, rooms =>
            {
                if (local) rooms.ResizeKeepingLocalContents(room.id, bounds, crop); else rooms.Resize(room.id, bounds, crop);
            }, true);
        }

        private void RotateRoom(JsonElement operation)
        {
            Fields(operation, "op", "roomId", "clockwise");
            MapRoom room = Room(operation);
            RequireContentsUnlocked(room);
            CoreRoom(room.id, rooms => rooms.RotateSelected(Boolean(operation, "clockwise", true)), true);
        }

        private void DuplicateRoom(JsonElement operation)
        {
            Fields(operation, "op", "roomId", "id", "name", "x", "y");
            MapRoom room = Room(operation);
            RequireContentsUnlocked(room);
            string id = NewId(operation), created = "";
            int x = Integer(operation, "x"), y = Integer(operation, "y");
            CoreRoom(room.id, rooms => { rooms.CopySelected(); created = rooms.Paste(new Vector2Int(x, y)).Single(); });
            MapRoom copy = Room(created, false);
            copy.id = id;
            if (operation.TryGetProperty("name", out _)) copy.name = Text(operation, "name");
            var delta = MapRoomCollision.Resolve(copy, Vector2Int.zero, Document.rooms);
            copy.x = checked(copy.x + delta.x); copy.y = checked(copy.y + delta.y);
            for (int index = 0; index < copy.objects.Count; index++)
            {
                string objectId = "object-" + Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(id + "\0" + room.objects[index].id)))[..32];
                if (!ids.Add(objectId)) throw new ArgumentException("A derived duplicate object ID is already in use.");
                copy.objects[index].id = objectId;
                CreatedIds.Add(objectId);
            }
        }

        private void DeleteRoom(JsonElement operation)
        {
            Fields(operation, "op", "roomId");
            MapRoom room = Room(operation);
            RequireContentsUnlocked(room);
            Document.rooms.Remove(room);
        }

        private Dictionary<int, MapCell> Cells(MapRoom room, MapLayer layer)
        {
            var key = (room.id, layer);
            if (tileIndexes.TryGetValue(key, out var result)) return result;
            List<MapCell> source = layer == MapLayer.ForegroundTiles ? room.foreground : room.background;
            Charge(source.Count);
            result = source.ToDictionary(cell => CellKey(cell.x, cell.y));
            tileIndexes.Add(key, result);
            return result;
        }

        private void SetCell(MapRoom room, MapLayer layer, Dictionary<int, MapCell> cells, int x, int y, MapCell? template)
        {
            Visit();
            if (!room.Contains(x, y)) throw new ArgumentException("Tile coordinates must be inside the room.");
            int key = CellKey(x, y);
            if (cells.TryGetValue(key, out MapCell? previous)) RequireGroup(previous.groupId, layer);
            if (template == null) cells.Remove(key);
            else cells[key] = new MapCell { x = x, y = y, material = template.material, groupId = template.groupId, shape = template.shape };
        }

        private MapCell? Template(JsonElement operation, MapLayer layer, bool erase)
        {
            string group = OptionalText(operation, "groupId", "", true);
            RequireGroup(group, layer);
            if (erase) return null;
            return new MapCell { material = OptionalText(operation, "materialId", "terrain"), shape = Shape(operation), groupId = group };
        }

        private void Paint(JsonElement operation, bool erase)
        {
            Fields(operation, "op", "roomId", "layer", "cells", "materialId", "groupId", "shape");
            MapRoom room = Room(operation);
            MapLayer layer = TileLayer(operation);
            MapCell? template = Template(operation, layer, erase);
            JsonElement points = Required(operation, "cells");
            if (points.ValueKind != JsonValueKind.Array || points.GetArrayLength() > MaximumCellVisits)
                throw new ArgumentException("cells must be a bounded array.");
            var cells = Cells(room, layer);
            foreach (JsonElement point in points.EnumerateArray())
            {
                Fields(point, "x", "y", "shape");
                MapCell? value = template == null ? null : new MapCell { material = template.material, groupId = template.groupId,
                    shape = point.TryGetProperty("shape", out _) ? Shape(point) : template.shape };
                SetCell(room, layer, cells, Integer(point, "x"), Integer(point, "y"), value);
            }
        }

        private void Rectangle(JsonElement operation)
        {
            Fields(operation, "op", "roomId", "layer", "x", "y", "width", "height", "materialId", "groupId", "shape", "erase");
            MapRoom room = Room(operation);
            MapLayer layer = TileLayer(operation);
            var bounds = Bounds(operation);
            if (bounds.x < 0 || bounds.y < 0 || (long)bounds.x + bounds.width > room.width || (long)bounds.y + bounds.height > room.height)
                throw new ArgumentException("The rectangle must fit inside the room.");
            if ((long)bounds.width * bounds.height > MaximumCellVisits - visits) throw new ArgumentException("The rectangle exceeds the remaining cell budget.");
            MapCell? template = Template(operation, layer, Boolean(operation, "erase", false));
            var cells = Cells(room, layer);
            for (int y = bounds.y; y < bounds.y + bounds.height; y++)
                for (int x = bounds.x; x < bounds.x + bounds.width; x++) SetCell(room, layer, cells, x, y, template);
        }

        private void Fill(JsonElement operation)
        {
            Fields(operation, "op", "roomId", "layer", "x", "y", "materialId", "groupId", "shape", "erase");
            MapRoom room = Room(operation);
            MapLayer layer = TileLayer(operation);
            int x = Integer(operation, "x"), y = Integer(operation, "y");
            if (!room.Contains(x, y)) throw new ArgumentException("The fill origin must be inside the room.");
            var cells = Cells(room, layer);
            cells.TryGetValue(CellKey(x, y), out var original);
            MapCell? template = Template(operation, layer, Boolean(operation, "erase", false));
            if (SameCell(original, template)) return;
            var seen = new HashSet<int>();
            var pending = new Queue<(int X, int Y)>();
            pending.Enqueue((x, y)); seen.Add(CellKey(x, y));
            while (pending.TryDequeue(out var at))
            {
                Charge(1);
                cells.TryGetValue(CellKey(at.X, at.Y), out var current);
                if (!SameCell(current, original)) continue;
                SetCell(room, layer, cells, at.X, at.Y, template);
                Enqueue(at.X - 1, at.Y); Enqueue(at.X + 1, at.Y); Enqueue(at.X, at.Y - 1); Enqueue(at.X, at.Y + 1);
            }
            void Enqueue(int nextX, int nextY)
            {
                if (room.Contains(nextX, nextY) && seen.Add(CellKey(nextX, nextY))) pending.Enqueue((nextX, nextY));
            }
        }

        private void AddObject(JsonElement operation)
        {
            Fields(operation, "op", "roomId", "id", "definitionId", "layer", "groupId", "x", "y", "width", "height", "rotation", "scaleX", "scaleY", "nodes", "properties");
            MapRoom room = Room(operation);
            if (room.objects.Count >= MapDocument.MaximumObjectsPerRoom) throw new ArgumentException("The room has reached its object limit.");
            var item = new MapObject { id = NewId(operation), definition = Text(operation, "definitionId"), layer = ObjectLayer(operation),
                groupId = OptionalText(operation, "groupId", "", true), x = Number(operation, "x"), y = Number(operation, "y") };
            ApplyObjectFields(item, operation);
            RequireGroup(item.groupId, item.layer);
            ValidateObjectBounds(room, item);
            room.objects.Add(item);
        }

        private void UpdateObject(JsonElement operation)
        {
            Fields(operation, "op", "roomId", "objectId", "definitionId", "groupId", "x", "y", "width", "height", "rotation", "scaleX", "scaleY", "nodes", "properties");
            MapRoom room = Room(operation);
            Charge(room.objects.Count);
            MapObject item = Object(room, Text(operation, "objectId"));
            RequireGroup(item.groupId, item.layer);
            ApplyObjectFields(item, operation);
            RequireGroup(item.groupId, item.layer);
            ValidateObjectBounds(room, item);
        }

        private void ApplyObjectFields(MapObject item, JsonElement operation)
        {
            if (operation.TryGetProperty("definitionId", out _)) item.definition = Text(operation, "definitionId");
            if (operation.TryGetProperty("groupId", out _)) item.groupId = Text(operation, "groupId", true);
            item.x = Number(operation, "x", item.x); item.y = Number(operation, "y", item.y);
            item.width = Number(operation, "width", item.width); item.height = Number(operation, "height", item.height);
            item.rotation = Number(operation, "rotation", item.rotation);
            item.scaleX = Number(operation, "scaleX", item.scaleX); item.scaleY = Number(operation, "scaleY", item.scaleY);
            if (operation.TryGetProperty("nodes", out JsonElement nodes))
            {
                if (nodes.ValueKind != JsonValueKind.Array || nodes.GetArrayLength() > MapDocument.MaximumNodesPerObject)
                    throw new ArgumentException("nodes must be an array of at most 4096 points.");
                Charge(nodes.GetArrayLength());
                item.nodes = nodes.EnumerateArray().Select(node => { Fields(node, "x", "y"); return new Vector2(Number(node, "x"), Number(node, "y")); }).ToList();
            }
            if (operation.TryGetProperty("properties", out JsonElement properties)) MergeProperties(item.properties, properties);
        }

        private void DeleteObject(JsonElement operation)
        {
            Fields(operation, "op", "roomId", "objectId");
            MapRoom room = Room(operation);
            Charge(room.objects.Count);
            MapObject item = Object(room, Text(operation, "objectId"));
            RequireGroup(item.groupId, item.layer);
            room.objects.Remove(item);
        }

        private void SetProperties(JsonElement operation)
        {
            Fields(operation, "op", "roomId", "values");
            var target = operation.TryGetProperty("roomId", out _) ? Room(operation).properties : Document.properties;
            MergeProperties(target, Required(operation, "values"));
        }

        private void MergeProperties(List<MapProperty> target, JsonElement values)
        {
            if (values.ValueKind != JsonValueKind.Object) throw new ArgumentException("Properties must be an object containing string or null values.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in values.EnumerateObject())
            {
                Charge(target.Count + 1);
                if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 256 || !seen.Add(property.Name))
                    throw new ArgumentException("Property keys must be unique, nonempty strings of at most 256 characters.");
                int index = target.FindIndex(item => item.key == property.Name);
                if (property.Value.ValueKind == JsonValueKind.Null) { if (index >= 0) target.RemoveAt(index); continue; }
                if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length > 65536)
                    throw new ArgumentException("Property values must be strings of at most 65536 characters or null.");
                string value = property.Value.GetString()!;
                if (index < 0) target.Add(new MapProperty { key = property.Name, value = value }); else target[index].value = value;
            }
        }
    }

    private static MapObject Object(MapRoom room, string id) => room.objects.Find(item => item.id == id) ?? throw new ArgumentException("Unknown object ID '" + id + "' in the requested room.");
    private static bool SameCell(MapCell? left, MapCell? right) => left == null ? right == null : right != null && left.material == right.material && left.shape == right.shape && left.groupId == right.groupId;
    private static int CellKey(int x, int y) => x * MapDocument.MaximumRoomDimension + y;
    private static void ValidateObjectBounds(MapRoom room, MapObject item)
    {
        if (item.width <= 0 || item.height <= 0 || item.scaleX == 0 || item.scaleY == 0) throw new ArgumentException("Object dimensions must be positive and scales must be nonzero.");
        Rect bounds = MapObjectEditing.Bounds(item);
        const float tolerance = 0.0001f;
        if (!float.IsFinite(bounds.xMin) || !float.IsFinite(bounds.yMin) || !float.IsFinite(bounds.xMax) || !float.IsFinite(bounds.yMax)
            || bounds.xMin < -tolerance || bounds.yMin < -tolerance || bounds.xMax > room.width + tolerance || bounds.yMax > room.height + tolerance)
            throw new ArgumentException("The object must fit inside its room.");
        foreach (Vector2 node in item.nodes)
            if (node.x < 0 || node.y < 0 || node.x > room.width || node.y > room.height) throw new ArgumentException("Object nodes must be inside the room.");
    }

    private static void Fields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal)) throw new ArgumentException("Unknown or duplicate field '" + property.Name + "'.");
    }
    private static JsonElement Required(JsonElement value, string key) => value.TryGetProperty(key, out JsonElement result) ? result : throw new ArgumentException("Missing field '" + key + "'.");
    private static string Text(JsonElement value, string key, bool allowEmpty = false)
    {
        JsonElement result = Required(value, key);
        if (result.ValueKind != JsonValueKind.String || (!allowEmpty && string.IsNullOrWhiteSpace(result.GetString())) || result.GetString()!.Length > 256)
            throw new ArgumentException("'" + key + "' must be a string of at most 256 characters.");
        return result.GetString()!;
    }
    private static string OptionalText(JsonElement value, string key, string fallback, bool allowEmpty = false) => value.TryGetProperty(key, out _) ? Text(value, key, allowEmpty) : fallback;
    private static int Integer(JsonElement value, string key, int? fallback = null)
    {
        if (!value.TryGetProperty(key, out var result)) return fallback ?? throw new ArgumentException("Missing integer '" + key + "'.");
        if (result.ValueKind != JsonValueKind.Number || !result.TryGetInt32(out int integer)) throw new ArgumentException("'" + key + "' must be a 32-bit integer.");
        return integer;
    }
    private static float Number(JsonElement value, string key, float? fallback = null)
    {
        if (!value.TryGetProperty(key, out var result)) return fallback ?? throw new ArgumentException("Missing number '" + key + "'.");
        if (result.ValueKind != JsonValueKind.Number || !result.TryGetSingle(out float number) || !float.IsFinite(number)) throw new ArgumentException("'" + key + "' must be a finite number.");
        return number;
    }
    private static bool Boolean(JsonElement value, string key, bool? fallback = null)
    {
        if (!value.TryGetProperty(key, out var result)) return fallback ?? throw new ArgumentException("Missing boolean '" + key + "'.");
        if (result.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("'" + key + "' must be a boolean.");
        return result.GetBoolean();
    }
    private static RectInt Bounds(JsonElement value, int? x = null, int? y = null)
    {
        int left = Integer(value, "x", x), bottom = Integer(value, "y", y), width = Integer(value, "width"), height = Integer(value, "height");
        if (width < 1 || height < 1 || width > MapDocument.MaximumRoomDimension || height > MapDocument.MaximumRoomDimension || (long)left + width > int.MaxValue || (long)bottom + height > int.MaxValue)
            throw new ArgumentException("Room dimensions must be 1..1024 and bounds must fit 32-bit coordinates.");
        return new RectInt(left, bottom, width, height);
    }
    private static TileShape Shape(JsonElement value) => OptionalText(value, "shape", "solid") switch
    {
        "solid" => TileShape.Solid, "bottomLeft" => TileShape.BottomLeft, "bottomRight" => TileShape.BottomRight,
        "topLeft" => TileShape.TopLeft, "topRight" => TileShape.TopRight, _ => throw new ArgumentException("Unknown tile shape.")
    };
    private static MapLayer TileLayer(JsonElement value) => Text(value, "layer") switch
    { "foreground" => MapLayer.ForegroundTiles, "background" => MapLayer.BackgroundTiles, _ => throw new ArgumentException("Tile layer must be foreground or background.") };
    private static MapLayer ObjectLayer(JsonElement value) => Text(value, "layer") switch
    {
        "entities" => MapLayer.Entities, "triggers" => MapLayer.Triggers, "foregroundDecals" => MapLayer.ForegroundDecals,
        "backgroundDecals" => MapLayer.BackgroundDecals, _ => throw new ArgumentException("Unknown object layer.")
    };
    private static string LayerName(MapLayer layer) => layer switch
    {
        MapLayer.ForegroundTiles => "foreground", MapLayer.BackgroundTiles => "background", MapLayer.Entities => "entities",
        MapLayer.Triggers => "triggers", MapLayer.ForegroundDecals => "foregroundDecals", MapLayer.BackgroundDecals => "backgroundDecals", _ => throw new ArgumentException("Unknown layer.")
    };
}
