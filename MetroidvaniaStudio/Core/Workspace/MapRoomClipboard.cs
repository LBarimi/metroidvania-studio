using System;
using System.Collections.Generic;
using System.Linq;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public sealed partial class MapRoomEditing
    {
        private List<MapRoom> clipboard;
        private List<MapLayerGroup> clipboardGroups;

        public void CopySelected()
        {
            RequireIdle();
            var sources = EditableSelection();
            if (sources.Count == 0) return;
            clipboard = sources.Select(CloneRoom).ToList();
            clipboardGroups = MapJson.FromJson<List<MapLayerGroup>>(MapJson.ToJson(session.Document.layerGroups));
        }

        public void CutSelected() { CopySelected(); DeleteSelected(); }

        /// <summary>Paste a detached group with new room/object identities at its lower-left anchor.</summary>
        public string[] Paste(Vector2Int position)
        {
            RequireIdle();
            if (clipboard == null || clipboard.Count == 0) return Array.Empty<string>();
            long left = clipboard.Min(r => (long)r.x), bottom = clipboard.Min(r => (long)r.y);
            var names = new HashSet<string>(session.Document.rooms.Select(r => r.name), StringComparer.OrdinalIgnoreCase);
            var ids = AllIds();
            var copies = new List<MapRoom>();
            foreach (var source in clipboard)
            {
                long x = source.x - left + position.x, y = source.y - bottom + position.y;
                ValidateBounds(x, y, source.width, source.height);
                var copy = CloneRoom(source); copy.id = NewId(ids); copy.x = (int)x; copy.y = (int)y;
                string stem = source.name + "_copy", name = stem;
                for (int suffix = 2; names.Contains(name); suffix++) name = stem + "_" + suffix;
                names.Add(name); copy.name = name;
                foreach (var item in copy.objects) item.id = NewId(ids);
                copies.Add(copy);
            }
            // Group IDs keep their meaning even when a clipboard crosses documents.
            var groups = new List<MapLayerGroup>();
            var groupIds = new Dictionary<string, string>(StringComparer.Ordinal);
            bool remapTree = clipboardGroups.Any(source => ids.Contains(source.id)
                && !session.Document.layerGroups.Any(existing => existing.id == source.id && MapJson.ToJson(existing) == MapJson.ToJson(source)));
            foreach (var source in clipboardGroups)
            {
                var existing = session.Document.layerGroups.Find(g => g.id == source.id);
                if (!remapTree && existing != null && MapJson.ToJson(existing) == MapJson.ToJson(source)) continue;
                var group = MapJson.FromJson<MapLayerGroup>(MapJson.ToJson(source));
                if (remapTree || ids.Contains(group.id)) { group.id = NewId(ids); groupIds.Add(source.id, group.id); }
                else ids.Add(group.id);
                groups.Add(group);
            }
            foreach (var group in groups)
                if (groupIds.TryGetValue(group.parentId, out var parent)) group.parentId = parent;
            foreach (var copy in copies)
            {
                foreach (var cell in copy.foreground.Concat(copy.background))
                    if (groupIds.TryGetValue(cell.groupId, out var id)) cell.groupId = id;
                foreach (var item in copy.objects)
                    if (groupIds.TryGetValue(item.groupId, out var id)) item.groupId = id;
            }
            session.Execute("Paste rooms", document => { document.layerGroups.AddRange(groups); document.rooms.AddRange(copies); });
            var result = copies.Select(r => r.id).ToArray(); ReplaceSelection(result); return result;
        }

        public void FlipSelected(bool horizontal) => TransformRooms(horizontal, false, true);
        public void RotateSelected(bool clockwise) => TransformRooms(false, true, clockwise);

        private void TransformRooms(bool horizontal, bool rotate, bool clockwise)
        {
            RequireIdle();
            var sources = EditableSelection();
            if (sources.Count == 0) return;
            long left = sources.Min(r => (long)r.x), bottom = sources.Min(r => (long)r.y);
            long width = sources.Max(r => (long)r.x + r.width) - left;
            long height = sources.Max(r => (long)r.y + r.height) - bottom;
            var copies = new Dictionary<string, MapRoom>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                var copy = CloneRoom(source);
                int w = source.width, h = source.height;
                long rx = source.x - left, ry = source.y - bottom;
                long x = rotate ? left + (clockwise ? ry : height - ry - h) : horizontal ? left + width - rx - w : source.x;
                long y = rotate ? bottom + (clockwise ? width - rx - w : rx) : horizontal ? source.y : bottom + height - ry - h;
                copy.width = rotate ? h : w; copy.height = rotate ? w : h;
                ValidateBounds(x, y, copy.width, copy.height); copy.x = (int)x; copy.y = (int)y;
                foreach (var cell in copy.foreground.Concat(copy.background))
                {
                    int cx = cell.x, cy = cell.y;
                    cell.x = rotate ? clockwise ? cy : h - 1 - cy : horizontal ? w - 1 - cx : cx;
                    cell.y = rotate ? clockwise ? w - 1 - cx : cx : horizontal ? cy : h - 1 - cy;
                    cell.shape = rotate ? MapBrushGeometry.RotateShape(cell.shape, clockwise) : MapBrushGeometry.FlipShape(cell.shape, horizontal);
                }
                Vector2 Transform(Vector2 point) => rotate
                    ? clockwise ? new Vector2(point.y, w - point.x) : new Vector2(h - point.y, point.x)
                    : horizontal ? new Vector2(w - point.x, point.y) : new Vector2(point.x, h - point.y);
                foreach (var item in copy.objects)
                {
                    var center = Transform(new Vector2(item.x + item.width / 2, item.y + item.height / 2));
                    item.x = center.x - item.width / 2; item.y = center.y - item.height / 2;
                    if (rotate) item.rotation += clockwise ? -90 : 90;
                    else { item.rotation = -item.rotation; if (horizontal) item.scaleX = -item.scaleX; else item.scaleY = -item.scaleY; }
                    for (int i = 0; i < item.nodes.Count; i++) item.nodes[i] = Transform(item.nodes[i]);
                }
                copies.Add(copy.id, copy);
            }
            // A single rectangular room changing size uses the same gap-preserving layout as resize.
            var first = copies.Values.First();
            MapRoomResizePlan layout = rotate && copies.Count == 1
                ? MapRoomResizeLayout.Plan(session.Document.rooms, first.id, new RectInt(first.x, first.y, first.width, first.height)) : null;
            session.Execute(rotate ? "Rotate rooms" : "Flip rooms", document =>
            {
                if (layout != null) foreach (var change in layout.Changes)
                {
                    if (copies.ContainsKey(change.Id)) continue;
                    var neighbor = document.rooms.Find(r => r.id == change.Id);
                    neighbor.x = change.After.x; neighbor.y = change.After.y;
                }
                for (int i = 0; i < document.rooms.Count; i++)
                    if (copies.TryGetValue(document.rooms[i].id, out var copy)) document.rooms[i] = copy;
            });
        }

        private static MapRoom CloneRoom(MapRoom room) => MapJson.FromJson<MapRoom>(MapJson.ToJson(room));
    }
}
