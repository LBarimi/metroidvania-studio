using System;
using System.Collections.Generic;
using System.Linq;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    [Serializable]
    public readonly struct MapNodeSelection : IEquatable<MapNodeSelection>
    {
        public string ObjectId { get; }
        public int NodeIndex { get; }
        public MapNodeSelection(string objectId, int nodeIndex) { ObjectId = objectId; NodeIndex = nodeIndex; }
        public bool Equals(MapNodeSelection other) => ObjectId == other.ObjectId && NodeIndex == other.NodeIndex;
        public override bool Equals(object other) => other is MapNodeSelection node && Equals(node);
        public override int GetHashCode() => (ObjectId?.GetHashCode() ?? 0) * 397 ^ NodeIndex;
    }

    /// <summary>Room-local hit rectangles. Null members use default geometry; empty arrays have no hit area.</summary>
    public sealed class MapObjectHitGeometry
    {
        public Rect[] Body;
        public MapNodeHitGeometry[] Nodes;
    }

    public sealed class MapNodeHitGeometry
    {
        public int NodeIndex;
        public Rect[] Areas;
    }

    public sealed partial class MapObjectEditing
    {
        private readonly HashSet<MapNodeSelection> selectedNodes = new HashSet<MapNodeSelection>();
        private readonly HashSet<string> nodeOnlyIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> selectedNodeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        public Func<MapObject, MapObjectHitGeometry> HitGeometryProvider { get; set; }
        public IReadOnlyCollection<MapNodeSelection> SelectedNodes { get { SyncSelection(); return selectedNodes.ToArray(); } }
        public IEnumerable<string> SelectedBodyIds { get { SyncSelection(); return SelectedIds.Where(IsBodySelected).ToArray(); } }
        public bool IsBodySelected(string id) => SelectedIds.Contains(id) && !nodeOnlyIds.Contains(id);
        public bool IsNodeSelected(string id, int index) => selectedNodes.Contains(new MapNodeSelection(id, index));

        public void RestoreSelection(IEnumerable<string> bodyIds, IEnumerable<MapNodeSelection> nodes)
        {
            CheckAlive(); CancelMove(); SyncSelection();
            // Materialize before clearing so callers may safely pass a view of the
            // current selection. One room index also keeps large paste selection
            // restoration linear instead of calling List.Find for every ID.
            var requestedBodies = bodyIds == null ? null : new HashSet<string>(bodyIds.Where(id => id != null), StringComparer.Ordinal);
            MapNodeSelection[] requestedNodes = nodes?.ToArray();
            var objectsById = new Dictionary<string, MapObject>(StringComparer.Ordinal);
            if ((requestedBodies?.Count ?? 0) > 0 || (requestedNodes?.Length ?? 0) > 0)
            {
                IEnumerable<MapObject> roomObjects = Room?.objects ?? (IEnumerable<MapObject>)Array.Empty<MapObject>();
                foreach (MapObject item in roomObjects)
                    if (item?.id != null) objectsById.TryAdd(item.id, item);
            }

            SelectedIds.Clear(); ClearNode();
            bool contextEditable = RoomEditable() && ActiveGroupValid();
            if (requestedBodies != null) foreach (string id in requestedBodies)
                if (contextEditable && objectsById.TryGetValue(id, out MapObject item)
                    && controller.CanEditObject(item)) SelectedIds.Add(id);
            if (requestedNodes != null) foreach (MapNodeSelection node in requestedNodes)
            {
                if (!contextEditable || !objectsById.TryGetValue(node.ObjectId, out MapObject item)
                    || !controller.CanEditObject(item)
                    || node.NodeIndex < 0 || node.NodeIndex >= item.nodes.Count) continue;
                AddNodeSelection(node, item.nodes.Count);
            }
            UpdatePrimaryNode(); ResetCycle();
        }

        /// <summary>Checks nodes on selected owners, preserving body hit ordering and editability rules.</summary>
        public MapNodeSelection? HitNode(Vector2 localPoint, float tolerance)
        {
            CheckAlive(); SyncSelection(); ValidatePoint(localPoint, nameof(localPoint));
            if (float.IsNaN(tolerance) || float.IsInfinity(tolerance) || tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
            MapRoom room = Room;
            if (room == null) return null;
            bool contextEditable = RoomEditable() && ActiveGroupValid();
            var ordered = room.objects.Select((item, index) => new { item, index })
                .Where(entry => contextEditable && SelectedIds.Contains(entry.item.id) && controller.CanEditObject(entry.item))
                .OrderByDescending(entry => LayerPriority(entry.item.layer)).ThenByDescending(entry => entry.index);
            foreach (var entry in ordered)
            {
                MapObject item = entry.item;
                MapObjectHitGeometry geometry = Geometry(item);
                ILookup<int, MapNodeHitGeometry> nodeGeometry = geometry?.Nodes?.ToLookup(node => node.NodeIndex);
                for (int i = 0; i < item.nodes.Count; i++)
                {
                    bool hit = geometry?.Nodes == null ? (item.nodes[i] - localPoint).sqrMagnitude <= tolerance * tolerance
                        : nodeGeometry[i].SelectMany(node => node.Areas).Any(rect => rect.Contains(localPoint));
                    if (hit) return new MapNodeSelection(item.id, i);
                }
            }
            return null;
        }

        /// <summary>Returns every editable object body crossed by a continuous pointer segment.</summary>
        public string[] HitsSegment(Vector2 from, Vector2 to)
        {
            CheckAlive(); ValidatePoint(from, nameof(from)); ValidatePoint(to, nameof(to)); SyncSelection();
            if (!RoomEditable() || !ActiveGroupValid()) return Array.Empty<string>();
            return Room.objects.Select((item, index) => new { item, index })
                .Where(entry => controller.CanEditObject(entry.item) && BodyIntersectsSegment(entry.item, from, to))
                .OrderByDescending(entry => LayerPriority(entry.item.layer))
                .ThenBy(entry => Math.Abs((double)entry.item.width * entry.item.height * entry.item.scaleX * entry.item.scaleY))
                .ThenByDescending(entry => entry.index).Select(entry => entry.item.id).ToArray();
        }

        /// <summary>
        /// Returns every editable body crossed by a connected pointer path. Each
        /// object and custom hit geometry is prepared once for the whole batch.
        /// </summary>
        public string[] HitsPath(Vector2 from, IReadOnlyList<Vector2> points)
        {
            CheckAlive(); ValidatePoint(from, nameof(from));
            if (points == null) throw new ArgumentNullException(nameof(points));
            for (int index = 0; index < points.Count; index++) ValidatePoint(points[index], nameof(points));
            SyncSelection();
            if (!RoomEditable() || !ActiveGroupValid() || points.Count == 0) return Array.Empty<string>();
            var hits = new List<string>();
            foreach (MapObject item in Room.objects)
            {
                if (!controller.CanEditObject(item)) continue;
                Rect[] areas = Geometry(item)?.Body;
                Vector2[] corners = areas == null ? Corners(item) : null;
                Vector2 segmentStart = from;
                for (int index = 0; index < points.Count; index++)
                {
                    Vector2 segmentEnd = points[index];
                    if (segmentEnd != segmentStart && BodyIntersectsSegment(item, segmentStart, segmentEnd, areas, corners))
                    { hits.Add(item.id); break; }
                    segmentStart = segmentEnd;
                }
            }
            return hits.ToArray();
        }

        private void AddNodeSelection(MapNodeSelection node)
        {
            MapObject item = Find(node.ObjectId);
            if (item == null) return;
            AddNodeSelection(node, item.nodes.Count);
        }

        private void AddNodeSelection(MapNodeSelection node, int ownerNodeCount)
        {
            if (!SelectedIds.Contains(node.ObjectId)) nodeOnlyIds.Add(node.ObjectId);
            SelectedIds.Add(node.ObjectId); selectedNodes.Add(node);
            selectedNodeCounts[node.ObjectId] = ownerNodeCount;
            SelectedNodeObjectId = node.ObjectId; SelectedNodeIndex = node.NodeIndex;
        }

        private void UpdatePrimaryNode()
        {
            if (selectedNodes.Contains(new MapNodeSelection(SelectedNodeObjectId, SelectedNodeIndex))) return;
            MapNodeSelection first = default;
            bool found = false;
            foreach (MapNodeSelection node in selectedNodes)
            {
                if (found && (StringComparer.Ordinal.Compare(node.ObjectId, first.ObjectId) > 0
                    || node.ObjectId == first.ObjectId && node.NodeIndex >= first.NodeIndex)) continue;
                first = node; found = true;
            }
            SelectedNodeObjectId = first.ObjectId; SelectedNodeIndex = first.ObjectId == null ? -1 : first.NodeIndex;
        }

        private void PruneNodes(IReadOnlyDictionary<string, MapObject> objectsById)
        {
            selectedNodes.RemoveWhere(node =>
            {
                return !objectsById.TryGetValue(node.ObjectId, out MapObject item) || !SelectedIds.Contains(node.ObjectId)
                    || node.NodeIndex < 0 || node.NodeIndex >= item.nodes.Count
                    || selectedNodeCounts.TryGetValue(node.ObjectId, out int count) && count != item.nodes.Count;
            });
            var nodeOwners = new HashSet<string>(selectedNodes.Select(node => node.ObjectId), StringComparer.Ordinal);
            nodeOnlyIds.RemoveWhere(id => !SelectedIds.Contains(id) || !nodeOwners.Contains(id));
            foreach (string id in selectedNodeCounts.Keys.ToArray()) if (!nodeOwners.Contains(id)) selectedNodeCounts.Remove(id);
            UpdatePrimaryNode();
        }

        private Dictionary<string, int[]> NodeIndicesByObject()
        {
            var grouped = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (MapNodeSelection node in selectedNodes)
            {
                if (!grouped.TryGetValue(node.ObjectId, out List<int> indices))
                {
                    indices = new List<int>();
                    grouped.Add(node.ObjectId, indices);
                }
                indices.Add(node.NodeIndex);
            }
            var result = new Dictionary<string, int[]>(grouped.Count, StringComparer.Ordinal);
            foreach (var pair in grouped)
            {
                pair.Value.Sort();
                result.Add(pair.Key, pair.Value.ToArray());
            }
            return result;
        }

        private static int[] NodeIndices(IReadOnlyDictionary<string, int[]> indicesByObject, string id) =>
            indicesByObject.TryGetValue(id, out int[] indices) ? indices : Array.Empty<int>();

        private int[] NodeIndices(string id) => NodeIndices(NodeIndicesByObject(), id);

        private void OffsetSelection(MapObject item, Vector2 offset, IReadOnlyDictionary<string, int[]> indicesByObject)
        {
            if (IsBodySelected(item.id)) Offset(item, offset);
            else foreach (int index in NodeIndices(indicesByObject, item.id)) item.nodes[index] += offset;
        }

        private Vector2 SelectionCenter(List<MapObject> objects, IReadOnlyDictionary<string, int[]> indicesByObject)
        {
            var points = new List<Vector2>();
            foreach (MapObject item in objects)
            {
                if (IsBodySelected(item.id)) points.AddRange(Corners(item));
                else foreach (int index in NodeIndices(indicesByObject, item.id)) points.Add(item.nodes[index]);
            }
            return points.Count == 0 ? Vector2.zero : FiniteVector(((double)points.Min(p => p.x) + points.Max(p => p.x)) / 2,
                ((double)points.Min(p => p.y) + points.Max(p => p.y)) / 2);
        }

        private bool BodyContains(MapObject item, Vector2 point)
        {
            Rect[] areas = Geometry(item)?.Body;
            return areas == null ? Contains(item, point) : areas.Any(area => area.Contains(point));
        }

        private bool BodyIntersectsSegment(MapObject item, Vector2 from, Vector2 to)
        {
            Rect[] areas = Geometry(item)?.Body;
            return BodyIntersectsSegment(item, from, to, areas, areas == null ? Corners(item) : null);
        }

        private static bool BodyIntersectsSegment(MapObject item, Vector2 from, Vector2 to, Rect[] areas, Vector2[] corners)
        {
            if (areas != null)
            {
                for (int index = 0; index < areas.Length; index++)
                    if (SegmentIntersectsRect(from, to, areas[index])) return true;
                return false;
            }
            if (Contains(item, from) || Contains(item, to)) return true;
            for (int index = 0; index < corners.Length; index++)
                if (SegmentsIntersect(from, to, corners[index], corners[(index + 1) % corners.Length])) return true;
            return false;
        }

        private static bool SegmentIntersectsRect(Vector2 from, Vector2 to, Rect area)
        {
            const double epsilon = 0.000001;
            if (from.x >= area.xMin - epsilon && from.x <= area.xMax + epsilon
                && from.y >= area.yMin - epsilon && from.y <= area.yMax + epsilon
                || to.x >= area.xMin - epsilon && to.x <= area.xMax + epsilon
                && to.y >= area.yMin - epsilon && to.y <= area.yMax + epsilon) return true;
            Vector2 bottomLeft = area.min, bottomRight = new Vector2(area.xMax, area.yMin);
            Vector2 topRight = area.max, topLeft = new Vector2(area.xMin, area.yMax);
            return SegmentsIntersect(from, to, bottomLeft, bottomRight)
                || SegmentsIntersect(from, to, bottomRight, topRight)
                || SegmentsIntersect(from, to, topRight, topLeft)
                || SegmentsIntersect(from, to, topLeft, bottomLeft);
        }

        private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            const double epsilon = 0.000001;
            double abC = Cross(a, b, c), abD = Cross(a, b, d), cdA = Cross(c, d, a), cdB = Cross(c, d, b);
            if (((abC > epsilon && abD < -epsilon) || (abC < -epsilon && abD > epsilon))
                && ((cdA > epsilon && cdB < -epsilon) || (cdA < -epsilon && cdB > epsilon))) return true;
            return Math.Abs(abC) <= epsilon && OnSegment(a, b, c, epsilon)
                || Math.Abs(abD) <= epsilon && OnSegment(a, b, d, epsilon)
                || Math.Abs(cdA) <= epsilon && OnSegment(c, d, a, epsilon)
                || Math.Abs(cdB) <= epsilon && OnSegment(c, d, b, epsilon);
        }

        private static double Cross(Vector2 a, Vector2 b, Vector2 point) =>
            ((double)b.x - a.x) * (point.y - a.y) - ((double)b.y - a.y) * (point.x - a.x);

        private static bool OnSegment(Vector2 a, Vector2 b, Vector2 point, double epsilon) =>
            point.x >= Math.Min(a.x, b.x) - epsilon && point.x <= Math.Max(a.x, b.x) + epsilon
            && point.y >= Math.Min(a.y, b.y) - epsilon && point.y <= Math.Max(a.y, b.y) + epsilon;

        /// <summary>Body geometry shared by object-only and mixed tile/object rectangle operations.</summary>
        public bool OverlapsSelectionBody(MapObject item, Rect area)
        {
            CheckAlive();
            if (item == null) throw new ArgumentNullException(nameof(item));
            ValidatePoint(area.position, nameof(area)); ValidatePoint(area.size, nameof(area)); ValidatePoint(area.max, nameof(area));
            Rect[] areas = Geometry(item)?.Body;
            return areas == null ? Overlaps(item, area) : areas.Any(rect => rect.Overlaps(area));
        }

        private MapObjectHitGeometry Geometry(MapObject item)
        {
            try
            {
                MapObjectHitGeometry result = HitGeometryProvider?.Invoke(Clone(item));
                if (result == null) return null;
                if (result.Body != null && result.Body.Any(rect => !ValidHitRect(rect))) return null;
                if (result.Nodes != null && result.Nodes.Any(node => node == null || node.NodeIndex < 0 || node.NodeIndex >= item.nodes.Count
                    || node.Areas == null || node.Areas.Any(rect => !ValidHitRect(rect)))) return null;
                return result;
            }
            catch { return null; }
        }

        private static bool ValidHitRect(Rect rect) => !float.IsNaN(rect.x) && !float.IsNaN(rect.y) && !float.IsNaN(rect.width) && !float.IsNaN(rect.height)
            && !float.IsInfinity(rect.xMin) && !float.IsInfinity(rect.yMin) && !float.IsInfinity(rect.xMax) && !float.IsInfinity(rect.yMax)
            && rect.width >= 0 && rect.height >= 0;
    }
}
