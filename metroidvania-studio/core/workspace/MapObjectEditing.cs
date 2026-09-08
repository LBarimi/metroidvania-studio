using System;
using System.Collections.Generic;
using System.Linq;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    /// <summary>
    /// Object selection and atomic edits in room-local tile coordinates. x/y is the
    /// unrotated box's lower-left corner; width/height never include rotation or scale.
    /// Geometry scales about that box's center, then rotates about the same center.
    /// A rotation changes rotation, not width/height, so geometry is transformed once.
    /// Nodes are independent room-local positions and follow explicit object transforms.
    /// </summary>
    public sealed partial class MapObjectEditing : IDisposable
    {
        public readonly HashSet<string> SelectedIds = new HashSet<string>(StringComparer.Ordinal);
        public IEnumerable<MapObject> SelectedObjects => EditableSelection().ToArray();
        public string SelectedNodeObjectId { get; private set; }
        public int SelectedNodeIndex { get; private set; } = -1;
        public bool IsDragging { get; private set; }

        private readonly MapCanvasController controller;
        private MapEditSession Session => controller.Session;
        private MapRoom Room => controller.Room;
        private int selectionEpoch;
        private string selectionRoomId;
        private string[] lastHits = Array.Empty<string>();
        private Vector2 lastClick;
        private int cycleIndex;
        private readonly List<MapObject> clipboard = new List<MapObject>();
        private readonly List<MapObject> moveOrigins = new List<MapObject>();
        private readonly HashSet<string> moveBodyIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int[]> moveNodeIndices = new Dictionary<string, int[]>(StringComparer.Ordinal);
        private MapDocument moveDocument;
        private object moveEditToken;
        private MapObjectIncrementalEdit moveIncremental;
        private string moveRoomId, moveGroupId;
        private MapLayer moveLayer;
        private bool resizing;
        private Vector2Int resizeHandle;
        private bool disposed;

        public MapObjectEditing(MapCanvasController controller)
        {
            this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
            selectionEpoch = Session.DocumentEpoch;
            selectionRoomId = controller.ActiveRoomId;
            Session.Changed += OnChanged;
        }

        /// <summary>Front layer first, then smaller visible area, then the most recently placed object.</summary>
        public string[] Hits(Vector2 localPoint)
        {
            CheckAlive(); ValidatePoint(localPoint, nameof(localPoint)); SyncSelection();
            if (!RoomEditable() || !ActiveGroupValid()) return Array.Empty<string>();
            return Room.objects.Select((item, index) => new { item, index })
                .Where(entry => controller.CanEditObject(entry.item) && BodyContains(entry.item, localPoint))
                .OrderByDescending(entry => LayerPriority(entry.item.layer))
                .ThenBy(entry => Math.Abs((double)entry.item.width * entry.item.height * entry.item.scaleX * entry.item.scaleY))
                .ThenByDescending(entry => entry.index).Select(entry => entry.item.id).ToArray();
        }

        public void Click(Vector2 point, bool additive = false, bool cycle = true)
        {
            CheckAlive(); CancelMove();
            string[] hits = Hits(point);
            bool repeat = cycle && (point - lastClick).sqrMagnitude <= 1f / 256f && hits.SequenceEqual(lastHits);
            cycleIndex = repeat && hits.Length > 0 ? (cycleIndex + 1) % hits.Length : 0;
            lastClick = point; lastHits = hits;
            if (!additive) { SelectedIds.Clear(); ClearNode(); }
            if (hits.Length > 0)
            {
                string id = hits[cycleIndex];
                if (additive && IsBodySelected(id))
                {
                    if (selectedNodes.Any(node => node.ObjectId == id)) nodeOnlyIds.Add(id);
                    else SelectedIds.Remove(id);
                }
                else { SelectedIds.Add(id); nodeOnlyIds.Remove(id); }
            }
        }

        public void SelectArea(Rect area, bool additive = false)
        {
            CheckAlive(); CancelMove(); SyncSelection();
            ValidatePoint(area.position, nameof(area)); ValidatePoint(area.size, nameof(area));
            area = Rect.MinMaxRect(Math.Min(area.x, area.x + area.width), Math.Min(area.y, area.y + area.height),
                Math.Max(area.x, area.x + area.width), Math.Max(area.y, area.y + area.height));
            ValidatePoint(area.position, nameof(area)); ValidatePoint(area.max, nameof(area)); ValidatePoint(area.size, nameof(area));
            if (!additive) { SelectedIds.Clear(); ClearNode(); }
            bool contextEditable = RoomEditable() && ActiveGroupValid();
            if (contextEditable && area.width > 0 && area.height > 0)
                foreach (MapObject item in Room.objects)
                {
                    if (!controller.CanEditObject(item)) continue;
                    MapObjectHitGeometry geometry = Geometry(item);
                    ILookup<int, MapNodeHitGeometry> nodeGeometry = geometry?.Nodes?.ToLookup(node => node.NodeIndex);
                    if (geometry?.Body == null ? Overlaps(item, area) : geometry.Body.Any(rect => rect.Overlaps(area)))
                    { SelectedIds.Add(item.id); nodeOnlyIds.Remove(item.id); }
                    for (int i = 0; i < item.nodes.Count; i++)
                    {
                        bool hit = geometry?.Nodes == null ? area.Contains(item.nodes[i])
                            : nodeGeometry[i].SelectMany(node => node.Areas).Any(rect => rect.Overlaps(area));
                        if (hit) AddNodeSelection(new MapNodeSelection(item.id, i), item.nodes.Count);
                    }
                }
            UpdatePrimaryNode(); ResetCycle();
        }

        /// <summary>Strict matching also compares layer, group, size, transform, properties and relative nodes.</summary>
        public void SelectSimilar(string id, bool strict = false, bool additive = false)
        {
            CheckAlive(); CancelMove(); SyncSelection();
            MapObject source = Find(id);
            bool contextEditable = RoomEditable() && ActiveGroupValid();
            if (source == null || !contextEditable || !controller.CanEditObject(source)) return;
            if (!additive) { SelectedIds.Clear(); ClearNode(); }
            HashSet<(string key, string value)> strictProperties = strict
                ? new HashSet<(string key, string value)>(source.properties.Select(property => (property.key, property.value))) : null;
            foreach (MapObject item in Room.objects)
                if (controller.CanEditObject(item) && string.Equals(item.definition, source.definition, StringComparison.OrdinalIgnoreCase)
                    && (!strict || SameAttributes(source, item, strictProperties))) { SelectedIds.Add(item.id); nodeOnlyIds.Remove(item.id); }
            ResetCycle();
        }

        public void SelectAll()
        {
            CheckAlive(); CancelMove(); SyncSelection(); SelectedIds.Clear();
            if (RoomEditable() && ActiveGroupValid())
                foreach (MapObject item in Room.objects) if (controller.CanEditObject(item)) SelectedIds.Add(item.id);
            ClearNode(); ResetCycle();
        }

        public void Clear()
        {
            CheckAlive(); CancelMove(); SelectedIds.Clear(); ClearNode(); ResetCycle();
        }

        public void SelectNode(string id, int index, bool additive = false, bool toggle = false)
        {
            CheckAlive(); CancelMove(); SyncSelection();
            MapObject item = Find(id);
            if (item == null || !Editable(item)) return;
            if (index < 0 || index >= item.nodes.Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (!additive) { SelectedIds.Clear(); ClearNode(); }
            var node = new MapNodeSelection(id, index);
            if (toggle && selectedNodes.Remove(node))
            {
                if (nodeOnlyIds.Contains(id) && !selectedNodes.Any(candidate => candidate.ObjectId == id))
                { SelectedIds.Remove(id); nodeOnlyIds.Remove(id); selectedNodeCounts.Remove(id); }
            }
            else AddNodeSelection(node, item.nodes.Count);
            UpdatePrimaryNode(); ResetCycle();
        }

        public string Place(MapObjectDefinition definition, Vector2 position, Vector2 size, Vector2? nodeEnd = null)
        {
            RequireIdle();
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (!RoomEditable()) return null;
            MapLayer layer = controller.Layer == MapLayer.All ? definition.layer : controller.Layer;
            string group = controller.ActiveGroupId;
            if (!ActualObjectLayer(layer)) throw new ArgumentException("Object placement requires an object layer.");
            if (!controller.CanEditMember(layer, group)) return null;
            MapObject item = definition.Create(Snap(position), Snap(size), layer, group);
            if (nodeEnd.HasValue)
            {
                Vector2 endpoint = Snap(nodeEnd.Value);
                if (item.nodes.Count > 0) item.nodes[item.nodes.Count - 1] = endpoint;
                else if (definition.maximumNodes != 0) item.nodes.Add(endpoint);
                else throw new InvalidOperationException("This object definition does not accept nodes.");
            }
            definition.ValidateObject(item);
            if (item.x < 0 || item.y < 0 || item.x + item.width > Room.width || item.y + item.height > Room.height)
                return null;
            item.id = NewId(AllIds());
            AddObjectsIncrementally("Place object", new[] { item });
            ReplaceSelection(new[] { item.id });
            return item.id;
        }

        public void Copy()
        {
            RequireIdle();
            List<MapObject> selected = EditableSelection();
            if (selected.Any(item => !IsBodySelected(item.id)))
                throw new InvalidOperationException("Use the map controller's selection clipboard to copy or cut selected nodes. This legacy object clipboard copies whole bodies only.");
            clipboard.Clear();
            if (selected.Count == 0) return;
            Vector2 origin = CombinedBounds(selected).min;
            foreach (MapObject item in selected)
            {
                MapObject copy = Clone(item); Offset(copy, -origin); clipboard.Add(copy);
            }
        }

        /// <summary>Places a deep copy of a picked object, preserving its data and offsetting room-local nodes.</summary>
        public string PlaceTemplate(MapObject template, Vector2 position, Vector2? size = null)
        {
            RequireIdle();
            if (template == null) throw new ArgumentNullException(nameof(template));
            position = Snap(position);
            if (!RoomEditable()) return null;
            MapLayer layer = controller.Layer == MapLayer.All ? template.layer : controller.Layer;
            if (!ActualObjectLayer(layer)) return null;
            MapObjectDefinition definition = Definition(template);
            if (definition != null && !definition.SupportsLayer(layer)) return null;
            string group = controller.Layer != MapLayer.All && controller.ActiveGroupId.Length > 0 ? controller.ActiveGroupId
                : Session.Document.layerGroups.Any(candidate => candidate.id == template.groupId && candidate.layer == layer) ? template.groupId : "";
            if (!controller.CanEditMember(layer, group)) return null;
            MapObject copy = Clone(template);
            Offset(copy, position - new Vector2(copy.x, copy.y));
            copy.layer = layer; copy.groupId = group; copy.id = NewId(AllIds());
            if (size.HasValue && definition?.resizable != false)
            {
                Vector2 desired = Snap(size.Value), minimum = definition?.minimumSize ?? Vector2.one / 16f;
                copy.width = Math.Max(minimum.x, desired.x); copy.height = Math.Max(minimum.y, desired.y);
            }
            ValidateObject(copy);
            Session.Execute("Place picked object", _ => Room.objects.Add(copy));
            ReplaceSelection(new[] { copy.id });
            return copy.id;
        }

        public void Cut() { Copy(); Delete(); }

        public void Paste(Vector2 at)
        {
            RequireIdle(); at = Snap(at);
            if (!RoomEditable() || clipboard.Count == 0) return;
            var copies = new List<MapObject>();
            HashSet<string> ids = AllIds();
            var groupIdsByLayer = new Dictionary<MapLayer, HashSet<string>>();
            foreach (MapLayerGroup candidate in Session.Document.layerGroups)
            {
                if (!groupIdsByLayer.TryGetValue(candidate.layer, out HashSet<string> groupIds))
                {
                    groupIds = new HashSet<string>(StringComparer.Ordinal);
                    groupIdsByLayer.Add(candidate.layer, groupIds);
                }
                groupIds.Add(candidate.id);
            }
            foreach (MapObject source in clipboard)
            {
                MapLayer layer = controller.Layer == MapLayer.All ? source.layer : controller.Layer;
                if (!ActualObjectLayer(layer)) continue;
                MapObjectDefinition definition = Definition(source);
                if (definition != null && !definition.SupportsLayer(layer)) continue;
                string group = controller.Layer != MapLayer.All && controller.ActiveGroupId.Length > 0 ? controller.ActiveGroupId
                    : groupIdsByLayer.TryGetValue(layer, out HashSet<string> groupIds) && groupIds.Contains(source.groupId) ? source.groupId : "";
                if (!controller.CanEditMember(layer, group)) continue;
                MapObject copy = Clone(source);
                copy.id = NewId(ids); copy.layer = layer; copy.groupId = group; Offset(copy, at);
                ValidateObject(copy); copies.Add(copy);
            }
            if (copies.Count == 0) return;
            Session.Execute("Paste objects", _ => Room.objects.AddRange(copies));
            ReplaceSelection(copies.Select(item => item.id));
        }

        public void Delete()
        {
            RequireIdle(); List<MapObject> selected = EditableSelection();
            Dictionary<string, int[]> nodeIndices = NodeIndicesByObject();
            var ids = new HashSet<string>(selected.Where(item => IsBodySelected(item.id)).Select(item => item.id), StringComparer.Ordinal);
            var copies = new List<MapObject>();
            foreach (MapObject item in selected.Where(item => !ids.Contains(item.id)))
            {
                int[] indices = NodeIndices(nodeIndices, item.id);
                if (indices.Length == 0) continue;
                if (item.nodes.Count - indices.Length < (Definition(item)?.minimumNodes ?? 0))
                    throw new InvalidOperationException("Deleting the selected nodes would violate the minimum node count.");
                MapObject copy = Clone(item);
                foreach (int index in indices.Reverse()) copy.nodes.RemoveAt(index);
                ValidateObject(copy); copies.Add(copy);
            }
            if (ids.Count == 0 && copies.Count == 0) return;
            Session.Execute("Delete object selection", _ => { Room.objects.RemoveAll(item => ids.Contains(item.id)); ReplaceObjects(copies); });
            foreach (MapObject copy in copies) SelectedIds.Remove(copy.id);
            ClearNode();
            SyncSelection();
        }

        public void Move(Vector2 offset)
        {
            RequireIdle(); offset = Snap(offset);
            if (offset == Vector2.zero) return;
            Dictionary<string, int[]> nodeIndices = NodeIndicesByObject();
            CommitObjects("Move object selection", EditableSelection(), item => OffsetSelection(item, offset, nodeIndices));
        }

        public void Resize(Vector2 sizeDelta)
        {
            ResizeAnchored(sizeDelta, Vector2Int.one);
        }

        public void ResizeAnchored(Vector2 sizeDelta, Vector2Int handle)
        {
            RequireIdle(); MapObjectResize.ValidateHandle(handle); sizeDelta = Snap(sizeDelta);
            if (sizeDelta == Vector2.zero) return;
            List<MapObject> selected = EditableSelection().Where(item => IsBodySelected(item.id) && Definition(item)?.resizable != false).ToList();
            CommitObjects("Resize objects", selected, item => MapObjectResize.Apply(item, sizeDelta, handle, Definition(item)?.minimumSize ?? Vector2.one / 16f));
        }

        public void Flip(bool horizontal, bool area = false)
        {
            RequireIdle(); List<MapObject> selected = EditableSelection();
            if (selected.Count == 0) return;
            Dictionary<string, int[]> nodeIndices = NodeIndicesByObject();
            Vector2 commonCenter = SelectionCenter(selected, nodeIndices);
            CommitObjects("Flip objects", selected.Where(item => !IsBodySelected(item.id) || Definition(item)?.flippable != false).ToList(), item =>
            {
                if (!IsBodySelected(item.id))
                {
                    foreach (int index in NodeIndices(nodeIndices, item.id)) item.nodes[index] = Reflect(item.nodes[index], commonCenter, horizontal);
                    return;
                }
                Vector2 center = Center(item), pivot = area ? commonCenter : center;
                Vector2 next = Reflect(center, pivot, horizontal);
                item.x = next.x - item.width * 0.5f; item.y = next.y - item.height * 0.5f;
                if (horizontal) item.scaleX = -item.scaleX; else item.scaleY = -item.scaleY;
                item.rotation = NormalizeAngle(-item.rotation);
                for (int i = 0; i < item.nodes.Count; i++) item.nodes[i] = Reflect(item.nodes[i], pivot, horizontal);
            });
        }

        public void Rotate(bool clockwise, bool area = false)
        {
            RequireIdle(); List<MapObject> selected = EditableSelection();
            if (selected.Count == 0) return;
            Dictionary<string, int[]> nodeIndices = NodeIndicesByObject();
            Vector2 commonCenter = SelectionCenter(selected, nodeIndices);
            CommitObjects("Rotate objects", selected.Where(item => !IsBodySelected(item.id) || Definition(item)?.rotatable != false).ToList(), item =>
            {
                if (!IsBodySelected(item.id))
                {
                    foreach (int index in NodeIndices(nodeIndices, item.id)) item.nodes[index] = QuarterTurn(item.nodes[index], commonCenter, clockwise);
                    return;
                }
                Vector2 center = Center(item), pivot = area ? commonCenter : center;
                Vector2 next = QuarterTurn(center, pivot, clockwise);
                item.x = next.x - item.width * 0.5f; item.y = next.y - item.height * 0.5f;
                item.rotation = NormalizeAngle(item.rotation + (clockwise ? -90 : 90));
                for (int i = 0; i < item.nodes.Count; i++) item.nodes[i] = QuarterTurn(item.nodes[i], pivot, clockwise);
            });
        }

        public void AddNode(Vector2 at)
        {
            RequireIdle(); at = Snap(at);
            List<MapObject> selected = EditableSelection().Where(item =>
            {
                MapObjectDefinition definition = Definition(item);
                return definition == null || definition.maximumNodes < 0 || item.nodes.Count < definition.maximumNodes;
            }).ToList();
            if (selected.Count == 0) return;
            Dictionary<string, int[]> nodeIndices = NodeIndicesByObject();
            var indices = selected.ToDictionary(item => item.id,
                item => NodeIndices(nodeIndices, item.id).DefaultIfEmpty(item.nodes.Count - 1).Max() + 1);
            CommitObjects("Add object nodes", selected, item => item.nodes.Insert(indices[item.id], at));
            SelectedIds.Clear(); ClearNode();
            foreach (MapObject item in selected) AddNodeSelection(new MapNodeSelection(item.id, indices[item.id]), item.nodes.Count + 1);
        }

        public void MoveNode(Vector2 at)
        {
            RequireIdle(); at = Snap(at);
            MapObject item = SelectedNode();
            if (item == null) return;
            Vector2 delta = at - item.nodes[SelectedNodeIndex];
            Dictionary<string, int[]> nodeIndices = NodeIndicesByObject();
            CommitObjects("Move object nodes", EditableSelection(), copy =>
            { foreach (int index in NodeIndices(nodeIndices, copy.id)) copy.nodes[index] += delta; });
        }

        public void DeleteNode()
        {
            RequireIdle(); Dictionary<string, int[]> nodeIndices = NodeIndicesByObject();
            List<MapObject> selected = EditableSelection().Where(item => NodeIndices(nodeIndices, item.id).Length > 0).ToList();
            var indices = selected.ToDictionary(item => item.id, item => NodeIndices(nodeIndices, item.id));
            foreach (MapObject item in selected)
                if (item.nodes.Count - indices[item.id].Length < (Definition(item)?.minimumNodes ?? 0))
                    throw new InvalidOperationException("Deleting the selected nodes would violate the minimum node count.");
            CommitObjects("Delete object nodes", selected, copy => { foreach (int index in indices[copy.id].Reverse()) copy.nodes.RemoveAt(index); });
            ClearNode();
        }

        public void PasteNodes(IEnumerable<Vector2> offsets, Vector2 at)
        {
            RequireIdle(); if (offsets == null) throw new ArgumentNullException(nameof(offsets)); at = Snap(at);
            List<MapObject> selected = EditableSelection();
            if (selected.Count != 1) throw new InvalidOperationException("Select one object to paste nodes into.");
            MapObject item = selected[0]; Vector2[] values = offsets.Select(point => Snap(point + at)).ToArray();
            if (values.Length == 0) return;
            MapObjectDefinition definition = Definition(item);
            if (definition != null && definition.maximumNodes >= 0 && (long)item.nodes.Count + values.Length > definition.maximumNodes)
                throw new InvalidOperationException("Pasting these nodes would exceed the object's maximum node count.");
            int insert = NodeIndices(item.id).DefaultIfEmpty(item.nodes.Count - 1).Max() + 1;
            CommitObjects("Paste object nodes", selected, copy => copy.nodes.InsertRange(insert, values));
            SelectedIds.Clear(); ClearNode();
            for (int i = 0; i < values.Length; i++) AddNodeSelection(new MapNodeSelection(item.id, insert + i), item.nodes.Count + values.Length);
        }

        /// <summary>Each target receives only fields its definition declares; invalid values reject the whole edit.</summary>
        public void ApplyProperties(Dictionary<string, string> values)
        {
            RequireIdle();
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (values.Count == 0) return;
            List<MapObject> selected = EditableSelection().Where(item => values.ContainsKey("desc") || Definition(item)?.fields.Any(field => values.ContainsKey(field.key)) == true).ToList();
            CommitObjects("Edit object properties", selected, item =>
            {
                foreach (MapFieldDefinition field in Definition(item)?.fields ?? new List<MapFieldDefinition>())
                {
                    if (field.key == "desc" || !values.TryGetValue(field.key, out string input)) continue;
                    string normalized = MapObjectDefinition.NormalizeValue(field, input);
                    MapProperty property = item.properties.Find(entry => entry.key == field.key);
                    if (property == null) item.properties.Add(new MapProperty { key = field.key, value = normalized });
                    else property.value = normalized;
                }
                if (values.TryGetValue("desc", out string description))
                {
                    if (description == null || description.Length > 2048) throw new ArgumentException("Description must contain at most 2048 characters.");
                    MapProperty property = item.properties.Find(entry => entry.key == "desc");
                    if (property == null) item.properties.Add(new MapProperty { key = "desc", value = description });
                    else property.value = description;
                }
            });
        }

        public void AssignGroup(string id)
        {
            RequireIdle();
            if (id == null) throw new ArgumentNullException(nameof(id));
            MapLayerGroup group = id.Length == 0 ? null : Session.Document.layerGroups.Find(candidate => candidate.id == id)
                ?? throw new ArgumentException("Unknown destination group.", nameof(id));
            if (!MapLayerGroups.IsVisible(Session.Document, id) || MapLayerGroups.IsLocked(Session.Document, id))
                throw new InvalidOperationException("The destination group is hidden or locked.");
            List<MapObject> selected = EditableSelection().Where(item => group == null || item.layer == group.layer).ToList();
            CommitObjects("Assign object group", selected, item => item.groupId = id);
            SyncSelection();
        }

        public void BeginMove()
        {
            BeginTransform(false, Vector2Int.zero);
        }

        public void BeginResize(Vector2Int handle)
        {
            MapObjectResize.ValidateHandle(handle);
            BeginTransform(true, handle);
        }

        private void BeginTransform(bool resize, Vector2Int handle)
        {
            RequireIdle(); List<MapObject> selected = EditableSelection()
                .Where(item => !resize || IsBodySelected(item.id) && Definition(item)?.resizable != false).ToList();
            if (selected.Count == 0) return;
            moveOrigins.Clear(); moveOrigins.AddRange(selected.Select(Clone));
            moveBodyIds.Clear(); moveNodeIndices.Clear();
            Dictionary<string, int[]> nodeIndices = NodeIndicesByObject();
            foreach (MapObject item in selected)
            {
                if (IsBodySelected(item.id)) moveBodyIds.Add(item.id);
                moveNodeIndices[item.id] = NodeIndices(nodeIndices, item.id);
            }
            moveDocument = Session.Document; moveRoomId = controller.ActiveRoomId;
            moveLayer = controller.Layer; moveGroupId = controller.ActiveGroupId;
            moveIncremental = new MapObjectIncrementalEdit(Room);
            Session.BeginIncrementalEdit(resize ? "Resize objects" : "Move object selection", moveIncremental);
            moveEditToken = Session.ActiveEditToken; IsDragging = true; resizing = resize; resizeHandle = handle;
        }

        public void DragMove(Vector2 deltaFromStart, bool axisLock = false)
        {
            DragTransform(deltaFromStart, axisLock, false);
        }

        public void DragResize(Vector2 deltaFromStart) => DragTransform(deltaFromStart, false, true);

        private void DragTransform(Vector2 deltaFromStart, bool axisLock, bool resize)
        {
            CheckAlive();
            if (!OwnsMove()) { ForgetMove(); SyncSelection(); return; }
            if (resizing != resize) throw new InvalidOperationException("The active object gesture has a different transform type.");
            if (!RoomEditable() || moveRoomId != controller.ActiveRoomId || moveLayer != controller.Layer || moveGroupId != controller.ActiveGroupId)
            { CancelMove(); return; }
            try
            {
                Vector2 delta = Snap(deltaFromStart);
                if (axisLock) { if (Math.Abs(delta.x) >= Math.Abs(delta.y)) delta.y = 0; else delta.x = 0; }
                var copies = new List<MapObject>();
                var currentById = new Dictionary<string, MapObject>(Room.objects.Count, StringComparer.Ordinal);
                foreach (MapObject item in Room.objects)
                    if (item?.id != null) currentById.TryAdd(item.id, item);
                bool contextEditable = RoomEditable() && ActiveGroupValid();
                foreach (MapObject origin in moveOrigins)
                {
                    if (!currentById.TryGetValue(origin.id, out MapObject current) || !contextEditable
                        || !controller.CanEditObject(current) || current.nodes.Count != origin.nodes.Count)
                    { CancelMove(); return; }
                    MapObject copy = Clone(origin);
                    if (resize)
                        MapObjectResize.Apply(copy, MapObjectResize.SizeDelta(origin, delta, resizeHandle), resizeHandle,
                            Definition(copy)?.minimumSize ?? Vector2.one / 16f);
                    else if (moveBodyIds.Contains(copy.id)) Offset(copy, delta);
                    else foreach (int index in moveNodeIndices[copy.id]) copy.nodes[index] += delta;
                    ValidateObject(copy); copies.Add(copy);
                }
                ReplaceObjects(copies);
            }
            catch { CancelMove(); throw; }
        }

        public void EndMove()
        {
            CheckAlive(); bool owns = OwnsMove(); ForgetMove();
            if (owns) Session.EndEdit();
        }

        public void CancelMove()
        {
            bool owns = OwnsMove(); ForgetMove();
            if (owns) Session.CancelEdit();
        }

        public void Dispose()
        {
            if (disposed) return;
            try { CancelMove(); }
            finally { Session.Changed -= OnChanged; disposed = true; }
        }

        public static Vector2[] Corners(MapObject item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            Vector2 center = Center(item);
            double angle = item.rotation * (Math.PI / 180.0), cosine = Math.Cos(angle), sine = Math.Sin(angle);
            double halfX = item.width * (double)item.scaleX * 0.5, halfY = item.height * (double)item.scaleY * 0.5;
            var corners = new Vector2[4];
            int[] signsX = { -1, 1, 1, -1 }, signsY = { -1, -1, 1, 1 };
            for (int i = 0; i < 4; i++)
            {
                double x = halfX * signsX[i], y = halfY * signsY[i];
                corners[i] = FiniteVector(center.x + x * cosine - y * sine, center.y + x * sine + y * cosine);
            }
            return corners;
        }

        public static Rect Bounds(MapObject item)
        {
            Vector2[] corners = Corners(item);
            return Rect.MinMaxRect(corners.Min(point => point.x), corners.Min(point => point.y), corners.Max(point => point.x), corners.Max(point => point.y));
        }

        public static bool Contains(MapObject item, Vector2 point)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            ValidatePoint(point, nameof(point));
            if (Math.Abs(item.scaleX) < 0.000001f || Math.Abs(item.scaleY) < 0.000001f) return false;
            Vector2 delta = point - Center(item);
            double angle = item.rotation * (Math.PI / 180.0), cosine = Math.Cos(angle), sine = Math.Sin(angle);
            double localX = delta.x * cosine + delta.y * sine;
            double localY = -delta.x * sine + delta.y * cosine;
            return Math.Abs(localX) <= Math.Abs(item.width * (double)item.scaleX) * 0.5 + 0.000001
                && Math.Abs(localY) <= Math.Abs(item.height * (double)item.scaleY) * 0.5 + 0.000001;
        }

        private void CommitObjects(string label, List<MapObject> selected, Action<MapObject> transform)
        {
            if (selected.Count == 0) return;
            var copies = new List<MapObject>(selected.Count);
            foreach (MapObject source in selected)
            {
                MapObject copy = Clone(source); transform(copy); ValidateObject(copy); copies.Add(copy);
            }
            Session.Execute(label, _ => ReplaceObjects(copies));
        }

        private void ReplaceObjects(IEnumerable<MapObject> copies)
        {
            if (copies == null) throw new ArgumentNullException(nameof(copies));
            List<MapObject> replacements = copies.ToList();
            var indices = new Dictionary<string, int>(Room.objects.Count, StringComparer.Ordinal);
            for (int index = 0; index < Room.objects.Count; index++)
                if (Room.objects[index]?.id != null) indices.TryAdd(Room.objects[index].id, index);
            var destinations = new int[replacements.Count];
            for (int replacement = 0; replacement < replacements.Count; replacement++)
            {
                MapObject copy = replacements[replacement];
                if (copy == null || !indices.TryGetValue(copy.id, out int index))
                    throw new InvalidOperationException("The edited object no longer exists.");
                destinations[replacement] = index;
            }
            for (int replacement = 0; replacement < replacements.Count; replacement++)
            {
                MapObject copy = replacements[replacement];
                int index = destinations[replacement];
                moveIncremental?.Replace(index, Room.objects[index], copy);
                Room.objects[index] = copy;
            }
        }

        private void AddObjectsIncrementally(string label, IReadOnlyList<MapObject> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (values.Count == 0) return;
            var incremental = new MapObjectIncrementalEdit(Room);
            Session.BeginIncrementalEdit(label, incremental);
            object token = Session.ActiveEditToken;
            try
            {
                for (int index = 0; index < values.Count; index++)
                {
                    int destination = Room.objects.Count;
                    incremental.Add(destination, values[index]);
                    Room.objects.Add(values[index]);
                }
                if (ReferenceEquals(token, Session.ActiveEditToken)) Session.EndEdit();
            }
            catch
            {
                if (ReferenceEquals(token, Session.ActiveEditToken)) Session.CancelEdit();
                throw;
            }
        }

        private List<MapObject> EditableSelection()
        {
            SyncSelection();
            if (!RoomEditable() || !ActiveGroupValid()) return new List<MapObject>();
            return Room.objects.Where(item => SelectedIds.Contains(item.id) && controller.CanEditObject(item)).ToList();
        }

        private bool RoomEditable() => Room != null && Room.visible && !Room.locked;
        private bool Editable(MapObject item) => RoomEditable() && ActiveGroupValid() && controller.CanEditObject(item);
        private bool ActiveGroupValid() => string.IsNullOrEmpty(controller.ActiveGroupId)
            || Session.Document.layerGroups.Any(group => group.id == controller.ActiveGroupId && (controller.Layer == MapLayer.All || group.layer == controller.Layer));
        private MapObject Find(string id) => Room?.objects.Find(item => item.id == id);
        private MapObjectDefinition Definition(MapObject item) => controller.ResolveObjectDefinition?.Invoke(item.definition);
        private MapObject SelectedNode() => SelectedNodeIndex >= 0 ? Find(SelectedNodeObjectId) : null;

        private void ValidateObject(MapObject item)
        {
            ValidatePoint(new Vector2(item.x, item.y), "position"); ValidatePoint(new Vector2(item.width, item.height), "size");
            if (item.width <= 0 || item.height <= 0) throw new InvalidOperationException("Object size must be positive.");
            ValidatePoint(new Vector2(item.scaleX, item.scaleY), "scale");
            if (float.IsNaN(item.rotation) || float.IsInfinity(item.rotation)) throw new InvalidOperationException("Object rotation must be finite.");
            foreach (Vector2 node in item.nodes) ValidatePoint(node, "node");
            Corners(item);
            Definition(item)?.ValidateObject(item);
        }

        private void RequireIdle()
        {
            CheckAlive(); SyncSelection();
            if (IsDragging || Session.IsEditing) throw new InvalidOperationException("Finish or cancel the current edit before editing objects.");
        }

        private void SyncSelection()
        {
            CheckAlive();
            if (selectionEpoch != Session.DocumentEpoch || selectionRoomId != controller.ActiveRoomId)
            {
                if (IsDragging && OwnsMove()) CancelMove();
                SelectedIds.Clear(); ClearNode(); ResetCycle();
                selectionEpoch = Session.DocumentEpoch; selectionRoomId = controller.ActiveRoomId;
            }
            MapRoom room = Room;
            if (room == null)
            {
                SelectedIds.Clear(); ClearNode();
                return;
            }
            if (SelectedIds.Count == 0)
            {
                if (selectedNodes.Count > 0 || nodeOnlyIds.Count > 0 || selectedNodeCounts.Count > 0) ClearNode();
                return;
            }
            var objectsById = new Dictionary<string, MapObject>(room.objects.Count, StringComparer.Ordinal);
            foreach (MapObject item in room.objects)
                if (item?.id != null) objectsById.TryAdd(item.id, item);
            bool contextEditable = RoomEditable() && ActiveGroupValid();
            SelectedIds.RemoveWhere(id => !objectsById.TryGetValue(id, out MapObject item)
                || !contextEditable || !controller.CanEditObject(item));
            PruneNodes(objectsById);
        }

        private void OnChanged()
        {
            if (IsDragging && !OwnsMove()) ForgetMove();
            SyncSelection();
        }

        private bool OwnsMove() => IsDragging && Session.IsEditing && ReferenceEquals(moveDocument, Session.Document)
            && moveEditToken != null && ReferenceEquals(moveEditToken, Session.ActiveEditToken);
        private void ForgetMove() { IsDragging = false; resizing = false; moveOrigins.Clear(); moveBodyIds.Clear(); moveNodeIndices.Clear(); moveDocument = null; moveEditToken = null; moveIncremental = null; moveRoomId = null; moveGroupId = null; }
        private void ClearNode() { selectedNodes.Clear(); nodeOnlyIds.Clear(); selectedNodeCounts.Clear(); SelectedNodeObjectId = null; SelectedNodeIndex = -1; }
        private void ResetCycle() { lastHits = Array.Empty<string>(); cycleIndex = 0; }
        private void CheckAlive() { if (disposed) throw new ObjectDisposedException(nameof(MapObjectEditing)); }

        private void ReplaceSelection(IEnumerable<string> ids)
        {
            SelectedIds.Clear(); foreach (string id in ids) SelectedIds.Add(id); ClearNode(); ResetCycle(); SyncSelection();
        }

        private HashSet<string> AllIds()
        {
            var ids = new HashSet<string>(Session.Document.rooms.Select(room => room.id), StringComparer.Ordinal);
            foreach (MapRoom room in Session.Document.rooms) foreach (MapObject item in room.objects) ids.Add(item.id);
            foreach (MapLayerGroup group in Session.Document.layerGroups) ids.Add(group.id);
            foreach (MapStyleground style in Session.Document.stylegrounds) ids.Add(style.id);
            return ids;
        }

        private static string NewId(HashSet<string> ids) { string id; do { id = Guid.NewGuid().ToString("N"); } while (!ids.Add(id)); return id; }
        private static bool ActualObjectLayer(MapLayer layer) => layer >= MapLayer.Entities && layer <= MapLayer.BackgroundDecals;
        private static MapObject Clone(MapObject item) => MapJson.FromJson<MapObject>(MapJson.ToJson(item));
        private static Vector2 Center(MapObject item) => FiniteVector(item.x + item.width * 0.5, item.y + item.height * 0.5);
        private static Vector2 Snap(Vector2 value) { ValidatePoint(value, nameof(value)); return new Vector2(MathEx.Round(value.x * 16) / 16, MathEx.Round(value.y * 16) / 16); }
        private static float NormalizeAngle(float value) => MathEx.Repeat(value + 180, 360) - 180;
        private static Vector2 Reflect(Vector2 point, Vector2 pivot, bool horizontal) => horizontal ? new Vector2(pivot.x * 2 - point.x, point.y) : new Vector2(point.x, pivot.y * 2 - point.y);
        private static Vector2 QuarterTurn(Vector2 point, Vector2 pivot, bool clockwise) { Vector2 delta = point - pivot; return pivot + (clockwise ? new Vector2(delta.y, -delta.x) : new Vector2(-delta.y, delta.x)); }
        private static void Offset(MapObject item, Vector2 offset) { item.x += offset.x; item.y += offset.y; for (int i = 0; i < item.nodes.Count; i++) item.nodes[i] += offset; }
        private static int LayerPriority(MapLayer layer) => layer == MapLayer.ForegroundDecals ? 4 : layer == MapLayer.Entities ? 3 : layer == MapLayer.Triggers ? 2 : 1;
        private static void ValidatePoint(Vector2 point, string parameter) { if (float.IsNaN(point.x) || float.IsNaN(point.y) || float.IsInfinity(point.x) || float.IsInfinity(point.y)) throw new ArgumentOutOfRangeException(parameter, "Coordinates must be finite."); }
        private static Vector2 FiniteVector(double x, double y) { if (double.IsNaN(x) || double.IsNaN(y) || Math.Abs(x) > float.MaxValue || Math.Abs(y) > float.MaxValue) throw new InvalidOperationException("Transformed object coordinates exceed the supported range."); return new Vector2((float)x, (float)y); }

        private static Rect CombinedBounds(List<MapObject> objects)
        {
            Rect result = Bounds(objects[0]);
            for (int i = 1; i < objects.Count; i++) { Rect next = Bounds(objects[i]); result = Rect.MinMaxRect(Math.Min(result.xMin, next.xMin), Math.Min(result.yMin, next.yMin), Math.Max(result.xMax, next.xMax), Math.Max(result.yMax, next.yMax)); }
            return result;
        }

        public static bool Overlaps(MapObject item, Rect area)
        {
            if (Math.Abs(item.scaleX) < 0.000001f || Math.Abs(item.scaleY) < 0.000001f) return false;
            Vector2[] polygon = Corners(item);
            var rectangle = new[] { area.min, new Vector2(area.xMax, area.yMin), area.max, new Vector2(area.xMin, area.yMax) };
            var axes = new[] { Vector2.right, Vector2.up, polygon[1] - polygon[0], polygon[3] - polygon[0] };
            foreach (Vector2 axis in axes)
            {
                if (axis.sqrMagnitude < 0.00000001f) continue;
                float objectMin = polygon.Min(point => Vector2.Dot(point, axis)), objectMax = polygon.Max(point => Vector2.Dot(point, axis));
                float areaMin = rectangle.Min(point => Vector2.Dot(point, axis)), areaMax = rectangle.Max(point => Vector2.Dot(point, axis));
                if (objectMax < areaMin || areaMax < objectMin) return false;
            }
            return true;
        }

        private static bool SameAttributes(MapObject a, MapObject b, HashSet<(string key, string value)> properties)
        {
            if (a.layer != b.layer || a.groupId != b.groupId || a.width != b.width || a.height != b.height || a.rotation != b.rotation
                || a.scaleX != b.scaleX || a.scaleY != b.scaleY || a.properties.Count != b.properties.Count || a.nodes.Count != b.nodes.Count) return false;
            foreach (MapProperty property in b.properties) if (!properties.Contains((property.key, property.value))) return false;
            for (int i = 0; i < a.nodes.Count; i++) if (a.nodes[i] - new Vector2(a.x, a.y) != b.nodes[i] - new Vector2(b.x, b.y)) return false;
            return true;
        }
    }
}
