using System;
using System.Collections.Generic;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    /// <summary>A detached room snapshot. Visibility filters targets; all supplied moving rooms move together.</summary>
    public readonly struct MapRoomSnapBounds
    {
        public string Id { get; }
        public RectInt Bounds { get; }
        public bool Visible { get; }
        public MapRoomSnapBounds(string id, RectInt bounds, bool visible = true) { Id = id; Bounds = bounds; Visible = visible; }
    }

    public readonly struct MapRoomMoveSnapResult
    {
        public Vector2Int Delta { get; }
        public int? GuideX { get; }
        public int? GuideY { get; }
        internal MapRoomMoveSnapResult(Vector2Int delta, int? guideX, int? guideY) { Delta = delta; GuideX = guideX; GuideY = guideY; }
    }

    public readonly struct MapRoomResizeSnapResult
    {
        public RectInt Bounds { get; }
        public int? GuideX { get; }
        public int? GuideY { get; }
        internal MapRoomResizeSnapResult(RectInt bounds, int? guideX, int? guideY) { Bounds = bounds; GuideX = guideX; GuideY = guideY; }
    }

    /// <summary>
    /// Pure room-edge snapping in tile units. A candidate needs orthogonal interval overlap or contact.
    /// Distance ranks first (including exact joins), then opposing-edge joins and stable coordinates/IDs.
    /// The closest axis wins first; a second-axis snap must preserve the first guide's interval contact.
    /// Bounds and deltas are clamped before snapping; results always fit the document's room limits.
    /// A zero threshold disables both snapping and guides (range clamping still applies).
    /// </summary>
    public static class MapRoomSnapping
    {
        public static MapRoomMoveSnapResult Move(IReadOnlyList<MapRoomSnapBounds> originals, Vector2Int proposedDelta,
            IReadOnlyList<MapRoomSnapBounds> targets, float thresholdTiles)
        {
            ValidateThreshold(thresholdTiles);
            HashSet<string> movingIds = ValidateRooms(originals, nameof(originals));
            ValidateRooms(targets, nameof(targets));
            if (originals.Count == 0) return new MapRoomMoveSnapResult(proposedDelta, null, null);
            var limits = new DeltaLimits(originals);
            Vector2Int delta = limits.Clamp(proposedDelta);
            if (thresholdTiles == 0) return new MapRoomMoveSnapResult(delta, null, null);
            Candidate? x = FindMove(true, originals, targets, movingIds, delta, limits, thresholdTiles, null);
            Candidate? y = FindMove(false, originals, targets, movingIds, delta, limits, thresholdTiles, null);
            Candidate? first = Better(x, y);
            if (!first.HasValue) return new MapRoomMoveSnapResult(delta, null, null);
            delta = Apply(delta, first.Value);
            Candidate? second = FindMove(!first.Value.XAxis, originals, targets, movingIds, delta, limits, thresholdTiles, first);
            if (second.HasValue) delta = Apply(delta, second.Value);
            return new MapRoomMoveSnapResult(delta, Guide(true, first, second), Guide(false, first, second));
        }

        public static MapRoomResizeSnapResult Resize(MapRoomSnapBounds original, RectInt proposed, Vector2Int handle,
            IReadOnlyList<MapRoomSnapBounds> targets, float thresholdTiles)
        {
            ValidateThreshold(thresholdTiles); ValidateRoom(original, nameof(original)); ValidateRooms(targets, nameof(targets));
            if (handle == Vector2Int.zero || Math.Abs((long)handle.x) > 1 || Math.Abs((long)handle.y) > 1)
                throw new ArgumentOutOfRangeException(nameof(handle), "A resize handle must identify an edge or corner using -1, 0, or 1.");
            Box bounds = ClampResize(new Box(original.Bounds), proposed, handle);
            if (thresholdTiles == 0) return new MapRoomResizeSnapResult(bounds.Rect, null, null);
            Candidate? x = handle.x == 0 ? null : FindResize(true, original.Id, bounds, targets, handle.x, thresholdTiles, null);
            Candidate? y = handle.y == 0 ? null : FindResize(false, original.Id, bounds, targets, handle.y, thresholdTiles, null);
            Candidate? first = Better(x, y);
            if (!first.HasValue) return new MapRoomResizeSnapResult(bounds.Rect, null, null);
            bounds = bounds.Resize(first.Value.XAxis, first.Value.SourceHigh, first.Value.Guide);
            int otherHandle = first.Value.XAxis ? handle.y : handle.x;
            Candidate? second = otherHandle == 0 ? null
                : FindResize(!first.Value.XAxis, original.Id, bounds, targets, otherHandle, thresholdTiles, first);
            if (second.HasValue) bounds = bounds.Resize(second.Value.XAxis, second.Value.SourceHigh, second.Value.Guide);
            return new MapRoomResizeSnapResult(bounds.Rect, Guide(true, first, second), Guide(false, first, second));
        }

        private static Candidate? FindMove(bool xAxis, IReadOnlyList<MapRoomSnapBounds> originals,
            IReadOnlyList<MapRoomSnapBounds> targets, HashSet<string> excluded, Vector2Int delta, DeltaLimits limits,
            float threshold, Candidate? first)
        {
            Candidate? best = null;
            for (int sourceIndex = 0; sourceIndex < originals.Count; sourceIndex++)
            {
                MapRoomSnapBounds source = originals[sourceIndex];
                Box moved = new Box(source.Bounds).Move(delta);
                foreach (MapRoomSnapBounds target in targets)
                {
                    if (!target.Visible || excluded.Contains(target.Id)) continue;
                    Box targetBox = new Box(target.Bounds);
                    if (!OrthogonalContact(moved, targetBox, xAxis)) continue;
                    for (int sourceEdge = 0; sourceEdge < 2; sourceEdge++)
                        for (int targetEdge = 0; targetEdge < 2; targetEdge++)
                        {
                            long guide = targetBox.Edge(xAxis, targetEdge != 0);
                            long correction = guide - moved.Edge(xAxis, sourceEdge != 0);
                            if (Math.Abs(correction) > (double)threshold) continue;
                            long next = (xAxis ? (long)delta.x : delta.y) + correction;
                            if (!limits.Contains(xAxis, next)) continue;
                            var candidate = new Candidate(xAxis, correction, guide, sourceIndex, source.Id, target.Id,
                                sourceEdge != 0, sourceEdge != targetEdge, targetBox);
                            if (first.HasValue)
                            {
                                Vector2Int nextDelta = Apply(delta, candidate);
                                Box firstSource = new Box(originals[first.Value.SourceIndex].Bounds).Move(nextDelta);
                                if (!OrthogonalContact(firstSource, first.Value.Target, first.Value.XAxis)) continue;
                            }
                            best = Better(best, candidate);
                        }
                }
            }
            return best;
        }

        private static Candidate? FindResize(bool xAxis, string sourceId, Box bounds, IReadOnlyList<MapRoomSnapBounds> targets,
            int direction, float threshold, Candidate? first)
        {
            Candidate? best = null;
            bool high = direction > 0;
            foreach (MapRoomSnapBounds target in targets)
            {
                if (!target.Visible || target.Id == sourceId) continue;
                Box targetBox = new Box(target.Bounds);
                if (!OrthogonalContact(bounds, targetBox, xAxis)) continue;
                for (int edge = 0; edge < 2; edge++)
                {
                    long guide = targetBox.Edge(xAxis, edge != 0);
                    long correction = guide - bounds.Edge(xAxis, high);
                    if (Math.Abs(correction) > (double)threshold) continue;
                    Box resized = bounds.Resize(xAxis, high, guide);
                    if (!resized.Valid || first.HasValue && !OrthogonalContact(resized, first.Value.Target, first.Value.XAxis)) continue;
                    best = Better(best, new Candidate(xAxis, correction, guide, 0, sourceId, target.Id, high, high != (edge != 0), targetBox));
                }
            }
            return best;
        }

        private static Box ClampResize(Box original, RectInt proposed, Vector2Int handle)
        {
            long left = original.Left, right = original.Right, bottom = original.Bottom, top = original.Top;
            int maximum = MapDocument.MaximumRoomDimension;
            if (handle.x < 0) left = Clamp(proposed.x, Math.Max(int.MinValue, right - maximum), right - 1);
            else if (handle.x > 0) right = Clamp((long)proposed.x + proposed.width, left + 1, Math.Min(int.MaxValue, left + maximum));
            if (handle.y < 0) bottom = Clamp(proposed.y, Math.Max(int.MinValue, top - maximum), top - 1);
            else if (handle.y > 0) top = Clamp((long)proposed.y + proposed.height, bottom + 1, Math.Min(int.MaxValue, bottom + maximum));
            return new Box(left, bottom, right, top);
        }

        private static bool OrthogonalContact(Box a, Box b, bool xAxis) => xAxis
            ? a.Bottom <= b.Top && a.Top >= b.Bottom : a.Left <= b.Right && a.Right >= b.Left;

        private static Vector2Int Apply(Vector2Int delta, Candidate candidate) => candidate.XAxis
            ? new Vector2Int((int)((long)delta.x + candidate.Correction), delta.y)
            : new Vector2Int(delta.x, (int)((long)delta.y + candidate.Correction));

        private static int? Guide(bool xAxis, Candidate? first, Candidate? second)
        {
            if (first.HasValue && first.Value.XAxis == xAxis) return (int)first.Value.Guide;
            if (second.HasValue && second.Value.XAxis == xAxis) return (int)second.Value.Guide;
            return null;
        }

        private static Candidate? Better(Candidate? a, Candidate? b)
        {
            if (!a.HasValue) return b;
            if (!b.HasValue) return a;
            return Compare(a.Value, b.Value) <= 0 ? a : b;
        }

        private static int Compare(Candidate a, Candidate b)
        {
            int result = Math.Abs(a.Correction).CompareTo(Math.Abs(b.Correction));
            if (result == 0) result = b.Join.CompareTo(a.Join);
            if (result == 0) result = a.Guide.CompareTo(b.Guide);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.TargetId, b.TargetId);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.SourceId, b.SourceId);
            if (result == 0) result = b.XAxis.CompareTo(a.XAxis);
            if (result == 0) result = a.SourceHigh.CompareTo(b.SourceHigh);
            return result;
        }

        private static HashSet<string> ValidateRooms(IReadOnlyList<MapRoomSnapBounds> rooms, string parameter)
        {
            if (rooms == null) throw new ArgumentNullException(parameter);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (MapRoomSnapBounds room in rooms)
            {
                ValidateRoom(room, parameter);
                if (!ids.Add(room.Id)) throw new ArgumentException("Room snapshot IDs must be unique.", parameter);
            }
            return ids;
        }

        private static void ValidateRoom(MapRoomSnapBounds room, string parameter)
        {
            if (string.IsNullOrWhiteSpace(room.Id) || !new Box(room.Bounds).Valid)
                throw new ArgumentException("Room snapshots need an ID and valid 1–1024 tile bounds within the integer coordinate range.", parameter);
        }

        private static void ValidateThreshold(float threshold)
        {
            if (float.IsNaN(threshold) || float.IsInfinity(threshold) || threshold < 0)
                throw new ArgumentOutOfRangeException(nameof(threshold), "Snap distance must be finite and nonnegative.");
        }

        private static long Clamp(long value, long minimum, long maximum) => Math.Max(minimum, Math.Min(maximum, value));

        private readonly struct DeltaLimits
        {
            private readonly long minX, maxX, minY, maxY;
            public DeltaLimits(IReadOnlyList<MapRoomSnapBounds> rooms)
            {
                long left = int.MinValue, right = int.MaxValue, bottom = int.MinValue, top = int.MaxValue;
                foreach (MapRoomSnapBounds room in rooms)
                {
                    Box box = new Box(room.Bounds);
                    left = Math.Max(left, int.MinValue - box.Left); right = Math.Min(right, int.MaxValue - box.Right);
                    bottom = Math.Max(bottom, int.MinValue - box.Bottom); top = Math.Min(top, int.MaxValue - box.Top);
                }
                minX = left; maxX = right; minY = bottom; maxY = top;
            }
            public Vector2Int Clamp(Vector2Int delta) => new Vector2Int((int)MapRoomSnapping.Clamp(delta.x, minX, maxX), (int)MapRoomSnapping.Clamp(delta.y, minY, maxY));
            public bool Contains(bool xAxis, long value) => xAxis ? value >= minX && value <= maxX : value >= minY && value <= maxY;
        }

        private readonly struct Candidate
        {
            public readonly bool XAxis, SourceHigh, Join;
            public readonly long Correction, Guide;
            public readonly int SourceIndex;
            public readonly string SourceId, TargetId;
            public readonly Box Target;
            public Candidate(bool xAxis, long correction, long guide, int sourceIndex, string sourceId, string targetId,
                bool sourceHigh, bool join, Box target)
            {
                XAxis = xAxis; Correction = correction; Guide = guide; SourceIndex = sourceIndex;
                SourceId = sourceId; TargetId = targetId; SourceHigh = sourceHigh; Join = join; Target = target;
            }
        }

        private readonly struct Box
        {
            public readonly long Left, Bottom, Right, Top;
            public Box(RectInt rect) : this(rect.x, rect.y, (long)rect.x + rect.width, (long)rect.y + rect.height) { }
            public Box(long left, long bottom, long right, long top) { Left = left; Bottom = bottom; Right = right; Top = top; }
            public long Edge(bool xAxis, bool high) => xAxis ? high ? Right : Left : high ? Top : Bottom;
            public Box Move(Vector2Int delta) => new Box(Left + delta.x, Bottom + delta.y, Right + delta.x, Top + delta.y);
            public Box Resize(bool xAxis, bool high, long guide) => xAxis
                ? new Box(high ? Left : guide, Bottom, high ? guide : Right, Top)
                : new Box(Left, high ? Bottom : guide, Right, high ? guide : Top);
            public bool Valid => Left >= int.MinValue && Bottom >= int.MinValue && Right <= int.MaxValue && Top <= int.MaxValue
                && Right > Left && Top > Bottom && Right - Left <= MapDocument.MaximumRoomDimension && Top - Bottom <= MapDocument.MaximumRoomDimension;
            public RectInt Rect => new RectInt((int)Left, (int)Bottom, (int)(Right - Left), (int)(Top - Bottom));
        }
    }
}
