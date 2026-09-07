using System;
using System.Collections.Generic;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public readonly struct MapRoomResizeChange
    {
        public string Id { get; }
        public RectInt Before { get; }
        public RectInt After { get; }
        internal MapRoomResizeChange(string id, RectInt before, RectInt after) { Id = id; Before = before; After = after; }
    }

    public sealed class MapRoomResizePlan
    {
        public string TargetId { get; }
        public RectInt TargetBounds { get; }
        public IReadOnlyList<MapRoomResizeChange> Changes { get; }
        internal MapRoomResizePlan(string id, RectInt bounds, MapRoomResizeChange[] changes)
        { TargetId = id; TargetBounds = bounds; Changes = Array.AsReadOnly(changes); }
    }

    /// <summary>
    /// Moves only rooms sharing an original edge, following contact chains outward
    /// in that edge's direction. Gaps and corner-only contacts never transmit movement.
    /// A newly overlapping pair rejects the whole plan instead of moving an unrelated room.
    /// Existing overlaps remain permitted. The supplied rooms and their contents are never modified.
    /// </summary>
    public static class MapRoomResizeLayout
    {
        // The planner runs synchronously inside editor transactions. Malformed or
        // machine-generated documents must not turn its pair scan into an unbounded
        // UI/server stall.
        public const long MaximumCollisionChecks = 2_000_000;

        public static MapRoomResizePlan Plan(IReadOnlyList<MapRoom> rooms, string roomId, RectInt newBounds)
        {
            if (rooms == null) throw new ArgumentNullException(nameof(rooms));
            if (string.IsNullOrEmpty(roomId)) throw new ArgumentException("Choose a room to resize.", nameof(roomId));
            ValidateBounds(new Box(newBounds));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var snapshots = new List<RoomSnapshot>(rooms.Count);
            foreach (MapRoom room in rooms)
            {
                if (room == null || string.IsNullOrWhiteSpace(room.id) || !ids.Add(room.id))
                    throw new ArgumentException("Room IDs must be nonempty and unique.", nameof(rooms));
                var snapshot = new RoomSnapshot(room);
                ValidateBounds(snapshot.Bounds); snapshots.Add(snapshot);
            }
            snapshots.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            RoomSnapshot[] source = snapshots.ToArray();
            int target = Array.FindIndex(source, room => room.Id == roomId);
            if (target < 0) throw new ArgumentException("The room to resize no longer exists: " + roomId, nameof(roomId));
            if (source[target].Locked) throw Locked(source[target].Id);
            var resized = new Box(newBounds);
            long checks = 0;
            Layout state = Seed(source, target, resized, ref checks);
            ValidateOverlaps(source, target, resized, state, ref checks);
            var changes = new List<MapRoomResizeChange>();
            for (int i = 0; i < source.Length; i++)
            {
                Box after = Current(source, target, resized, state, i);
                ValidateMove(source[i], after);
                if (!after.Equals(source[i].Bounds)) changes.Add(new MapRoomResizeChange(source[i].Id, source[i].Bounds.Rect, after.Rect));
            }
            return new MapRoomResizePlan(roomId, newBounds, changes.ToArray());
        }

        public static bool TryPlan(IReadOnlyList<MapRoom> rooms, string roomId, RectInt newBounds,
            out MapRoomResizePlan plan, out string error)
        {
            try { plan = Plan(rooms, roomId, newBounds); error = null; return true; }
            catch (ArgumentException exception) { plan = null; error = exception.Message; return false; }
            catch (InvalidOperationException exception) { plan = null; error = exception.Message; return false; }
        }

        private static Layout Seed(RoomSnapshot[] rooms, int target, Box resized, ref long checks)
        {
            var state = new Layout(rooms.Length);
            Box before = rooms[target].Bounds;
            FollowContacts(rooms, target, state.X, true, false, resized.Left - before.Left, ref checks);
            FollowContacts(rooms, target, state.X, true, true, resized.Right - before.Right, ref checks);
            FollowContacts(rooms, target, state.Y, false, false, resized.Bottom - before.Bottom, ref checks);
            FollowContacts(rooms, target, state.Y, false, true, resized.Top - before.Top, ref checks);
            for (int i = 0; i < rooms.Length; i++)
                if (i != target) ValidateMove(rooms[i], rooms[i].Bounds.Move(state.X[i], state.Y[i]));
            return state;
        }

        private static void FollowContacts(RoomSnapshot[] rooms, int target, long[] offsets,
            bool xAxis, bool forward, long delta, ref long checks)
        {
            if (delta == 0) return;
            var visited = new bool[rooms.Length]; visited[target] = true;
            var pending = new Queue<int>(); pending.Enqueue(target);
            while (pending.Count > 0)
            {
                Box sender = rooms[pending.Dequeue()].Bounds;
                for (int i = 0; i < rooms.Length; i++)
                {
                    CountCheck(ref checks);
                    if (visited[i]) continue;
                    Box recipient = rooms[i].Bounds;
                    bool touches = xAxis
                        ? (forward ? sender.Right == recipient.Left : sender.Left == recipient.Right)
                            && sender.Bottom < recipient.Top && sender.Top > recipient.Bottom
                        : (forward ? sender.Top == recipient.Bottom : sender.Bottom == recipient.Top)
                            && sender.Left < recipient.Right && sender.Right > recipient.Left;
                    if (!touches) continue;
                    visited[i] = true; offsets[i] = delta; pending.Enqueue(i);
                }
            }
        }

        private static void ValidateOverlaps(RoomSnapshot[] rooms, int target, Box resized, Layout state, ref long checks)
        {
            for (int a = 0; a < rooms.Length; a++) for (int b = a + 1; b < rooms.Length; b++)
            {
                CountCheck(ref checks);
                if (!Overlaps(rooms[a].Bounds, rooms[b].Bounds)
                    && Overlaps(Current(rooms, target, resized, state, a), Current(rooms, target, resized, state, b)))
                    throw new InvalidOperationException("Resizing would make rooms overlap. Leave space for rooms that are not attached to the resized edge.");
            }
        }

        private static void CountCheck(ref long checks)
        {
            if (++checks > MaximumCollisionChecks)
                throw new InvalidOperationException("This resize layout is too complex to resolve interactively. Resize a smaller room group or one edge at a time.");
        }

        private static Box Current(RoomSnapshot[] rooms, int target, Box resized, Layout state, int index) =>
            index == target ? resized : rooms[index].Bounds.Move(state.X[index], state.Y[index]);

        private static bool Overlaps(Box a, Box b) => a.Left < b.Right && a.Right > b.Left && a.Bottom < b.Top && a.Top > b.Bottom;

        private static InvalidOperationException Locked(string id) =>
            new InvalidOperationException("Room '" + id + "' is locked and would need to move. Unlock it before resizing this layout.");

        private static void ValidateMove(RoomSnapshot room, Box after)
        {
            ValidateBounds(after);
            if (room.Locked && !after.Equals(room.Bounds)) throw Locked(room.Id);
        }

        private static void ValidateBounds(Box bounds)
        {
            long width = bounds.Right - bounds.Left, height = bounds.Top - bounds.Bottom;
            if (width < 1 || height < 1 || width > MapDocument.MaximumRoomDimension || height > MapDocument.MaximumRoomDimension)
                throw new ArgumentOutOfRangeException(nameof(bounds), "Room dimensions must be between 1 and 1024 tiles.");
            if (bounds.Left < int.MinValue || bounds.Bottom < int.MinValue || bounds.Right > int.MaxValue || bounds.Top > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(bounds), "Room coordinates and outer bounds must fit within the supported integer coordinate range.");
        }

        private sealed class Layout
        {
            public readonly long[] X, Y;
            public Layout(int count) { X = new long[count]; Y = new long[count]; }
        }

        private readonly struct RoomSnapshot
        {
            public readonly string Id;
            public readonly Box Bounds;
            public readonly bool Locked;
            public RoomSnapshot(MapRoom room) { Id = room.id; Bounds = new Box(new RectInt(room.x, room.y, room.width, room.height)); Locked = room.locked; }
        }

        private readonly struct Box : IEquatable<Box>
        {
            public readonly long Left, Bottom, Right, Top;
            public Box(RectInt bounds) : this(bounds.x, bounds.y, (long)bounds.x + bounds.width, (long)bounds.y + bounds.height) { }
            private Box(long left, long bottom, long right, long top) { Left = left; Bottom = bottom; Right = right; Top = top; }
            public Box Move(long dx, long dy) => new Box(Left + dx, Bottom + dy, Right + dx, Top + dy);
            public RectInt Rect => new RectInt((int)Left, (int)Bottom, (int)(Right - Left), (int)(Top - Bottom));
            public bool Equals(Box other) => Left == other.Left && Bottom == other.Bottom && Right == other.Right && Top == other.Top;
        }
    }
}
