using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MetroidvaniaStudio.Integration
{
    /// <summary>Engine adapter: JSON tile coordinates are scaled to physical world units at import.</summary>
    public static class StudioRoomBuilder
    {
        public static MapDocument Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 32 * 1024 * 1024) throw new InvalidOperationException("Map JSON is empty or too large.");
            var document = new MapDocument { formatVersion = 0, tileSize = 0, rooms = null };
            JsonUtility.FromJsonOverwrite(json.TrimStart('\uFEFF'), document);
            Validate(document); return document;
        }
        public static void Validate(MapDocument document)
        {
            if (document == null || document.formatVersion < 1 || document.formatVersion > 2 || document.tileSize != 16
                || document.rooms == null || document.rooms.Count > 1024 || document.properties == null || document.layerGroups == null)
                throw new InvalidOperationException("A supported map document with 16 pixel tiles is required.");
            MapCameraSettings.Resolve(document);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var room in document.rooms)
            {
                if (room == null || string.IsNullOrEmpty(room.id) || !ids.Add(room.id) || room.width < 1 || room.height < 1 || room.width > 1024 || room.height > 1024
                    || room.foreground == null || room.background == null || room.objects == null || room.objects.Count > 16384)
                    throw new InvalidOperationException("Invalid room data.");
                foreach (var layer in new[] { room.foreground, room.background })
                {
                    if (layer.Count > room.width * room.height) throw new InvalidOperationException("Too many tile cells.");
                    var cells = new HashSet<long>();
                    foreach (var cell in layer)
                        if (cell == null || cell.x < 0 || cell.y < 0 || cell.x >= room.width || cell.y >= room.height || (int)cell.shape < 0 || (int)cell.shape > 4 || !cells.Add(Key(cell.x, cell.y)))
                            throw new InvalidOperationException("Invalid or duplicate tile cell.");
                }
                foreach (var item in room.objects)
                    if (item == null || string.IsNullOrWhiteSpace(item.id) || !ids.Add(item.id) || item.properties == null || item.nodes == null || item.nodes.Count > 4096 || !Finite(item.x) || !Finite(item.y) || !Finite(item.width) || !Finite(item.height)
                        || !Finite(item.rotation) || !Finite(item.scaleX) || !Finite(item.scaleY) || item.width <= 0 || item.height <= 0)
                        throw new InvalidOperationException("Invalid object geometry.");
            }
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static long Key(int x, int y) => ((long)x << 32) | (uint)y;
        public static GameObject Build(MapDocument document, string roomId, StudioResourceLibrary library, StudioTriggerManager triggerManager = null)
        {
            Validate(document);
            if (!library) throw new ArgumentNullException(nameof(library));
            MapRoom room = document.rooms.Find(value => value.id == roomId) ?? throw new InvalidOperationException("Room no longer exists.");
            float unit = 16f / library.ppu;
            var root = new GameObject("[Metroidvania Studio] " + room.name); root.SetActive(false);
            try
            {
                root.transform.position = new Vector3(room.x * unit, room.y * unit, 0);
                var marker = root.AddComponent<StudioImportedRoom>(); marker.roomId = room.id; marker.documentJson = JsonUtility.ToJson(document); marker.resources = library;
                var grid = root.AddComponent<Grid>(); grid.cellSize = new Vector3(unit, unit, 1);
                var groups = new Dictionary<string, MapLayerGroup>(StringComparer.Ordinal);
                foreach (var group in document.layerGroups) groups[group.id] = group;
                bool Visible(string id)
                {
                    int depth = 0;
                    while (!string.IsNullOrEmpty(id) && groups.TryGetValue(id, out var group))
                    { if (!group.visible || ++depth > groups.Count) return false; id = group.parentId; }
                    return true;
                }
                BuildTiles(room, room.background, root.transform, library, false, Visible);
                BuildTiles(room, room.foreground, root.transform, library, true, Visible);
                foreach (var data in room.objects)
                {
                    if (!Visible(data.groupId)) continue;
                    var item = new GameObject(data.definition); item.transform.SetParent(root.transform, false);
                    item.transform.localPosition = new Vector3((data.x + data.width * .5f) * unit, (data.y + data.height * .5f) * unit, 0);
                    item.transform.localRotation = Quaternion.Euler(0, 0, data.rotation);
                    item.transform.localScale = new Vector3(data.scaleX, data.scaleY, 1);
                    item.AddComponent<StudioPlacedObject>().data = data;
                    var sprite = library.ResolveObject(data.definition);
                    if (sprite)
                    {
                        var visual = new GameObject("Visual"); visual.transform.SetParent(item.transform, false);
                        visual.transform.localScale = new Vector3(data.width * unit / sprite.bounds.size.x, data.height * unit / sprite.bounds.size.y, 1);
                        var renderer = visual.AddComponent<SpriteRenderer>(); renderer.sprite = sprite;
                        renderer.sortingOrder = data.layer == MapLayer.BackgroundDecals ? -10 : data.layer == MapLayer.ForegroundDecals ? 20 : 10;
                    }
                    if (data.layer == MapLayer.Triggers) { var trigger = item.AddComponent<BoxCollider2D>(); trigger.isTrigger = true; trigger.size = new Vector2(data.width * unit, data.height * unit); }
                }
                marker.InitializeTriggers(room, triggerManager ?? new StudioTriggerManager());
                root.SetActive(true); return root;
            }
            catch { if (Application.isPlaying) UnityEngine.Object.Destroy(root); else UnityEngine.Object.DestroyImmediate(root); throw; }
        }
        private static void BuildTiles(MapRoom room, List<MapCell> source, Transform root, StudioResourceLibrary library, bool solid, Func<string, bool> visible)
        {
            var obj = new GameObject(solid ? "Foreground" : "Background"); obj.transform.SetParent(root, false);
            var map = obj.AddComponent<Tilemap>(); obj.AddComponent<TilemapRenderer>().sortingOrder = solid ? 0 : -20;
            var cells = new Dictionary<long, MapCell>(); foreach (var cell in source) if (visible(cell.groupId)) cells[Key(cell.x, cell.y)] = cell;
            var positions = new Vector3Int[cells.Count]; var tiles = new TileBase[cells.Count]; int index = 0;
            int[] dx = { 0, 1, 1, 1, 0, -1, -1, -1 }, dy = { 1, 1, 0, -1, -1, -1, 0, 1 };
            foreach (var cell in cells.Values)
            {
                int mask = 0;
                for (int side = 0; side < 8; side++)
                {
                    int x = cell.x + dx[side], y = cell.y + dy[side];
                    bool outside = x < 0 || y < 0 || x >= room.width || y >= room.height;
                    if (outside && TileMask.Connects(cell.shape, TileShape.Solid, new StudioGridPoint(dx[side], dy[side]))
                        || cells.TryGetValue(Key(x, y), out var neighbor) && neighbor.material == cell.material && TileMask.Connects(cell.shape, neighbor.shape, new StudioGridPoint(dx[side], dy[side]))) mask |= 1 << side;
                }
                positions[index] = new Vector3Int(cell.x, cell.y, 0); tiles[index++] = library.ResolveTile(cell.material, TileMask.Normalize(mask), cell.shape);
            }
            map.SetTiles(positions, tiles);
            if (solid)
            {
                obj.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Static;
                var composite = obj.AddComponent<CompositeCollider2D>(); composite.geometryType = CompositeCollider2D.GeometryType.Outlines;
                composite.generationType = CompositeCollider2D.GenerationType.Manual;
                var collider = obj.AddComponent<TilemapCollider2D>(); collider.compositeOperation = Collider2D.CompositeOperation.Merge;
                // One batched polygon collider supplies slope geometry in both edit and play mode.
                var slopes = new List<Vector2[]>(); float unit = 16f / library.ppu;
                foreach (var cell in cells.Values)
                {
                    if (cell.shape == TileShape.Solid) continue;
                    var polygon = TileMask.GetPolygon(cell.shape); var points = new Vector2[polygon.Length];
                    for (int i = 0; i < polygon.Length; i++) points[i] = new Vector2((cell.x + .5f + polygon[i].x) * unit, (cell.y + .5f + polygon[i].y) * unit);
                    slopes.Add(points);
                }
                if (slopes.Count > 0)
                {
                    var polygons = obj.AddComponent<PolygonCollider2D>(); polygons.pathCount = slopes.Count;
                    polygons.compositeOperation = Collider2D.CompositeOperation.Merge;
                    for (int i = 0; i < slopes.Count; i++) polygons.SetPath(i, slopes[i]);
                }
                collider.ProcessTilemapChanges(); composite.GenerateGeometry();
            }
        }
    }
}
