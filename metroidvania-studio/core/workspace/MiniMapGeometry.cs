using System;
using System.Collections.Generic;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public readonly struct MiniMapPoint
    {
        public readonly double X;
        public readonly double Y;
        public MiniMapPoint(double x, double y) { X = x; Y = y; }
    }

    public readonly struct MiniMapBounds
    {
        public readonly double XMin, YMin, XMax, YMax;
        private readonly bool hasValue;
        public bool IsEmpty => !hasValue;
        public double Width => IsEmpty ? 0 : XMax - XMin;
        public double Height => IsEmpty ? 0 : YMax - YMin;

        public MiniMapBounds(double xMin, double yMin, double xMax, double yMax)
        {
            XMin = xMin; YMin = yMin; XMax = xMax; YMax = yMax;
            hasValue = true;
        }
    }

    /// <summary>A positive shared boundary between two rooms, not authored door or passage data.</summary>
    public readonly struct MiniMapConnection
    {
        public readonly string RoomAId, RoomBId;
        public readonly bool Vertical;
        public readonly double Coordinate, Start, End;

        public MiniMapConnection(string roomAId, string roomBId, bool vertical, double coordinate, double start, double end)
        {
            RoomAId = roomAId; RoomBId = roomBId; Vertical = vertical;
            Coordinate = coordinate; Start = start; End = end;
        }
    }

    /// <summary>Independent, serializable camera state. Coordinates stay precise near integer world limits.</summary>
    [Serializable]
    public sealed class MiniMapViewState
    {
        public const int DefaultOutlineWidth = 6;
        public const int MaxOutlineWidth = 10;

        public double CenterX;
        public double CenterY;
        public double PixelsPerTile = 4;
        public int ChipSize = 8;
        public int OutlineWidth = DefaultOutlineWidth;
        private int outlineDefaultsVersion;
        public bool ShowGrid;
        public bool ShowNames;
        public bool Initialized;


        public void Fit(MapDocument document, Rect viewport)
        {
            MiniMapBounds bounds = MiniMapGeometry.GetBounds(document);
            if (bounds.IsEmpty)
            {
                CenterX = CenterY = 0;
                PixelsPerTile = 4;
            }
            else
            {
                CenterX = bounds.XMin + bounds.Width * .5;
                CenterY = bounds.YMin + bounds.Height * .5;
                double width = Math.Max(1, viewport.width - 48);
                double height = Math.Max(1, viewport.height - 48);
                PixelsPerTile = Math.Min(width / Math.Max(1, bounds.Width), height / Math.Max(1, bounds.Height));
            }
            Initialized = true;
            Normalize();
        }

        public void Pan(Vector2 pixels)
        {
            Normalize();
            if (!Finite(pixels.x) || !Finite(pixels.y)) return;
            CenterX -= pixels.x / PixelsPerTile;
            CenterY += pixels.y / PixelsPerTile;
            Normalize();
        }

        public void ZoomAt(Vector2 point, double factor, Rect viewport)
        {
            Normalize();
            if (!Finite(factor) || factor <= 0 || !Finite(point.x) || !Finite(point.y)) return;
            MiniMapPoint before = ViewToWorld(point, viewport);
            PixelsPerTile = Math.Max(1e-9, Math.Min(256, PixelsPerTile * factor));
            MiniMapPoint after = ViewToWorld(point, viewport);
            CenterX += before.X - after.X;
            CenterY += before.Y - after.Y;
            Normalize();
        }

        public Vector2 WorldToView(double x, double y, Rect viewport)
        {
            Normalize();
            return new Vector2(ToFloat(viewport.center.x + (x - CenterX) * PixelsPerTile),
                ToFloat(viewport.center.y - (y - CenterY) * PixelsPerTile));
        }

        public MiniMapPoint ViewToWorld(Vector2 point, Rect viewport)
        {
            Normalize();
            return new MiniMapPoint(CenterX + (point.x - viewport.center.x) / PixelsPerTile,
                CenterY - (point.y - viewport.center.y) / PixelsPerTile);
        }

        public void Normalize()
        {
            CenterX = Finite(CenterX) ? Math.Max(-1e15, Math.Min(1e15, CenterX)) : 0;
            CenterY = Finite(CenterY) ? Math.Max(-1e15, Math.Min(1e15, CenterY)) : 0;
            PixelsPerTile = Finite(PixelsPerTile) ? Math.Max(1e-9, Math.Min(256, PixelsPerTile)) : 4;
            ChipSize = Math.Max(1, Math.Min(1024, ChipSize));
            if (outlineDefaultsVersion < 1)
            {
                // Upgrade the old default once; later manual choices, including three, stay unchanged.
                if (OutlineWidth == 3) OutlineWidth = DefaultOutlineWidth;
                outlineDefaultsVersion = 1;
            }
            OutlineWidth = OutlineWidth == 0 ? DefaultOutlineWidth : Math.Max(1, Math.Min(MaxOutlineWidth, OutlineWidth));
        }

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static float ToFloat(double value) => (float)Math.Max(-1e20, Math.Min(1e20, value));
    }

    public static class MiniMapGeometry
    {
        public const int DefaultMaximumConnections = 16384;

        /// <summary>Matches only opposite edges at the same coordinate; corners and overlaps create no connection.</summary>
        public static MiniMapConnection[] GetConnections(MapDocument document)
            => GetConnections(document, DefaultMaximumConnections, out _);

        /// <summary>
        /// Matches connections up to a caller-owned output budget. Overlapping or
        /// adversarial room layouts can otherwise create a quadratic result even
        /// though their source JSON is small.
        /// </summary>
        public static MiniMapConnection[] GetConnections(MapDocument document, int maximumConnections)
            => GetConnections(document, maximumConnections, out _);

        /// <summary>
        /// Matches connections within an output budget and reports whether at
        /// least one additional valid connection was omitted.
        /// </summary>
        public static MiniMapConnection[] GetConnections(MapDocument document, int maximumConnections, out bool truncated)
        {
            if (maximumConnections < 0) throw new ArgumentOutOfRangeException(nameof(maximumConnections));
            truncated = false;
            if (document?.rooms == null || document.rooms.Count == 0) return Array.Empty<MiniMapConnection>();
            var vertical = new Dictionary<long, List<RoomEdge>>();
            var horizontal = new Dictionary<long, List<RoomEdge>>();
            for (int i = 0; i < document.rooms.Count; i++)
            {
                MapRoom room = document.rooms[i];
                if (room == null || room.width <= 0 || room.height <= 0) continue;
                AddEdge(vertical, room.x, new RoomEdge(i, room.id, room.y, (long)room.y + room.height, false));
                AddEdge(vertical, (long)room.x + room.width, new RoomEdge(i, room.id, room.y, (long)room.y + room.height, true));
                AddEdge(horizontal, room.y, new RoomEdge(i, room.id, room.x, (long)room.x + room.width, false));
                AddEdge(horizontal, (long)room.y + room.height, new RoomEdge(i, room.id, room.x, (long)room.x + room.width, true));
            }
            var result = new List<MiniMapConnection>(Math.Min(maximumConnections, 256));
            if (MatchEdges(vertical, true, result, maximumConnections, ref truncated))
                MatchEdges(horizontal, false, result, maximumConnections, ref truncated);
            return result.ToArray();
        }

        private static void AddEdge(Dictionary<long, List<RoomEdge>> buckets, long coordinate, RoomEdge edge)
        {
            if (!buckets.TryGetValue(coordinate, out List<RoomEdge> bucket)) buckets.Add(coordinate, bucket = new List<RoomEdge>());
            bucket.Add(edge);
        }

        private static bool MatchEdges(Dictionary<long, List<RoomEdge>> buckets, bool vertical,
            List<MiniMapConnection> result, int maximumConnections, ref bool truncated)
        {
            var coordinates = new List<long>(buckets.Keys);
            coordinates.Sort();
            foreach (long coordinate in coordinates)
            {
                List<RoomEdge> edges = buckets[coordinate];
                if (edges.Count < 2) continue;
                var events = new List<EdgeEvent>(edges.Count * 2);
                foreach (RoomEdge edge in edges)
                {
                    events.Add(new EdgeEvent(edge, true));
                    events.Add(new EdgeEvent(edge, false));
                }
                events.Sort((a, b) =>
                {
                    int order = a.Position.CompareTo(b.Position);
                    if (order == 0) order = a.Start.CompareTo(b.Start); // Ends precede starts: corners are not passages.
                    return order != 0 ? order : a.Edge.Index.CompareTo(b.Edge.Index);
                });
                var negative = new Dictionary<int, RoomEdge>();
                var positive = new Dictionary<int, RoomEdge>();
                foreach (EdgeEvent evt in events)
                {
                    RoomEdge edge = evt.Edge;
                    Dictionary<int, RoomEdge> own = edge.Positive ? positive : negative;
                    if (!evt.Start) { own.Remove(edge.Index); continue; }
                    Dictionary<int, RoomEdge> other = edge.Positive ? negative : positive;
                    foreach (RoomEdge opposite in other.Values)
                    {
                        long start = Math.Max(edge.Start, opposite.Start), end = Math.Min(edge.End, opposite.End);
                        if (start >= end || edge.Index == opposite.Index) continue;
                        RoomEdge a = edge.Index < opposite.Index ? edge : opposite;
                        RoomEdge b = edge.Index < opposite.Index ? opposite : edge;
                        if (result.Count >= maximumConnections)
                        {
                            truncated = true;
                            return false;
                        }
                        result.Add(new MiniMapConnection(a.Id, b.Id, vertical, coordinate, start, end));
                    }
                    own[edge.Index] = edge;
                }
            }
            return true;
        }

        private readonly struct RoomEdge
        {
            public readonly int Index;
            public readonly string Id;
            public readonly long Start, End;
            public readonly bool Positive;
            public RoomEdge(int index, string id, long start, long end, bool positive)
            { Index = index; Id = id; Start = start; End = end; Positive = positive; }
        }

        private readonly struct EdgeEvent
        {
            public readonly RoomEdge Edge;
            public readonly bool Start;
            public long Position => Start ? Edge.Start : Edge.End;
            public EdgeEvent(RoomEdge edge, bool start) { Edge = edge; Start = start; }
        }

        public static MiniMapBounds GetBounds(MapDocument document)
        {
            if (document?.rooms == null) return default;
            bool found = false;
            double xMin = 0, yMin = 0, xMax = 0, yMax = 0;
            foreach (MapRoom room in document.rooms)
            {
                if (room == null || room.width <= 0 || room.height <= 0) continue;
                double right = (double)room.x + room.width;
                double top = (double)room.y + room.height;
                if (!found)
                {
                    xMin = room.x; yMin = room.y; xMax = right; yMax = top;
                    found = true;
                }
                else
                {
                    xMin = Math.Min(xMin, room.x); yMin = Math.Min(yMin, room.y);
                    xMax = Math.Max(xMax, right); yMax = Math.Max(yMax, top);
                }
            }
            return found ? new MiniMapBounds(xMin, yMin, xMax, yMax) : default;
        }

        public static Rect RoomRect(MapRoom room, MiniMapViewState view, Rect viewport)
        {
            if (room == null || view == null) return default;
            Vector2 topLeft = view.WorldToView(room.x, (double)room.y + room.height, viewport);
            Vector2 bottomRight = view.WorldToView((double)room.x + room.width, room.y, viewport);
            return Rect.MinMaxRect(topLeft.x, topLeft.y, bottomRight.x, bottomRight.y);
        }

        /// <summary>Later rooms are drawn on top. Hidden and locked rooms remain discoverable in the overview.</summary>
        public static string HitTest(MapDocument document, MiniMapViewState view, Rect viewport, Vector2 point)
        {
            if (document?.rooms == null || view == null || !viewport.Contains(point)) return null;
            MiniMapPoint world = view.ViewToWorld(point, viewport);
            for (int i = document.rooms.Count - 1; i >= 0; i--)
            {
                MapRoom room = document.rooms[i];
                if (room != null && world.X >= room.x && world.X < (double)room.x + room.width &&
                    world.Y >= room.y && world.Y < (double)room.y + room.height) return room.id;
            }
            return null;
        }
    }

    /// <summary>Per-view adjacency cache. Rebuilds after live geometry edits, not on every zoom or repaint.</summary>
    internal sealed class MiniMapConnectionCache
    {
        private readonly List<GeometrySnapshot> snapshot = new List<GeometrySnapshot>();
        private readonly Dictionary<string, List<MiniMapConnection>> byRoom = new Dictionary<string, List<MiniMapConnection>>();
        internal readonly List<Vector2> Openings = new List<Vector2>();
        internal bool IsTruncated { get; private set; }

        public void Update(MapDocument document)
        {
            int count = document?.rooms?.Count ?? 0;
            bool changed = count != snapshot.Count;
            if (!changed)
                for (int i = 0; i < count; i++)
                    if (!snapshot[i].Matches(document.rooms[i])) { changed = true; break; }
            if (!changed) return;

            // Build before publishing so a failed rebuild cannot expose a
            // half-cleared cache to a later repaint.
            var nextSnapshot = new List<GeometrySnapshot>(count);
            for (int i = 0; i < count; i++) nextSnapshot.Add(new GeometrySnapshot(document.rooms[i]));
            MiniMapConnection[] connections = MiniMapGeometry.GetConnections(document,
                MiniMapGeometry.DefaultMaximumConnections, out bool truncated);
            var nextByRoom = new Dictionary<string, List<MiniMapConnection>>();
            foreach (MiniMapConnection connection in connections)
            {
                Add(nextByRoom, connection.RoomAId, connection);
                Add(nextByRoom, connection.RoomBId, connection);
            }
            snapshot.Clear(); snapshot.AddRange(nextSnapshot);
            byRoom.Clear();
            foreach (KeyValuePair<string, List<MiniMapConnection>> pair in nextByRoom)
                byRoom.Add(pair.Key, pair.Value);
            IsTruncated = truncated;
        }

        public List<MiniMapConnection> ForRoom(string id) => id != null && byRoom.TryGetValue(id, out List<MiniMapConnection> value) ? value : null;

        private static void Add(Dictionary<string, List<MiniMapConnection>> target, string id, MiniMapConnection connection)
        {
            if (id == null) return;
            if (!target.TryGetValue(id, out List<MiniMapConnection> list)) target.Add(id, list = new List<MiniMapConnection>());
            list.Add(connection);
        }

        private readonly struct GeometrySnapshot
        {
            private readonly string id;
            private readonly int x, y, width, height;
            private readonly bool exists;
            public GeometrySnapshot(MapRoom room)
            {
                exists = room != null; id = room?.id;
                x = room?.x ?? 0; y = room?.y ?? 0; width = room?.width ?? 0; height = room?.height ?? 0;
            }
            public bool Matches(MapRoom room) => room == null ? !exists : exists && id == room.id &&
                x == room.x && y == room.y && width == room.width && height == room.height;
        }
    }
}
