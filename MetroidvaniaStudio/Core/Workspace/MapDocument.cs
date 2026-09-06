using System;
using System.Collections.Generic;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public enum MapLayer
    {
        ForegroundTiles,
        BackgroundTiles,
        Entities,
        Triggers,
        ForegroundDecals,
        BackgroundDecals,
        All = 6
    }

    [Serializable]
    public sealed class MapProperty
    {
        public string key = "";
        public string value = "";
    }

    [Serializable]
    public sealed class MapCell
    {
        public int x;
        public int y;
        public TileShape shape;
        public string material = "terrain";
        public string groupId = "";
    }

    [Serializable]
    public sealed class MapObject
    {
        public string id = Guid.NewGuid().ToString("N");
        public string definition = "object";
        public MapLayer layer = MapLayer.Entities;
        public string groupId = "";
        public float x;
        public float y;
        public float width = 1;
        public float height = 1;
        public float rotation;
        public float scaleX = 1;
        public float scaleY = 1;
        public List<Vector2> nodes = new List<Vector2>();
        public List<MapProperty> properties = new List<MapProperty>();
    }

    [Serializable]
    public sealed class MapStyleground
    {
        public string id = Guid.NewGuid().ToString("N");
        public string name = "Styleground";
        public string type = "parallax";
        public MapLayer layer = MapLayer.BackgroundDecals;
        public string texture = "";
        public string color = "#FFFFFF";
        public float scrollX = 1;
        public float scrollY = 1;
        public List<MapProperty> properties = new List<MapProperty>();
    }

    [Serializable]
    public sealed class MapRoom
    {
        public string id = Guid.NewGuid().ToString("N");
        public string name = "room_00";
        public int x;
        public int y;
        public int width = 40;
        public int height = 24;
        public bool visible = true;
        public bool locked;
        public List<MapCell> foreground = new List<MapCell>();
        public List<MapCell> background = new List<MapCell>();
        public List<MapObject> objects = new List<MapObject>();
        public List<MapProperty> properties = new List<MapProperty>();

        public bool Contains(int localX, int localY)
        {
            return localX >= 0 && localY >= 0 && localX < width && localY < height;
        }

        public MapCell GetCell(MapLayer layer, int localX, int localY)
        {
            List<MapCell> cells = GetCells(layer);
            for (int i = 0; i < cells.Count; i++)
                if (cells[i].x == localX && cells[i].y == localY)
                    return cells[i];
            return null;
        }

        public void SetCell(MapLayer layer, int localX, int localY, MapCell cell)
        {
            List<MapCell> cells = GetCells(layer);
            if (!Contains(localX, localY))
                throw new ArgumentOutOfRangeException(nameof(localX), "Tile coordinates must be inside the room.");
            if (cell != null && (cell.shape < TileShape.Solid || cell.shape > TileShape.TopRight))
                throw new ArgumentOutOfRangeException(nameof(cell), "Unknown tile shape.");

            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i].x != localX || cells[i].y != localY) continue;
                if (cell == null)
                    cells.RemoveAt(i);
                else
                    cells[i] = CopyCell(cell, localX, localY);
                return;
            }
            if (cell != null)
                cells.Add(CopyCell(cell, localX, localY));
        }

        private List<MapCell> GetCells(MapLayer layer)
        {
            if (layer == MapLayer.ForegroundTiles) return foreground;
            if (layer == MapLayer.BackgroundTiles) return background;
            throw new ArgumentException("Cell operations require a foreground or background tile layer.", nameof(layer));
        }

        private static MapCell CopyCell(MapCell cell, int x, int y)
        {
            // Brushes may reuse their template cell; placing it must not move an earlier cell.
            return new MapCell { x = x, y = y, shape = cell.shape, material = cell.material, groupId = cell.groupId };
        }
    }

    [Serializable]
    public sealed class MapDocument
    {
        public const int CurrentFormatVersion = 2;
        public const int RequiredTileSize = 16;
        public const int MaximumRoomDimension = 1024;
        public const int MaximumRoomCount = 1024;
        public const int MaximumObjectsPerRoom = 16384;
        public const int MaximumNodesPerObject = 4096;

        public int formatVersion = CurrentFormatVersion;
        public int tileSize = RequiredTileSize;
        public string name = "Untitled";
        public List<MapRoom> rooms = new List<MapRoom>();
        public List<MapProperty> properties = new List<MapProperty>();
        public List<MapStyleground> stylegrounds = new List<MapStyleground>();
        public List<MapLayerGroup> layerGroups = new List<MapLayerGroup>();

        public static MapDocument CreateDefault()
        {
            var document = new MapDocument();
            document.rooms.Add(new MapRoom { id = "room_00", name = "room_00", width = 40, height = 24 });
            return document;
        }

        public MapDocument Clone()
        {
            Validate();
            return MapJson.FromJson<MapDocument>(MapJson.ToJson(this));
        }

        public void Validate()
        {
            Require(formatVersion == CurrentFormatVersion,
                "Unsupported map format version " + formatVersion + ". This editor supports version " + CurrentFormatVersion + ".");
            Require(tileSize == RequiredTileSize, "Map tiles must be exactly 16 pixels.");
            Require(name != null, "Map name cannot be null.");
            Require(rooms != null, "Map rooms are missing.");
            Require(rooms.Count <= MaximumRoomCount, "A map cannot contain more than " + MaximumRoomCount + " rooms.");
            Require(stylegrounds != null, "Map stylegrounds are missing.");
            ValidateProperties(properties, "Map");
            MapCameraSettings.Resolve(this);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, MapLayerGroup> groups = MapLayerGroups.Validate(this, ids);

            foreach (MapRoom room in rooms)
            {
                Require(room != null, "Map contains a null room.");
                ValidateId(room.id, ids, "Room");
                string context = "Room '" + room.name + "'";
                Require(room.name != null, context + " has a null name.");
                Require(room.width > 0 && room.height > 0 && room.width <= MaximumRoomDimension && room.height <= MaximumRoomDimension,
                    context + " dimensions must be between 1 and " + MaximumRoomDimension + " tiles.");
                Require((long)room.x + room.width <= int.MaxValue && (long)room.y + room.height <= int.MaxValue,
                    context + " bounds exceed the supported coordinate range.");
                ValidateCells(room, room.foreground, MapLayer.ForegroundTiles, groups);
                ValidateCells(room, room.background, MapLayer.BackgroundTiles, groups);
                ValidateProperties(room.properties, context);
                Require(room.objects != null, context + " objects are missing.");
                Require(room.objects.Count <= MaximumObjectsPerRoom,
                    context + " cannot contain more than " + MaximumObjectsPerRoom + " objects.");
                foreach (MapObject item in room.objects)
                {
                    Require(item != null, context + " contains a null object.");
                    ValidateId(item.id, ids, "Object");
                    Require(!string.IsNullOrWhiteSpace(item.definition), "Object '" + item.id + " needs a definition.");
                    Require(item.layer >= MapLayer.Entities && item.layer <= MapLayer.BackgroundDecals,
                        "Object '" + item.id + " must use an entity, trigger, or decal layer.");
                    ValidateGroupReference(item.groupId, item.layer, groups, "Object '" + item.id + "'");
                    Require(Finite(item.x) && Finite(item.y) && Finite(item.width) && Finite(item.height)
                        && Finite(item.rotation) && Finite(item.scaleX) && Finite(item.scaleY),
                        "Object '" + item.id + " has non-finite position, size, rotation, or scale.");
                    Require(item.width > 0 && item.height > 0, "Object '" + item.id + " size must be positive.");
                    Require(item.nodes != null, "Object '" + item.id + " nodes are missing.");
                    Require(item.nodes.Count <= MaximumNodesPerObject,
                        "Object '" + item.id + " cannot contain more than " + MaximumNodesPerObject + " nodes.");
                    foreach (Vector2 node in item.nodes)
                        Require(Finite(node.x) && Finite(node.y), "Object '" + item.id + " has a non-finite node.");
                    ValidateProperties(item.properties, "Object '" + item.id + "'");
                }
            }

            foreach (MapStyleground style in stylegrounds)
            {
                Require(style != null, "Map contains a null styleground.");
                ValidateId(style.id, ids, "Styleground");
                Require(style.name != null && style.type != null && style.texture != null && style.color != null,
                    "Styleground '" + style.id + " contains null text fields.");
                Require(style.layer >= MapLayer.ForegroundTiles && style.layer <= MapLayer.BackgroundDecals,
                    "Styleground '" + style.id + " has an unknown layer.");
                Require(Finite(style.scrollX) && Finite(style.scrollY), "Styleground '" + style.id + " has a non-finite scroll factor.");
                ValidateProperties(style.properties, "Styleground '" + style.id + "'");
            }
            MapStylegroundEditing.ValidateContents(this);
        }

        private static void ValidateCells(MapRoom room, List<MapCell> cells, MapLayer layer, Dictionary<string, MapLayerGroup> groups)
        {
            string context = "Room '" + room.name + "' " + layer;
            if (cells == null) throw new InvalidOperationException(context + " cells are missing.");
            if (cells.Count == 0) return;
            // Valid local coordinates need only 20 bits. Packing them into a long's
            // opposite halves makes Int64.GetHashCode collapse whole grids to x ^ y.
            var positions = new HashSet<int>();
            foreach (MapCell cell in cells)
            {
                if (cell == null) throw new InvalidOperationException(context + " contains a null cell.");
                if (!room.Contains(cell.x, cell.y))
                    throw new InvalidOperationException(context + " cell (" + cell.x + ", " + cell.y + ") is outside the room.");
                if (!positions.Add(cell.x * MaximumRoomDimension + cell.y))
                    throw new InvalidOperationException(context + " contains duplicate cell (" + cell.x + ", " + cell.y + ").");
                if (cell.shape < TileShape.Solid || cell.shape > TileShape.TopRight)
                    throw new InvalidOperationException(context + " has an unknown tile shape.");
                if (string.IsNullOrWhiteSpace(cell.material)) throw new InvalidOperationException(context + " has a tile without a material.");
                ValidateGroupReference(cell.groupId, layer, groups, context);
            }
        }

        private static void ValidateGroupReference(string groupId, MapLayer layer, Dictionary<string, MapLayerGroup> groups, string context)
        {
            if (groupId == null) throw new InvalidOperationException(context + " has a null group ID.");
            if (groupId.Length == 0) return;
            if (!groups.TryGetValue(groupId, out MapLayerGroup group))
                throw new InvalidOperationException(context + " references missing group '" + groupId + "'.");
            if (group.layer != layer) throw new InvalidOperationException(context + " references a group from another layer.");
        }

        private static void ValidateProperties(List<MapProperty> values, string context)
        {
            if (values == null) throw new InvalidOperationException(context + " properties are missing.");
            if (values.Count == 0) return;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (MapProperty property in values)
            {
                if (property == null || string.IsNullOrWhiteSpace(property.key)) throw new InvalidOperationException(context + " has a property without a key.");
                if (!keys.Add(property.key)) throw new InvalidOperationException(context + " has duplicate property '" + property.key + "'.");
                if (property.value == null) throw new InvalidOperationException(context + " property '" + property.key + "' has a null value.");
            }
        }

        private static void ValidateId(string id, HashSet<string> ids, string context)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException(context + " ID is missing.");
            if (!ids.Add(id)) throw new InvalidOperationException("Duplicate map ID '" + id + "'.");
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
