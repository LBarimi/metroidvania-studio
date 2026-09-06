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
    /// Resizes a room and translates rooms beyond each changed boundary by that boundary's full delta,
    /// including detached, diagonal and hidden rooms. New overlaps propagate that same translation to
    /// intervening rooms, restoring their former gap. Unchanged boundaries do not pin unrelated rooms.
    /// Existing overlaps remain permitted; newly overlapping pairs must be separated before a plan succeeds.
    /// This planner owns no document state and never modifies the supplied rooms or their contents.
    /// </summary>
    public static class MapRoomResizeLayout
    {
        private const int MaximumAlternatives = 4096;
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
            Layout state = Seed(source, target, resized);
            state = Resolve(source, target, resized, state);
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

        private static Layout Seed(RoomSnapshot[] rooms, int target, Box resized)
        {
            var state = new Layout(rooms.Length);
            Box before = rooms[target].Bounds;
            for (int i = 0; i < rooms.Length; i++)
            {
                if (i == target) { state.XFixed[i] = state.YFixed[i] = true; continue; }
                Box room = rooms[i].Bounds;
                long dx = room.Left >= before.Right ? resized.Right - before.Right
                    : room.Right <= before.Left ? resized.Left - before.Left : 0;
                long dy = room.Bottom >= before.Top ? resized.Top - before.Top
                    : room.Top <= before.Bottom ? resized.Bottom - before.Bottom : 0;
                state.X[i] = dx; state.Y[i] = dy;
                state.XFixed[i] = dx != 0; state.YFixed[i] = dy != 0;
                ValidateMove(rooms[i], room.Move(dx, dy));
            }
            return state;
        }

        private static Layout Resolve(RoomSnapshot[] rooms, int target, Box resized, Layout initial)
        {
            // Axis assignments only become fixed, so each branch has at most two assignments per room.
            // Ambiguous diagonal contacts try both original separating axes before reporting a conflict.
            var pending = new Stack<Layout>(); pending.Push(initial);
            Exception failure = null;
            int alternatives = 0;
            long collisionChecks = 0;
            while (pending.Count > 0)
            {
                if (++alternatives > MaximumAlternatives)
                    throw new InvalidOperationException("This resize has too many conflicting layout alternatives. Resize one edge at a time.");
                Layout state = pending.Pop();
                while (true)
                {
                    List<Propagation> choice = null;
                    bool collision = false;
                    for (int a = 0; a < rooms.Length; a++) for (int b = a + 1; b < rooms.Length; b++)
                    {
                        if (++collisionChecks > MaximumCollisionChecks)
                            throw new InvalidOperationException("This resize layout is too complex to resolve interactively. Resize a smaller room group or one edge at a time.");
                        if (Overlaps(rooms[a].Bounds, rooms[b].Bounds)
                            || !Overlaps(Current(rooms, target, resized, state, a), Current(rooms, target, resized, state, b))) continue;
                        collision = true;
                        var options = new List<Propagation>(2);
                        AddOptions(options, rooms, target, resized, state, a, b, true, ref failure);
                        AddOptions(options, rooms, target, resized, state, a, b, false, ref failure);
                        // An unresolved pair may disappear after another room receives a perpendicular translation.
                        if (options.Count > 0 && (choice == null || options.Count < choice.Count)) choice = options;
                    }
                    if (!collision) return state;
                    if (choice == null) break;
                    choice.Sort((a, b) =>
                    {
                        int order = Math.Abs(a.Delta).CompareTo(Math.Abs(b.Delta));
                        if (order == 0) order = b.XAxis.CompareTo(a.XAxis);
                        if (order == 0) order = a.Recipient.CompareTo(b.Recipient);
                        return order;
                    });
                    for (int i = choice.Count - 1; i > 0; i--)
                    {
                        Layout alternative = state.Clone(); Apply(alternative, choice[i]); pending.Push(alternative);
                    }
                    Apply(state, choice[0]);
                }
            }
            if (failure != null) throw failure;
            throw new InvalidOperationException("Resizing would make rooms overlap: incompatible boundary movements cannot preserve their gaps.");
        }

        private static void AddOptions(List<Propagation> options, RoomSnapshot[] rooms, int target, Box resized,
            Layout state, int a, int b, bool xAxis, ref Exception failure)
        {
            Box beforeA = rooms[a].Bounds, beforeB = rooms[b].Bounds;
            bool separated = xAxis ? beforeA.Right <= beforeB.Left || beforeB.Right <= beforeA.Left
                : beforeA.Top <= beforeB.Bottom || beforeB.Top <= beforeA.Bottom;
            if (!separated) return;
            bool[] fixedAxis = xAxis ? state.XFixed : state.YFixed;
            if (fixedAxis[a] == fixedAxis[b]) return;
            int sender = fixedAxis[a] ? a : b, recipient = fixedAxis[a] ? b : a;
            if (recipient == target) return;
            long delta = xAxis ? state.X[sender] : state.Y[sender];
            if (sender == target)
            {
                Box recipientBefore = rooms[recipient].Bounds, targetBefore = rooms[target].Bounds;
                delta = xAxis ? recipientBefore.Left >= targetBefore.Right ? resized.Right - targetBefore.Right : resized.Left - targetBefore.Left
                    : recipientBefore.Bottom >= targetBefore.Top ? resized.Top - targetBefore.Top : resized.Bottom - targetBefore.Bottom;
            }
            Box after = rooms[recipient].Bounds.Move(xAxis ? delta : state.X[recipient], xAxis ? state.Y[recipient] : delta);
            try { ValidateMove(rooms[recipient], after); }
            catch (ArgumentOutOfRangeException exception) { failure = failure ?? exception; return; }
            catch (InvalidOperationException exception) { failure = failure ?? exception; return; }
            options.Add(new Propagation(recipient, xAxis, delta));
        }

        private static void Apply(Layout state, Propagation propagation)
        {
            if (propagation.XAxis) { state.X[propagation.Recipient] = propagation.Delta; state.XFixed[propagation.Recipient] = true; }
            else { state.Y[propagation.Recipient] = propagation.Delta; state.YFixed[propagation.Recipient] = true; }
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
            public long[] X, Y;
            public bool[] XFixed, YFixed;
            public Layout(int count) { X = new long[count]; Y = new long[count]; XFixed = new bool[count]; YFixed = new bool[count]; }
            public Layout Clone() => new Layout(0) { X = (long[])X.Clone(), Y = (long[])Y.Clone(), XFixed = (bool[])XFixed.Clone(), YFixed = (bool[])YFixed.Clone() };
        }

        private readonly struct Propagation
        {
            public readonly int Recipient;
            public readonly bool XAxis;
            public readonly long Delta;
            public Propagation(int recipient, bool xAxis, long delta) { Recipient = recipient; XAxis = xAxis; Delta = delta; }
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
