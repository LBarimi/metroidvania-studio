using System;
using System.Collections.Generic;
using System.Linq;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public sealed partial class MapRoomEditing
    {
        /// <summary>Combines rooms without moving world contents or replacing placement IDs.</summary>
        public string MergeSelected()
        {
            RequireIdle();
            List<MapRoom> sources = EditableSelection();
            if (sources.Count < 2) throw new InvalidOperationException("@roomMergeNeedSelection");
            MapRoom primary = sources.Single(room => room.id == PrimaryId);
            long left = sources.Min(room => (long)room.x), bottom = sources.Min(room => (long)room.y);
            long right = sources.Max(room => (long)room.x + room.width), top = sources.Max(room => (long)room.y + room.height);
            if (right - left > MapDocument.MaximumRoomDimension || top - bottom > MapDocument.MaximumRoomDimension)
                throw new InvalidOperationException("@roomMergeTooLarge");
            var bounds = new RectInt((int)left, (int)bottom, (int)(right - left), (int)(top - bottom));
            ValidateBounds(bounds.x, bounds.y, bounds.width, bounds.height);
            var ids = new HashSet<string>(sources.Select(room => room.id), StringComparer.Ordinal);
            if (session.Document.rooms.Any(room => !ids.Contains(room.id) && Overlaps(room, bounds)))
                throw new InvalidOperationException("@roomMergeObstacle");
            for (int i = 0; i < sources.Count; i++)
                for (int j = i + 1; j < sources.Count; j++)
                    if (Overlaps(sources[i], new RectInt(sources[j].x, sources[j].y, sources[j].width, sources[j].height)))
                        throw new InvalidOperationException("@roomMergeOverlap");
            foreach (MapStyleground style in session.Document.stylegrounds)
                if (sources.Any(room => MapStylegroundEditing.MatchesRoom(session.Document, style.id, room.name)
                    != MapStylegroundEditing.MatchesRoom(session.Document, style.id, primary.name)))
                    throw new InvalidOperationException("@roomRestructureStyle");

            MapRoom merged = EmptyRoom(primary, bounds, primary.id, primary.name);
            var properties = merged.properties.ToDictionary(p => p.key, p => p.value, StringComparer.Ordinal);
            foreach (MapRoom source in sources)
            {
                foreach (MapProperty property in source.properties)
                {
                    // A merged room has one region color, taken from the active room.
                    if (property.key == "mapMaker.minimapColor" || property.key == "color") continue;
                    if (properties.TryGetValue(property.key, out string existing))
                    {
                        if (existing != property.value) throw new InvalidOperationException("@roomMergeProperties");
                    }
                    else { properties.Add(property.key, property.value); merged.properties.Add(new MapProperty { key = property.key, value = property.value }); }
                }
                long dx = (long)source.x - bounds.x, dy = (long)source.y - bounds.y;
                foreach (MapCell cell in source.foreground) merged.foreground.Add(ShiftCell(cell, dx, dy));
                foreach (MapCell cell in source.background) merged.background.Add(ShiftCell(cell, dx, dy));
                foreach (MapObject item in source.objects) merged.objects.Add(ShiftObject(item, dx, dy));
            }
            session.Execute("Merge rooms", document =>
            {
                int index = document.rooms.FindIndex(room => ids.Contains(room.id));
                document.rooms.RemoveAll(room => ids.Contains(room.id));
                document.rooms.Insert(index, merged);
            });
            ReplaceSelection(new[] { merged.id });
            return merged.id;
        }

        /// <summary>Partitions a room around a local rectangle, keeping all parts non-overlapping.</summary>
        public string SplitArea(string id, RectInt area)
        {
            RequireIdle();
            MapRoom source = RequireRoom(id);
            RequireUnlocked(source);
            ValidateArea(area);
            if (area.width < 1 || area.height < 1 || area.x < 0 || area.y < 0
                || (long)area.x + area.width > source.width || (long)area.y + area.height > source.height)
                throw new InvalidOperationException("@roomSplitNeedArea");
            if (area.width == source.width && area.height == source.height)
                throw new InvalidOperationException("@roomSplitWholeRoom");

            // Try both cuts: prefer vertical side strips, but allow a horizontal cut
            // when it keeps a path or trigger intact in the remaining space.
            List<RectInt> parts = null;
            int[] owners = null;
            foreach (bool vertical in new[] { true, false })
            {
                List<RectInt> candidate = Partition(source, area, vertical);
                var assignments = new int[source.objects.Count];
                bool fits = true;
                for (int i = 0; i < source.objects.Count; i++)
                {
                    Vector2[] points = MapObjectEditing.Corners(source.objects[i]).Concat(source.objects[i].nodes)
                        .Append(new Vector2(source.objects[i].x, source.objects[i].y)).ToArray();
                    assignments[i] = candidate.FindIndex(rect => points.All(point => point.x >= rect.x && point.y >= rect.y
                        && point.x <= (long)rect.x + rect.width && point.y <= (long)rect.y + rect.height));
                    if (assignments[i] < 0) { fits = false; break; }
                }
                if (fits) { parts = candidate; owners = assignments; break; }
            }
            if (parts == null) throw new InvalidOperationException("@roomSplitCrossingObject");
            if (session.Document.rooms.Count - 1 + parts.Count > MapDocument.MaximumRoomCount)
                throw new InvalidOperationException("@roomSplitLimit");

            HashSet<string> usedIds = AllIds();
            var names = new HashSet<string>(session.Document.rooms.Select(room => room.name), StringComparer.OrdinalIgnoreCase);
            int keeper = Enumerable.Range(1, parts.Count - 1).OrderByDescending(i => (long)parts[i].width * parts[i].height).First();
            var rooms = new List<MapRoom>();
            for (int i = 0; i < parts.Count; i++)
            {
                RectInt rect = parts[i];
                string name = source.name;
                if (i != keeper)
                {
                    int suffix = 1;
                    do { name = "room_" + suffix.ToString("00", System.Globalization.CultureInfo.InvariantCulture); suffix++; } while (!names.Add(name));
                }
                var bounds = new RectInt(checked(source.x + rect.x), checked(source.y + rect.y), rect.width, rect.height);
                rooms.Add(EmptyRoom(source, bounds, i == keeper ? source.id : NewId(usedIds), name));
            }
            foreach (MapCell cell in source.foreground) DistributeCell(cell, parts, rooms, true);
            foreach (MapCell cell in source.background) DistributeCell(cell, parts, rooms, false);
            for (int i = 0; i < source.objects.Count; i++)
            {
                int owner = owners[i];
                rooms[owner].objects.Add(ShiftObject(source.objects[i], -parts[owner].x, -parts[owner].y));
            }
            var filters = SplitFilters(source.name, rooms.Where(room => room.id != source.id).Select(room => room.name).ToArray());
            session.Execute("Split room", document =>
            {
                int index = document.rooms.FindIndex(room => room.id == source.id);
                document.rooms.RemoveAt(index);
                // The original room retains its ID, name and list position.
                document.rooms.Insert(index, rooms[keeper]);
                document.rooms.InsertRange(index + 1, rooms.Where((room, i) => i != keeper));
                foreach (var change in filters)
                    document.stylegrounds.Single(style => style.id == change.Key).properties.Single(p => p.key == "roomFilter").value = change.Value;
            });
            ReplaceSelection(new[] { rooms[0].id });
            return rooms[0].id;
        }

        private Dictionary<string, string> SplitFilters(string sourceName, string[] names)
        {
            var result = new Dictionary<string, string>();
            foreach (MapStyleground style in session.Document.stylegrounds)
            {
                MapProperty property = style.properties.Find(p => p.key == "roomFilter");
                if (property == null) continue;
                bool desired = MapStylegroundEditing.MatchesFilter(property.value, sourceName);
                string filter = property.value;
                foreach (string name in names)
                {
                    if (MapStylegroundEditing.MatchesFilter(filter, name) == desired) continue;
                    filter += (filter.Length == 0 ? "" : ",") + (desired ? "" : "!") + name;
                    if (MapStylegroundEditing.MatchesFilter(filter, name) != desired)
                        throw new InvalidOperationException("@roomRestructureStyle");
                }
                if (filter != property.value) result.Add(style.id, filter);
            }
            return result;
        }

        private static List<RectInt> Partition(MapRoom source, RectInt area, bool vertical)
        {
            var result = new List<RectInt> { area };
            int right = area.x + area.width, top = area.y + area.height;
            if (vertical)
            {
                result.Add(new RectInt(0, 0, area.x, source.height));
                result.Add(new RectInt(right, 0, source.width - right, source.height));
                result.Add(new RectInt(area.x, 0, area.width, area.y));
                result.Add(new RectInt(area.x, top, area.width, source.height - top));
            }
            else
            {
                result.Add(new RectInt(0, 0, source.width, area.y));
                result.Add(new RectInt(0, top, source.width, source.height - top));
                result.Add(new RectInt(0, area.y, area.x, area.height));
                result.Add(new RectInt(right, area.y, source.width - right, area.height));
            }
            return result.Where(rect => rect.width > 0 && rect.height > 0).ToList();
        }

        private static void DistributeCell(MapCell cell, List<RectInt> parts, List<MapRoom> rooms, bool foreground)
        {
            int owner = parts.FindIndex(rect => rect.Contains(new Vector2Int(cell.x, cell.y)));
            if (owner < 0) throw new InvalidOperationException("Tile is outside the source room.");
            (foreground ? rooms[owner].foreground : rooms[owner].background).Add(ShiftCell(cell, -parts[owner].x, -parts[owner].y));
        }

        private static MapRoom EmptyRoom(MapRoom source, RectInt bounds, string id, string name) => new MapRoom
        {
            id = id, name = name, x = bounds.x, y = bounds.y, width = bounds.width, height = bounds.height,
            visible = source.visible, locked = source.locked,
            properties = source.properties.Select(p => new MapProperty { key = p.key, value = p.value }).ToList()
        };

        private static MapCell ShiftCell(MapCell cell, long dx, long dy) => new MapCell
        {
            x = checked((int)(cell.x + dx)), y = checked((int)(cell.y + dy)),
            shape = cell.shape, material = cell.material, groupId = cell.groupId
        };

        private static MapObject ShiftObject(MapObject source, long dx, long dy)
        {
            var item = new MapObject
            {
                id = source.id, definition = source.definition, layer = source.layer, groupId = source.groupId,
                x = RebaseCoordinate(source.x, dx), y = RebaseCoordinate(source.y, dy),
                width = source.width, height = source.height, rotation = source.rotation, scaleX = source.scaleX, scaleY = source.scaleY,
                nodes = new List<Vector2>(source.nodes),
                properties = source.properties.Select(p => new MapProperty { key = p.key, value = p.value }).ToList()
            };
            for (int i = 0; i < item.nodes.Count; i++)
                item.nodes[i] = new Vector2(RebaseCoordinate(item.nodes[i].x, dx), RebaseCoordinate(item.nodes[i].y, dy));
            return item;
        }
    }
}
