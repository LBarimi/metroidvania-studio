using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    /// <summary>
    /// A reversible edit journal used only by gesture paths that can describe
    /// their mutations without cloning the complete map document.
    /// </summary>
    internal interface IMapIncrementalEdit
    {
        bool HasChanges { get; }
        long ByteCount { get; }
        void Complete(MapDocument document);
        void ApplyBefore(MapDocument document);
        void ApplyAfter(MapDocument document);
    }

    internal static class MapIncrementalClone
    {
        public static MapCell Cell(MapCell value) => value == null ? null : new MapCell
        {
            x = value.x,
            y = value.y,
            shape = value.shape,
            material = value.material,
            groupId = value.groupId
        };

        public static MapObject Object(MapObject value) => value == null ? null : new MapObject
        {
            id = value.id,
            definition = value.definition,
            layer = value.layer,
            groupId = value.groupId,
            x = value.x,
            y = value.y,
            width = value.width,
            height = value.height,
            rotation = value.rotation,
            scaleX = value.scaleX,
            scaleY = value.scaleY,
            nodes = value.nodes == null ? null : new List<Vector2>(value.nodes),
            properties = Properties(value.properties)
        };

        public static List<MapProperty> Properties(List<MapProperty> values) => values == null ? null
            : values.Select(value => value == null ? null : new MapProperty { key = value.key, value = value.value }).ToList();

        public static bool Same(MapCell a, MapCell b) => ReferenceEquals(a, b) || a != null && b != null
            && a.x == b.x && a.y == b.y && a.shape == b.shape && a.material == b.material && a.groupId == b.groupId;

        public static bool Same(MapObject a, MapObject b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.id != b.id || a.definition != b.definition || a.layer != b.layer
                || a.groupId != b.groupId || a.x != b.x || a.y != b.y || a.width != b.width || a.height != b.height
                || a.rotation != b.rotation || a.scaleX != b.scaleX || a.scaleY != b.scaleY
                || (a.nodes?.Count ?? -1) != (b.nodes?.Count ?? -1) || !Same(a.properties, b.properties)) return false;
            if (a.nodes != null) for (int index = 0; index < a.nodes.Count; index++) if (a.nodes[index] != b.nodes[index]) return false;
            return true;
        }

        public static bool Same(List<MapProperty> a, List<MapProperty> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int index = 0; index < a.Count; index++)
            {
                MapProperty left = a[index], right = b[index];
                if (ReferenceEquals(left, right)) continue;
                if (left == null || right == null || left.key != right.key || left.value != right.value) return false;
            }
            return true;
        }

        public static long Bytes(MapCell value) => value == null ? 4 : 48 + Text(value.material) + Text(value.groupId);
        public static long Bytes(MapObject value)
        {
            if (value == null) return 4;
            long result = 112 + Text(value.id) + Text(value.definition) + Text(value.groupId) + 16L * (value.nodes?.Count ?? 0);
            return result + Bytes(value.properties);
        }
        public static long Bytes(List<MapProperty> values)
        {
            if (values == null) return 4;
            long result = 2;
            foreach (MapProperty value in values) result += value == null ? 4 : 16 + Text(value.key) + Text(value.value);
            return result;
        }
        private static int Text(string value) => value == null ? 4 : Encoding.UTF8.GetByteCount(value);
    }

    internal sealed class MapTileIncrementalEdit : IMapIncrementalEdit
    {
        private enum Kind { Add, Replace, RemoveSwap }
        private sealed class Operation
        {
            public Kind kind;
            public int index;
            public MapCell before;
            public MapCell after;
            public MapCell tail;
        }

        private readonly string roomId;
        private readonly MapLayer layer;
        private readonly List<Operation> operations = new List<Operation>();
        private readonly List<MapProperty> beforeProperties;
        private List<MapProperty> afterProperties;
        private bool completed;

        public bool HasChanges => completed && (operations.Count > 0 || !MapIncrementalClone.Same(beforeProperties, afterProperties));
        public long ByteCount { get; private set; }

        public MapTileIncrementalEdit(MapRoom room, MapLayer layer)
        {
            if (room == null) throw new ArgumentNullException(nameof(room));
            if (layer != MapLayer.ForegroundTiles && layer != MapLayer.BackgroundTiles)
                throw new ArgumentException("A tile delta requires a tile layer.", nameof(layer));
            roomId = room.id;
            this.layer = layer;
            beforeProperties = MapIncrementalClone.Properties(room.properties);
        }

        public void Add(int index, MapCell value)
        {
            operations.Add(new Operation { kind = Kind.Add, index = index, after = MapIncrementalClone.Cell(value) });
        }

        public void Replace(int index, MapCell before, MapCell after)
        {
            if (MapIncrementalClone.Same(before, after)) return;
            operations.Add(new Operation { kind = Kind.Replace, index = index,
                before = MapIncrementalClone.Cell(before), after = MapIncrementalClone.Cell(after) });
        }

        public void RemoveSwap(int index, MapCell removed, MapCell tail)
        {
            operations.Add(new Operation { kind = Kind.RemoveSwap, index = index,
                before = MapIncrementalClone.Cell(removed), tail = MapIncrementalClone.Cell(tail) });
        }

        public void Complete(MapDocument document)
        {
            if (completed) throw new InvalidOperationException("The tile delta is already complete.");
            MapRoom room = RequireRoom(document);
            afterProperties = MapIncrementalClone.Properties(room.properties);
            long bytes = MapIncrementalClone.Bytes(beforeProperties) + MapIncrementalClone.Bytes(afterProperties);
            foreach (Operation operation in operations)
                bytes += 32 + MapIncrementalClone.Bytes(operation.before) + MapIncrementalClone.Bytes(operation.after)
                    + MapIncrementalClone.Bytes(operation.tail);
            ByteCount = Math.Max(1, bytes);
            completed = true;
        }

        public void ApplyBefore(MapDocument document)
        {
            MapRoom room = RequireRoom(document); List<MapCell> cells = Cells(room);
            for (int index = operations.Count - 1; index >= 0; index--) Undo(cells, operations[index]);
            // Cancel may run while Complete is unwinding. Always restore the
            // captured value rather than relying on an after snapshot existing.
            room.properties = MapIncrementalClone.Properties(beforeProperties);
        }

        public void ApplyAfter(MapDocument document)
        {
            RequireComplete();
            MapRoom room = RequireRoom(document); List<MapCell> cells = Cells(room);
            foreach (Operation operation in operations) Redo(cells, operation);
            if (!MapIncrementalClone.Same(beforeProperties, afterProperties)) room.properties = MapIncrementalClone.Properties(afterProperties);
        }

        private static void Undo(List<MapCell> cells, Operation operation)
        {
            switch (operation.kind)
            {
                case Kind.Add:
                    RequireIndex(cells, operation.index, false);
                    cells.RemoveAt(operation.index);
                    break;
                case Kind.Replace:
                    RequireIndex(cells, operation.index, true);
                    cells[operation.index] = MapIncrementalClone.Cell(operation.before);
                    break;
                case Kind.RemoveSwap:
                    if (operation.index < 0 || operation.index > cells.Count) throw new InvalidOperationException("Tile history no longer matches its layer.");
                    if (operation.index == cells.Count) cells.Add(MapIncrementalClone.Cell(operation.before));
                    else
                    {
                        cells.Add(MapIncrementalClone.Cell(operation.tail));
                        cells[operation.index] = MapIncrementalClone.Cell(operation.before);
                    }
                    break;
            }
        }

        private static void Redo(List<MapCell> cells, Operation operation)
        {
            switch (operation.kind)
            {
                case Kind.Add:
                    if (operation.index != cells.Count) throw new InvalidOperationException("Tile history no longer matches its layer.");
                    cells.Add(MapIncrementalClone.Cell(operation.after));
                    break;
                case Kind.Replace:
                    RequireIndex(cells, operation.index, true);
                    cells[operation.index] = MapIncrementalClone.Cell(operation.after);
                    break;
                case Kind.RemoveSwap:
                    RequireIndex(cells, operation.index, true);
                    int tail = cells.Count - 1;
                    if (operation.index != tail) cells[operation.index] = MapIncrementalClone.Cell(operation.tail);
                    cells.RemoveAt(tail);
                    break;
            }
        }

        private MapRoom RequireRoom(MapDocument document) => document?.rooms?.Find(room => room.id == roomId)
            ?? throw new InvalidOperationException("The tile history room no longer exists.");
        private List<MapCell> Cells(MapRoom room) => layer == MapLayer.ForegroundTiles ? room.foreground : room.background;
        private void RequireComplete() { if (!completed) throw new InvalidOperationException("Complete the tile delta before applying it."); }
        private static void RequireIndex<T>(List<T> values, int index, bool allowAny)
        {
            if (index < 0 || index >= values.Count || !allowAny && index != values.Count - 1)
                throw new InvalidOperationException("Tile history no longer matches its layer.");
        }
    }

    internal sealed class MapObjectIncrementalEdit : IMapIncrementalEdit
    {
        private enum Mode { None, Replace, Add, Remove }
        private sealed class Replacement { public int index; public MapObject before; public MapObject after; }
        private sealed class Removal { public int index; public MapObject value; }

        private readonly string roomId;
        private readonly Dictionary<string, Replacement> replacements = new Dictionary<string, Replacement>(StringComparer.Ordinal);
        private readonly List<Removal> additions = new List<Removal>();
        private readonly List<Removal> removals = new List<Removal>();
        private readonly HashSet<int> removedOriginalIndices = new HashSet<int>();
        private MapRoom sourceRoom;
        private Dictionary<string, int> originalIndices;
        private bool removalsOrdered = true;
        private Mode mode;
        private bool completed;

        public bool HasChanges => completed && (replacements.Values.Any(value => !MapIncrementalClone.Same(value.before, value.after))
            || additions.Count > 0 || removals.Count > 0);
        public long ByteCount { get; private set; }

        public MapObjectIncrementalEdit(MapRoom room)
        {
            if (room == null) throw new ArgumentNullException(nameof(room));
            roomId = room.id;
            sourceRoom = room;
        }

        public void Replace(int index, MapObject before, MapObject after)
        {
            SelectMode(Mode.Replace);
            if (!replacements.TryGetValue(before.id, out Replacement value))
            {
                value = new Replacement { index = index, before = MapIncrementalClone.Object(before) };
                replacements.Add(before.id, value);
            }
            if (value.index != index || after.id != before.id) throw new InvalidOperationException("Object replacement changed identity or order.");
            value.after = MapIncrementalClone.Object(after);
        }

        public void Add(int index, MapObject value)
        {
            SelectMode(Mode.Add);
            additions.Add(new Removal { index = index, value = MapIncrementalClone.Object(value) });
        }

        public void Remove(IReadOnlyList<MapObject> objects, IReadOnlyList<int> indices)
        {
            SelectMode(Mode.Remove);
            if (objects == null || indices == null || objects.Count != indices.Count) throw new ArgumentException("Object removals and indices must match.");
            if (objects.Count == 0) return;
            // Every Drag call supplies indices from the current survivor list. Map
            // IDs back to their stroke-start indices so Undo and Redo need one pass.
            EnsureOriginalIndices();
            var batch = new List<Removal>(objects.Count);
            var batchIndices = new HashSet<int>();
            for (int index = 0; index < objects.Count; index++)
            {
                MapObject value = objects[index];
                int currentIndex = indices[index];
                if (value == null || currentIndex < 0 || currentIndex >= sourceRoom.objects.Count
                    || sourceRoom.objects[currentIndex]?.id != value.id
                    || !originalIndices.TryGetValue(value.id, out int originalIndex)
                    || removedOriginalIndices.Contains(originalIndex) || !batchIndices.Add(originalIndex))
                    throw new InvalidOperationException("Object removals no longer match their room.");
                batch.Add(new Removal { index = originalIndex, value = MapIncrementalClone.Object(value) });
            }
            if (batch.Count == 0) return;
            foreach (Removal removal in batch)
            {
                removals.Add(removal);
                removedOriginalIndices.Add(removal.index);
            }
            removalsOrdered = false;
        }

        public void Complete(MapDocument document)
        {
            if (completed) throw new InvalidOperationException("The object delta is already complete.");
            RequireRoom(document);
            OrderRemovals();
            long bytes = 1;
            foreach (Replacement value in replacements.Values) bytes += 32 + MapIncrementalClone.Bytes(value.before) + MapIncrementalClone.Bytes(value.after);
            foreach (Removal value in additions) bytes += 24 + MapIncrementalClone.Bytes(value.value);
            foreach (Removal value in removals) bytes += 24 + MapIncrementalClone.Bytes(value.value);
            ByteCount = Math.Max(1, bytes);
            sourceRoom = null;
            originalIndices = null;
            removedOriginalIndices.Clear();
            completed = true;
        }

        public void ApplyBefore(MapDocument document)
        {
            List<MapObject> objects = RequireRoom(document).objects;
            if (mode == Mode.Replace) foreach (Replacement value in replacements.Values) ReplaceAt(objects, value.index, value.before);
            else if (mode == Mode.Add) RemoveAdditions(objects);
            else if (mode == Mode.Remove) { OrderRemovals(); RestoreRemovals(objects); }
        }

        public void ApplyAfter(MapDocument document)
        {
            RequireComplete(); List<MapObject> objects = RequireRoom(document).objects;
            if (mode == Mode.Replace) foreach (Replacement value in replacements.Values) ReplaceAt(objects, value.index, value.after);
            else if (mode == Mode.Add) InsertAdditions(objects);
            else if (mode == Mode.Remove) ApplyRemovals(objects);
        }

        private void InsertAdditions(List<MapObject> objects)
        {
            if (additions.Count == 0) return;
            var ids = new HashSet<string>(objects.Select(item => item.id), StringComparer.Ordinal);
            var copies = new List<MapObject>(additions.Count);
            int initialCount = objects.Count;
            bool contiguous = true;
            int insertionIndex = additions[0].index;
            for (int index = 0; index < additions.Count; index++)
            {
                Removal addition = additions[index];
                if (addition.index < 0 || addition.index > initialCount + index
                    || !ids.Add(addition.value.id))
                    throw new InvalidOperationException("Object history no longer matches its room.");
                contiguous &= addition.index == insertionIndex + index;
                copies.Add(MapIncrementalClone.Object(addition.value));
            }
            if (contiguous) objects.InsertRange(insertionIndex, copies);
            else
                for (int index = 0; index < additions.Count; index++)
                    objects.Insert(additions[index].index, copies[index]);
        }

        private void RemoveAdditions(List<MapObject> objects)
        {
            if (additions.Count == 0) return;
            int insertionIndex = additions[0].index;
            bool contiguous = true;
            for (int index = 0; index < additions.Count; index++)
                contiguous &= additions[index].index == insertionIndex + index;
            if (contiguous)
            {
                if (insertionIndex < 0 || insertionIndex + additions.Count > objects.Count)
                    throw new InvalidOperationException("Object history no longer matches its room.");
                for (int index = 0; index < additions.Count; index++)
                    if (objects[insertionIndex + index].id != additions[index].value.id)
                        throw new InvalidOperationException("Object history no longer matches its room.");
                objects.RemoveRange(insertionIndex, additions.Count);
                return;
            }
            for (int index = additions.Count - 1; index >= 0; index--)
                RemoveAt(objects, additions[index].index, additions[index].value.id);
        }

        private void RestoreRemovals(List<MapObject> objects)
        {
            if (removals.Count == 0) return;
            int restoredCount = checked(objects.Count + removals.Count);
            var ids = new HashSet<string>(objects.Select(item => item.id), StringComparer.Ordinal);
            var restored = new List<MapObject>(restoredCount);
            var copies = new MapObject[removals.Count];
            int previousIndex = -1;
            for (int index = 0; index < removals.Count; index++)
            {
                Removal removal = removals[index];
                if (removal.index <= previousIndex || removal.index > objects.Count + index
                    || removal.value == null || !ids.Add(removal.value.id))
                    throw new InvalidOperationException("Object history no longer matches its room.");
                previousIndex = removal.index;
                copies[index] = MapIncrementalClone.Object(removal.value);
            }

            int current = 0, removalIndex = 0;
            for (int destination = 0; destination < restoredCount; destination++)
            {
                if (removalIndex < removals.Count && removals[removalIndex].index == destination)
                    restored.Add(copies[removalIndex++]);
                else
                {
                    if (current >= objects.Count)
                        throw new InvalidOperationException("Object history no longer matches its room.");
                    restored.Add(objects[current++]);
                }
            }
            if (current != objects.Count || removalIndex != removals.Count)
                throw new InvalidOperationException("Object history no longer matches its room.");
            objects.Clear();
            objects.AddRange(restored);
        }

        private void ApplyRemovals(List<MapObject> objects)
        {
            if (removals.Count == 0) return;
            int previousIndex = -1;
            for (int index = 0; index < removals.Count; index++)
            {
                Removal removal = removals[index];
                if (removal.index <= previousIndex || removal.index < 0 || removal.index >= objects.Count
                    || removal.value == null || objects[removal.index].id != removal.value.id)
                    throw new InvalidOperationException("Object history no longer matches its room.");
                previousIndex = removal.index;
            }

            var survivors = new List<MapObject>(objects.Count - removals.Count);
            int removalIndex = 0;
            for (int index = 0; index < objects.Count; index++)
            {
                if (removalIndex < removals.Count && removals[removalIndex].index == index)
                    removalIndex++;
                else survivors.Add(objects[index]);
            }
            if (removalIndex != removals.Count)
                throw new InvalidOperationException("Object history no longer matches its room.");
            objects.Clear();
            objects.AddRange(survivors);
        }

        private void EnsureOriginalIndices()
        {
            if (originalIndices != null) return;
            if (sourceRoom == null) throw new InvalidOperationException("Object removals no longer match their room.");
            originalIndices = new Dictionary<string, int>(sourceRoom.objects.Count, StringComparer.Ordinal);
            for (int index = 0; index < sourceRoom.objects.Count; index++)
            {
                MapObject value = sourceRoom.objects[index];
                if (value?.id == null || !originalIndices.TryAdd(value.id, index))
                    throw new InvalidOperationException("Object removals require unique object IDs.");
            }
        }

        private void OrderRemovals()
        {
            if (removalsOrdered) return;
            removals.Sort((left, right) => left.index.CompareTo(right.index));
            removalsOrdered = true;
        }

        private void SelectMode(Mode next)
        {
            if (completed) throw new InvalidOperationException("The object delta is already complete.");
            if (mode == Mode.None) mode = next;
            else if (mode != next) throw new InvalidOperationException("One object gesture cannot mix structural and transform edits.");
        }
        private MapRoom RequireRoom(MapDocument document) => document?.rooms?.Find(room => room.id == roomId)
            ?? throw new InvalidOperationException("The object history room no longer exists.");
        private void RequireComplete() { if (!completed) throw new InvalidOperationException("Complete the object delta before applying it."); }
        private static void ReplaceAt(List<MapObject> values, int index, MapObject value)
        {
            if (index < 0 || index >= values.Count || values[index].id != value.id) throw new InvalidOperationException("Object history no longer matches its room.");
            values[index] = MapIncrementalClone.Object(value);
        }
        private static void RemoveAt(List<MapObject> values, int index, string id)
        {
            if (index < 0 || index >= values.Count || values[index].id != id) throw new InvalidOperationException("Object history no longer matches its room.");
            values.RemoveAt(index);
        }
    }
}
