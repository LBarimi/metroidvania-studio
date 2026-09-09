using System;
using System.Collections.Generic;
using System.Linq;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public sealed partial class MapObjectEditing
    {
        public const int MaximumBrushPathCells = 16384;
        private const int MaximumBrushIntersectionWork = 262144;

        public static bool IsCellBrushDefinition(string id) =>
            string.Equals(id, "Spawn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, "Respawn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, "Portal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, "InvisibleWall", StringComparison.OrdinalIgnoreCase);

        /// <summary>One object per empty tile cell, with one history entry for the entire path.</summary>
        public IReadOnlyList<string> PaintCells(MapObjectDefinition definition, IReadOnlyList<Vector2> points)
        {
            RequireIdle();
            if (definition == null || !IsCellBrushDefinition(definition.id)) throw new ArgumentException("Choose a grid object brush.");
            if (points == null || points.Count == 0 || points.Count > MaximumBrushPathCells) throw new ArgumentException("Invalid object brush path.");
            if (!RoomEditable()) return Array.Empty<string>();
            MapLayer layer = controller.Layer == MapLayer.All ? definition.layer : controller.Layer;
            string group = controller.ActiveGroupId;
            if (!controller.CanEditMember(layer, group)) return Array.Empty<string>();
            var cells = new List<Vector2Int>();
            var visited = new HashSet<Vector2Int>();
            Vector2Int previous = default;
            int rasterWork = 0;
            for (int index = 0; index < points.Count; index++)
            {
                ValidatePoint(points[index], nameof(points));
                var next = new Vector2Int(checked((int)Math.Floor(points[index].x)), checked((int)Math.Floor(points[index].y)));
                long length = index == 0 ? 1 : Math.Max(Math.Abs((long)next.x - previous.x), Math.Abs((long)next.y - previous.y));
                if (length > MaximumBrushPathCells - rasterWork) throw new ArgumentException("Object brush path is too long.");
                rasterWork += (int)length;
                foreach (var cell in MapBrushGeometry.Line(index == 0 ? next : previous, next))
                    if (cell.x >= 0 && cell.y >= 0 && cell.x < Room.width && cell.y < Room.height && visited.Add(cell)) cells.Add(cell);
                previous = next;
            }

            // Tile-sized bodies use constant-time occupancy checks. Legacy regions and
            // transformed bodies retain their authored geometry and are checked separately.
            var occupied = new HashSet<Vector2Int>();
            var regions = new List<(MapObject item, Rect bounds)>();
            foreach (MapObject item in Room.objects)
            {
                if (item.layer != layer) continue;
                if (item.width == 1 && item.height == 1 && item.rotation == 0 && item.scaleX == 1 && item.scaleY == 1
                    && item.x == Math.Floor(item.x) && item.y == Math.Floor(item.y))
                    occupied.Add(new Vector2Int((int)item.x, (int)item.y));
                else regions.Add((item, Bounds(item)));
            }
            var pending = new List<MapObject>();
            int intersectionWork = 0;
            foreach (Vector2Int cell in cells)
            {
                if (occupied.Contains(cell)) continue;
                // Touching the next tile's edge is allowed; positive-area overlap is not.
                var area = new Rect(cell.x + .00001f, cell.y + .00001f, .99998f, .99998f);
                bool blocked = false;
                foreach (var region in regions)
                {
                    if (++intersectionWork > MaximumBrushIntersectionWork) throw new ArgumentException("Object brush intersection work is too large.");
                    if (region.bounds.Overlaps(area) && Overlaps(region.item, area)) { blocked = true; break; }
                }
                if (blocked) continue;
                MapObject item = definition.Create(new Vector2(cell.x, cell.y), Vector2.one, layer, group);
                if (item.width != 1 || item.height != 1) throw new ArgumentException("Grid object brushes require a one-tile definition.");
                pending.Add(item);
            }
            if (pending.Count == 0) return Array.Empty<string>();
            var ids = AllIds();
            foreach (MapObject item in pending) item.id = NewId(ids);
            AddObjectsIncrementally("Paint objects", pending);
            // Keep the inspector small after a long stroke.
            ReplaceSelection(new[] { pending[pending.Count - 1].id });
            return pending.Select(item => item.id).ToArray();
        }
    }
}
