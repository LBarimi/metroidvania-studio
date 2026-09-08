using System;
using System.Collections.Generic;
using System.Linq;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public sealed partial class MapCanvasController
    {
        private static readonly MapLayer[] TileLayers = { MapLayer.ForegroundTiles, MapLayer.BackgroundTiles };
        public const int MaximumPasteMemberCount = 65_536;
        public const int MaximumPasteWork = 262_144;

        /// <summary>One mixed tile/object journal keeps paste Undo proportional to the pasted members.</summary>
        private sealed class MapPasteIncrementalEdit : IMapIncrementalEdit
        {
            private readonly MapTileIncrementalEdit foreground;
            private readonly MapTileIncrementalEdit background;
            private readonly MapObjectIncrementalEdit objects;
            private bool completed;

            public bool HasChanges => completed && (foreground.HasChanges || background.HasChanges || objects.HasChanges);
            public long ByteCount { get; private set; }

            public MapPasteIncrementalEdit(MapRoom room)
            {
                foreground = new MapTileIncrementalEdit(room, MapLayer.ForegroundTiles);
                background = new MapTileIncrementalEdit(room, MapLayer.BackgroundTiles);
                objects = new MapObjectIncrementalEdit(room);
            }

            public void Add(MapLayer layer, int index, MapCell value) => Tiles(layer).Add(index, value);
            public void Replace(MapLayer layer, int index, MapCell before, MapCell after) => Tiles(layer).Replace(index, before, after);
            public void AddObject(int index, MapObject value) => objects.Add(index, value);

            public void Complete(MapDocument document)
            {
                if (completed) throw new InvalidOperationException("The paste delta is already complete.");
                foreground.Complete(document);
                background.Complete(document);
                objects.Complete(document);
                ByteCount = foreground.ByteCount + background.ByteCount + objects.ByteCount;
                completed = true;
            }

            public void ApplyBefore(MapDocument document)
            {
                objects.ApplyBefore(document);
                background.ApplyBefore(document);
                foreground.ApplyBefore(document);
            }

            public void ApplyAfter(MapDocument document)
            {
                if (!completed) throw new InvalidOperationException("Complete the paste delta before applying it.");
                foreground.ApplyAfter(document);
                background.ApplyAfter(document);
                objects.ApplyAfter(document);
            }

            private MapTileIncrementalEdit Tiles(MapLayer layer)
            {
                if (layer == MapLayer.ForegroundTiles) return foreground;
                if (layer == MapLayer.BackgroundTiles) return background;
                throw new ArgumentException("Paste cells require a tile layer.", nameof(layer));
            }
        }

        private MapLayerGroupAccess GroupAccess(string memberGroupId)
        {
            if (!ReferenceEquals(layerGroupIndexDocument, Session.Document) || layerGroupIndex == null)
            {
                layerGroupIndexDocument = Session.Document;
                layerGroupIndex = new MapLayerGroupIndex(Session.Document);
                layerGroupAccess.Clear();
            }
            var key = (memberGroupId, ActiveGroupId);
            if (!layerGroupAccess.TryGetValue(key, out MapLayerGroupAccess access))
            {
                access = layerGroupIndex.Access(memberGroupId, ActiveGroupId);
                layerGroupAccess.Add(key, access);
            }
            return access;
        }

        public bool IsMemberVisible(MapLayer layer, string groupId) => !HiddenLayers.Contains(layer) && GroupAccess(groupId).Visible;
        public Func<MapObject, bool> ObjectVisibilityFilter { get; set; }
        public bool IsObjectVisible(MapObject item) => item != null && IsMemberVisible(item.layer, item.groupId) && (ObjectVisibilityFilter?.Invoke(item) ?? true);
        public bool CanEditObject(MapObject item) => item != null && CanEditMember(item.layer, item.groupId) && (ObjectVisibilityFilter?.Invoke(item) ?? true);
        public bool CanEditMember(MapLayer layer, string groupId)
        {
            if ((Layer != MapLayer.All && layer != Layer) || HiddenLayers.Contains(layer) || LockedLayers.Contains(layer)) return false;
            MapLayerGroupAccess access = GroupAccess(groupId);
            return access.Visible && !access.Locked && access.Matches;
        }
        private bool CanModifySelection() => Selection.HasValue && Room != null && Room.visible && !Room.locked;
        private bool Selected(MapCell c, MapLayer layer, RectInt area) => CanEditMember(layer, c.groupId) && area.Contains(new Vector2Int(c.x, c.y));
        private bool Selected(MapObject o, RectInt area) => CanEditObject(o)
            && ObjectEditor.OverlapsSelectionBody(o, new Rect(area.x, area.y, area.width, area.height));
        private static MapCell CopyCell(MapCell c, int x, int y) => new MapCell { x = x, y = y, material = c.material, shape = c.shape, groupId = c.groupId };
        private static MapObject CopyObject(MapObject source) => MapIncrementalClone.Object(source);
        private static void OffsetObject(MapObject item, Vector2 offset)
        {
            item.x += offset.x; item.y += offset.y;
            for (int i = 0; i < item.nodes.Count; i++) item.nodes[i] += offset;
        }

        public void CopySelection()
        {
            MapSelectionClipboard captured = CaptureSelection();
            if (captured != null) selectionClipboard = captured;
        }

        /// <summary>Captures editable members without changing either the map or the current clipboard.</summary>
        public MapSelectionClipboard CaptureSelection()
        {
            RequireClipboardIdle();
            if (Room == null || !Room.visible || Room.locked) return null;
            bool objectsOnly = UsesObjectSelection;
            bool nodesOnly = false;
            var fragment = new MapDocument { name = "Metroidvania Studio selection" };
            var room = new MapRoom { name = "Selection", width = 1, height = 1 };
            fragment.rooms.Add(room);
            Vector2 origin, size;
            if (objectsOnly)
            {
                var bodyIds = new HashSet<string>(ObjectEditor.SelectedBodyIds);
                var nodes = ObjectEditor.SelectedNodes.Where(node => !bodyIds.Contains(node.ObjectId)).ToArray();
                if (bodyIds.Count > 0 && nodes.Length > 0)
                    throw new InvalidOperationException("Copy object bodies and nodes from other objects separately.");
                if (nodes.Select(node => node.ObjectId).Distinct().Count() > 1)
                    throw new InvalidOperationException("Copy nodes from one object at a time.");
                var objects = ObjectEditor.SelectedObjects.Where(item => bodyIds.Contains(item.id)).ToArray();
                if (nodes.Length > 0)
                {
                    nodesOnly = true;
                    var owner = Room.objects.Find(item => item.id == nodes[0].ObjectId);
                    var points = nodes.OrderBy(node => node.NodeIndex).Select(node => owner.nodes[node.NodeIndex]).ToArray();
                    origin = new Vector2(points.Min(point => point.x), points.Min(point => point.y));
                    size = new Vector2(points.Max(point => point.x), points.Max(point => point.y)) - origin;
                    room.objects.Add(new MapObject { definition = owner.definition, layer = owner.layer, groupId = owner.groupId,
                        nodes = points.Select(point => point - origin).ToList() });
                }
                else
                {
                    if (objects.Length == 0) return null;
                    Rect bounds = MapObjectEditing.Bounds(objects[0]);
                    foreach (var item in objects.Skip(1))
                    {
                        Rect next = MapObjectEditing.Bounds(item);
                        bounds = Rect.MinMaxRect(Math.Min(bounds.xMin, next.xMin), Math.Min(bounds.yMin, next.yMin),
                            Math.Max(bounds.xMax, next.xMax), Math.Max(bounds.yMax, next.yMax));
                    }
                    origin = bounds.min; size = Vector2.Max(bounds.size, Vector2.one / 16f);
                    foreach (var item in objects) { var copy = CopyObject(item); OffsetObject(copy, -origin); room.objects.Add(copy); }
                }
            }
            else
            {
                if (!Selection.HasValue) return null;
                RectInt area = Selection.Value; origin = area.position; size = area.size;
                room.width = area.width; room.height = area.height;
                foreach (var layer in TileLayers)
                    foreach (var cell in Cells(Room, layer))
                        if (Selected(cell, layer, area)) Cells(room, layer).Add(CopyCell(cell, cell.x - area.x, cell.y - area.y));
                foreach (var item in Room.objects)
                    if (Selected(item, area)) { var copy = CopyObject(item); OffsetObject(copy, -origin); room.objects.Add(copy); }
            }
            if (room.foreground.Count + room.background.Count + room.objects.Count == 0) return null;
            var groupIds = new HashSet<string>(room.foreground.Concat(room.background).Select(cell => cell.groupId)
                .Concat(room.objects.Select(item => item.groupId)), StringComparer.Ordinal);
            var groupsById = new Dictionary<string, MapLayerGroup>(Session.Document.layerGroups.Count, StringComparer.Ordinal);
            foreach (MapLayerGroup group in Session.Document.layerGroups)
                if (group?.id != null) groupsById.TryAdd(group.id, group);
            foreach (string id in groupIds.ToArray())
            {
                string parent = id;
                while (!string.IsNullOrEmpty(parent))
                {
                    if (!groupsById.TryGetValue(parent, out MapLayerGroup group)) break;
                    groupIds.Add(group.id); parent = group.parentId;
                }
            }
            foreach (var group in Session.Document.layerGroups)
                if (groupIds.Contains(group.id)) fragment.layerGroups.Add(MapJson.FromJson<MapLayerGroup>(MapJson.ToJson(group)));
            return new MapSelectionClipboard(fragment, Layer, objectsOnly, size, nodesOnly);
        }

        public void SetClipboard(MapSelectionClipboard value)
        {
            RequireClipboardIdle();
            if (value == null) throw new ArgumentNullException(nameof(value));
            selectionClipboard = new MapSelectionClipboard(value.ReadOnlyFragment, value.SourceLayer, value.ObjectsOnly, value.Size, value.NodesOnly);
        }

        public void PasteCentered(Vector2 center)
        {
            if (selectionClipboard != null) PasteClipboardAt(center - selectionClipboard.Size * .5f);
        }

        public void PasteSelection(MapSelectionClipboard selection, Vector2 at, bool centered = false)
        {
            MapSelectionClipboard previous = selectionClipboard;
            SetClipboard(selection);
            try { if (centered) PasteCentered(at); else PasteAt(at); }
            catch { selectionClipboard = previous; throw; }
        }

        public void DeleteSelection()
        {
            if (UsesObjectSelection)
            { ObjectEditor.Delete(); return; }
            if (!CanModifySelection()) return;
            var area = Selection.Value;
            Session.Execute("Delete selection", document =>
            {
                var room = FindRoom(document);
                foreach (var layer in TileLayers) Cells(room, layer).RemoveAll(c => Selected(c, layer, area));
                room.objects.RemoveAll(o => Selected(o, area));
            });
        }

        public void CutSelection()
        {
            MapSelectionClipboard captured = CaptureSelection();
            if (captured == null) return;
            selectionClipboard = captured; DeleteSelection();
        }

        public void Paste(Vector2Int at)
        {
            PasteClipboardAt(at);
        }

        private void PasteClipboardAt(Vector2 position)
        {
            RequireClipboardIdle();
            MapRoom targetRoom = Room;
            if (selectionClipboard == null || targetRoom == null || !targetRoom.visible || targetRoom.locked) return;
            if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsInfinity(position.x) || float.IsInfinity(position.y)
                || Math.Abs(position.x) > 1000000 || Math.Abs(position.y) > 1000000)
                throw new ArgumentOutOfRangeException(nameof(position), "Paste coordinates exceed the supported range.");
            MapSelectionClipboard source = selectionClipboard;
            Vector2 at = source.ObjectsOnly ? new Vector2(MathEx.Round(position.x * 16) / 16, MathEx.Round(position.y * 16) / 16)
                : (Vector2)Vector2Int.FloorToInt(position);
            MapDocument fragment = source.ReadOnlyFragment;
            MapRoom sourceRoom = fragment.rooms[0];
            long memberCount = (long)sourceRoom.foreground.Count + sourceRoom.background.Count + sourceRoom.objects.Count;
            if (memberCount > MaximumPasteMemberCount)
                throw new InvalidOperationException($"One paste cannot contain more than {MaximumPasteMemberCount} tiles and objects.");
            long work = memberCount + fragment.layerGroups.Count;
            foreach (MapObject item in sourceRoom.objects)
                work += item.nodes.Count + item.properties.Count;
            if (work > MaximumPasteWork)
                throw new InvalidOperationException($"One paste cannot require more than {MaximumPasteWork} work units.");
            if (source.NodesOnly)
            {
                var carrier = sourceRoom.objects[0];
                var fragmentGroups = new MapLayerGroupIndex(fragment);
                MapLayerGroupAccess access = fragmentGroups.Access(carrier.groupId, "");
                if (access.Visible && !access.Locked)
                    ObjectEditor.PasteNodes(carrier.nodes, at);
                return;
            }

            MapDocument targetDocument = Session.Document;
            work += targetDocument.layerGroups.Count;
            if (work > MaximumPasteWork)
                throw new InvalidOperationException($"One paste cannot require more than {MaximumPasteWork} work units.");
            var fragmentGroupsIndex = new MapLayerGroupIndex(fragment);
            var fragmentAccess = new Dictionary<string, MapLayerGroupAccess>(StringComparer.Ordinal);
            var targetGroupsIndex = new MapLayerGroupIndex(targetDocument);
            var targetAccess = new Dictionary<string, MapLayerGroupAccess>(StringComparer.Ordinal);
            var targetGroups = targetDocument.layerGroups.ToDictionary(group => group.id, group => group, StringComparer.Ordinal);
            bool SourceEditable(string groupId)
            {
                if (!fragmentAccess.TryGetValue(groupId, out MapLayerGroupAccess access))
                {
                    access = fragmentGroupsIndex.Access(groupId, "");
                    fragmentAccess.Add(groupId, access);
                }
                return access.Visible && !access.Locked;
            }
            bool TargetEditable(MapLayer layer, string groupId)
            {
                if ((Layer != MapLayer.All && layer != Layer) || HiddenLayers.Contains(layer) || LockedLayers.Contains(layer)) return false;
                if (!targetAccess.TryGetValue(groupId, out MapLayerGroupAccess access))
                {
                    access = targetGroupsIndex.Access(groupId, ActiveGroupId);
                    targetAccess.Add(groupId, access);
                }
                return access.Visible && !access.Locked && access.Matches;
            }

            MapLayer TargetLayer(MapLayer sourceLayer) => Layer == MapLayer.All || source.SourceLayer == MapLayer.All ? sourceLayer : Layer;
            bool CouldEditLayer(MapLayer layer) => (Layer == MapLayer.All || layer == Layer)
                && !HiddenLayers.Contains(layer) && !LockedLayers.Contains(layer);
            var neededTileLayers = new HashSet<MapLayer>();
            if (sourceRoom.foreground.Count > 0)
            {
                MapLayer target = TargetLayer(MapLayer.ForegroundTiles);
                if ((target == MapLayer.ForegroundTiles || target == MapLayer.BackgroundTiles) && CouldEditLayer(target)) neededTileLayers.Add(target);
            }
            if (sourceRoom.background.Count > 0)
            {
                MapLayer target = TargetLayer(MapLayer.BackgroundTiles);
                if ((target == MapLayer.ForegroundTiles || target == MapLayer.BackgroundTiles) && CouldEditLayer(target)) neededTileLayers.Add(target);
            }
            bool needsForeground = neededTileLayers.Contains(MapLayer.ForegroundTiles);
            bool needsBackground = neededTileLayers.Contains(MapLayer.BackgroundTiles);
            if (needsForeground) work += targetRoom.foreground.Count;
            if (needsBackground) work += targetRoom.background.Count;
            bool hasPotentialObjects = sourceRoom.objects.Any(item =>
            {
                MapLayer target = TargetLayer(item.layer);
                return target >= MapLayer.Entities && target <= MapLayer.BackgroundDecals && CouldEditLayer(target);
            });
            if (hasPotentialObjects)
                work += targetDocument.rooms.Count + targetDocument.layerGroups.Count + targetDocument.stylegrounds.Count
                    + targetDocument.rooms.Sum(room => (long)room.objects.Count);
            if (work > MaximumPasteWork)
                throw new InvalidOperationException($"One paste cannot require more than {MaximumPasteWork} work units.");

            var foregroundOccupancy = needsForeground ? BuildCellOccupancy(targetRoom.foreground) : null;
            var backgroundOccupancy = needsBackground ? BuildCellOccupancy(targetRoom.background) : null;
            var tiles = new List<(MapLayer layer, MapCell cell)>(checked((int)Math.Min(memberCount, int.MaxValue)));
            var objects = new List<MapObject>(sourceRoom.objects.Count);
            HashSet<string> ids = hasPotentialObjects
                ? new HashSet<string>(targetDocument.rooms.Select(room => room.id).Concat(targetDocument.layerGroups.Select(group => group.id))
                    .Concat(targetDocument.stylegrounds.Select(style => style.id)).Concat(targetDocument.rooms.SelectMany(room => room.objects).Select(item => item.id)))
                : null;
            foreach (var sourceLayer in TileLayers)
                foreach (var cell in Cells(sourceRoom, sourceLayer))
                {
                    if (!SourceEditable(cell.groupId)) continue;
                    var layer = TargetLayer(sourceLayer);
                    if (layer != MapLayer.ForegroundTiles && layer != MapLayer.BackgroundTiles) continue;
                    string group = PasteGroup(cell.groupId, layer, targetGroups);
                    if (!TargetEditable(layer, group)) continue;
                    int x = (int)at.x + cell.x, y = (int)at.y + cell.y;
                    if (!targetRoom.Contains(x, y)) continue;
                    List<MapCell> targetCells = Cells(targetRoom, layer);
                    Dictionary<int, int> occupancy = layer == MapLayer.ForegroundTiles ? foregroundOccupancy : backgroundOccupancy;
                    if (occupancy.TryGetValue(CellKey(x, y), out int targetIndex)
                        && !TargetEditable(layer, targetCells[targetIndex].groupId)) continue;
                    var copy = CopyCell(cell, x, y); copy.groupId = group; tiles.Add((layer, copy));
                }
            foreach (var item in sourceRoom.objects)
            {
                if (!SourceEditable(item.groupId)) continue;
                var layer = TargetLayer(item.layer);
                if (layer < MapLayer.Entities || layer > MapLayer.BackgroundDecals) continue;
                string group = PasteGroup(item.groupId, layer, targetGroups);
                if (!TargetEditable(layer, group)) continue;
                MapObjectDefinition definition = ResolveObjectDefinition?.Invoke(item.definition);
                if (definition != null && !definition.SupportsLayer(layer)) continue;
                var copy = CopyObject(item);
                copy.id = MapObjectIds.Create(ids);
                copy.layer = layer; copy.groupId = group; OffsetObject(copy, at);
                if (!(ObjectVisibilityFilter?.Invoke(copy) ?? true)) continue;
                MapObjectEditing.Corners(copy); definition?.ValidateObject(copy); objects.Add(copy);
            }
            if (tiles.Count + objects.Count == 0) return;
            if ((long)targetRoom.objects.Count + objects.Count > MapDocument.MaximumObjectsPerRoom)
                throw new InvalidOperationException("Pasting these objects would exceed the room object limit.");

            var incremental = new MapPasteIncrementalEdit(targetRoom);
            Session.BeginIncrementalEdit("Paste selection", incremental);
            try
            {
                var room = FindRoom(Session.Document);
                var occupancies = new Dictionary<MapLayer, Dictionary<int, int>>
                {
                    [MapLayer.ForegroundTiles] = foregroundOccupancy ?? new Dictionary<int, int>(),
                    [MapLayer.BackgroundTiles] = backgroundOccupancy ?? new Dictionary<int, int>()
                };
                foreach (var tile in tiles)
                {
                    List<MapCell> cells = Cells(room, tile.layer);
                    Dictionary<int, int> occupancy = occupancies[tile.layer];
                    int key = CellKey(tile.cell.x, tile.cell.y);
                    if (occupancy.TryGetValue(key, out int index))
                    {
                        incremental.Replace(tile.layer, index, cells[index], tile.cell);
                        cells[index] = tile.cell;
                    }
                    else
                    {
                        index = cells.Count;
                        incremental.Add(tile.layer, index, tile.cell);
                        cells.Add(tile.cell);
                        occupancy.Add(key, index);
                    }
                }
                foreach (MapObject item in objects)
                {
                    incremental.AddObject(room.objects.Count, item);
                    room.objects.Add(item);
                }
                Session.EndEdit();
            }
            catch
            {
                if (Session.IsEditing) Session.CancelEdit();
                throw;
            }
            if (source.ObjectsOnly)
            {
                Selection = null; ObjectEditor.RestoreSelection(objects.Select(item => item.id), null);
            }
            else SelectArea(new RectInt(Vector2Int.FloorToInt(at), Vector2Int.RoundToInt(source.Size)));
        }

        private void RequireClipboardIdle()
        {
            if (Session.IsEditing || IsDragging || ObjectEditor.IsDragging)
                throw new InvalidOperationException("Finish or cancel the active edit before using the clipboard.");
        }

        private string PasteGroup(string source, MapLayer layer, IReadOnlyDictionary<string, MapLayerGroup> groups)
        {
            if (Layer != MapLayer.All && !string.IsNullOrEmpty(ActiveGroupId)) return ActiveGroupId;
            return !string.IsNullOrEmpty(source) && groups.TryGetValue(source, out MapLayerGroup group) && group.layer == layer ? source : "";
        }

        private static int CellKey(int x, int y) => x * MapDocument.MaximumRoomDimension + y;

        private static Dictionary<int, int> BuildCellOccupancy(List<MapCell> cells)
        {
            var result = new Dictionary<int, int>(cells.Count);
            for (int index = 0; index < cells.Count; index++)
                if (!result.TryAdd(CellKey(cells[index].x, cells[index].y), index))
                    throw new InvalidOperationException("The target tile layer contains duplicate coordinates.");
            return result;
        }

        public void MoveSelection(Vector2Int offset)
        {
            if (UsesObjectSelection) { ObjectEditor.Move(offset); return; }
            if (!CanModifySelection() || offset == Vector2Int.zero) return;
            var area = Selection.Value;
            offset.x = MathEx.Clamp(offset.x, -area.x, Room.width - area.xMax);
            offset.y = MathEx.Clamp(offset.y, -area.y, Room.height - area.yMax);
            TransformSelection("Move selection", area, new RectInt(area.position + offset, area.size),
                c => { c.x += offset.x; c.y += offset.y; }, o => OffsetObject(o, offset));
        }

        public void FlipSelection(bool horizontal)
        {
            if (UsesObjectSelection) { ObjectEditor.Flip(horizontal); return; }
            if (!CanModifySelection()) return;
            var area = Selection.Value;
            TransformSelection("Flip selection", area, area, c =>
            {
                if (horizontal) c.x = area.xMin + area.xMax - 1 - c.x;
                else c.y = area.yMin + area.yMax - 1 - c.y;
                c.shape = MapBrushGeometry.FlipShape(c.shape, horizontal);
            }, o =>
            {
                if (horizontal) { o.x = area.xMin + area.xMax - o.x - o.width; o.scaleX = -o.scaleX; }
                else { o.y = area.yMin + area.yMax - o.y - o.height; o.scaleY = -o.scaleY; }
                o.rotation = -o.rotation;
                for (int i = 0; i < o.nodes.Count; i++)
                { var node = o.nodes[i]; if (horizontal) node.x = area.xMin + area.xMax - node.x; else node.y = area.yMin + area.yMax - node.y; o.nodes[i] = node; }
            }, definition => definition.flippable);
        }

        public void RotateSelection(bool clockwise)
        {
            if (UsesObjectSelection) { ObjectEditor.Rotate(clockwise); return; }
            if (!CanModifySelection()) return;
            var area = Selection.Value;
            if (area.x + area.height > Room.width || area.y + area.width > Room.height)
                throw new InvalidOperationException("Rotated selection would extend outside the room.");
            var target = new RectInt(area.position, new Vector2Int(area.height, area.width));
            TransformSelection("Rotate selection", area, target, c =>
            {
                int x = c.x - area.x, y = c.y - area.y;
                c.x = area.x + (clockwise ? y : area.height - 1 - y);
                c.y = area.y + (clockwise ? area.width - 1 - x : x);
                c.shape = MapBrushGeometry.RotateShape(c.shape, clockwise);
            }, o =>
            {
                float x = o.x + o.width / 2 - area.x, y = o.y + o.height / 2 - area.y;
                o.x = area.x + (clockwise ? y : area.height - y) - o.width / 2;
                o.y = area.y + (clockwise ? area.width - x : x) - o.height / 2;
                o.rotation += clockwise ? -90 : 90;
                for (int i = 0; i < o.nodes.Count; i++)
                { Vector2 n = o.nodes[i] - (Vector2)area.position; o.nodes[i] = (Vector2)area.position + (clockwise ? new Vector2(n.y, area.width - n.x) : new Vector2(area.height - n.y, n.x)); }
            }, definition => definition.rotatable);
        }

        private void TransformSelection(string label, RectInt area, RectInt target, Action<MapCell> tileTransform,
            Action<MapObject> objectTransform, Func<MapObjectDefinition, bool> acceptsObject = null)
        {
            Session.Execute(label, document =>
            {
                var room = FindRoom(document);
                foreach (var layer in TileLayers)
                {
                    var cells = Cells(room, layer);
                    var moving = cells.Where(c => Selected(c, layer, area)).ToList();
                    var movingSet = new HashSet<MapCell>(moving);
                    var remaining = cells.Where(c => !movingSet.Contains(c)).ToDictionary(c => new Vector2Int(c.x, c.y));
                    foreach (var c in moving)
                    {
                        tileTransform(c);
                        var point = new Vector2Int(c.x, c.y);
                        if (remaining.TryGetValue(point, out var destination) && !CanEditMember(layer, destination.groupId))
                            throw new InvalidOperationException("The destination contains hidden, locked, or unselected-group tiles.");
                        remaining[point] = c;
                    }
                    cells.Clear(); cells.AddRange(remaining.Values);
                }
                foreach (var obj in room.objects)
                {
                    if (!Selected(obj, area)) continue;
                    MapObjectDefinition definition = ResolveObjectDefinition?.Invoke(obj.definition);
                    if (definition != null && acceptsObject != null && !acceptsObject(definition)) continue;
                    objectTransform(obj);
                    MapObjectEditing.Corners(obj);
                    definition?.ValidateObject(obj);
                }
            });
            Selection = target;
        }
    }
}
