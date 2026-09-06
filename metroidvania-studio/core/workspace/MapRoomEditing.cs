using System;
using System.Collections.Generic;
using System.Linq;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    /// <summary>Room selection and transactional room edits, independent of the editor view.</summary>
    public sealed partial class MapRoomEditing : IDisposable
    {
        private readonly MapEditSession session;
        public readonly HashSet<string> SelectedIds = new HashSet<string>(StringComparer.Ordinal);
        public string PrimaryId { get; private set; }
        public bool IsDragging { get; private set; }

        private readonly Dictionary<string, Vector2Int> moveOrigins = new Dictionary<string, Vector2Int>(StringComparer.Ordinal);
        private MapDocument moveDocument;
        private object moveEditToken;
        private bool disposed;

        public MapRoomEditing(MapEditSession session)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            session.Changed += OnDocumentChanged;
            if (session.Document.rooms.Count > 0)
            {
                PrimaryId = session.Document.rooms[0].id;
                SelectedIds.Add(PrimaryId);
            }
        }

        public void Select(string id, bool additive = false, bool toggle = false)
        {
            CheckAlive();
            if (string.IsNullOrEmpty(id)) { Clear(); return; }
            RequireRoom(id);
            CancelMove();
            PruneSelection();
            if (!additive && !toggle) SelectedIds.Clear();
            if (toggle && SelectedIds.Contains(id)) SelectedIds.Remove(id);
            else { SelectedIds.Add(id); PrimaryId = id; }
            PruneSelection();
        }

        public void SelectArea(RectInt worldArea, bool additive = false)
        {
            CheckAlive();
            ValidateArea(worldArea);
            CancelMove();
            if (!additive) SelectedIds.Clear();
            if (worldArea.width > 0 && worldArea.height > 0)
                foreach (MapRoom room in session.Document.rooms)
                    if (room.visible && Overlaps(room, worldArea)) SelectedIds.Add(room.id);
            PruneSelection();
        }

        public void SelectAll()
        {
            CheckAlive();
            CancelMove();
            SelectedIds.Clear();
            foreach (MapRoom room in session.Document.rooms)
                if (room.visible) SelectedIds.Add(room.id);
            PruneSelection();
        }

        public void Clear()
        {
            CheckAlive();
            CancelMove();
            SelectedIds.Clear();
            PrimaryId = null;
        }

        public string Create(RectInt worldBounds, string name = null)
        {
            RequireIdle();
            ValidateBounds(worldBounds.x, worldBounds.y, worldBounds.width, worldBounds.height);
            var names = new HashSet<string>(session.Document.rooms.Select(room => room.name), StringComparer.Ordinal);
            int number = 0;
            while (names.Contains("room_" + number.ToString("00"))) number++;
            var room = new MapRoom
            {
                id = NewId(AllIds()), name = name ?? "room_" + number.ToString("00"),
                x = worldBounds.x, y = worldBounds.y, width = worldBounds.width, height = worldBounds.height
            };
            session.Execute("Create room", document => document.rooms.Add(room));
            ReplaceSelection(new[] { room.id });
            return room.id;
        }

        /// <summary>Copies the selected group to its right, keeping its relative room layout.</summary>
        public string[] DuplicateSelected()
        {
            RequireIdle();
            List<MapRoom> sources = EditableSelection();
            if (sources.Count == 0) return Array.Empty<string>();
            long left = sources.Min(room => (long)room.x);
            long right = sources.Max(room => (long)room.x + room.width);
            long offset = right - left + 2;
            var names = new HashSet<string>(session.Document.rooms.Select(room => room.name), StringComparer.Ordinal);
            HashSet<string> ids = AllIds();
            var copies = new List<MapRoom>(sources.Count);
            foreach (MapRoom source in sources)
            {
                long x = (long)source.x + offset;
                ValidateBounds(x, source.y, source.width, source.height);
                MapRoom copy = MapJson.FromJson<MapRoom>(MapJson.ToJson(source));
                copy.id = NewId(ids);
                string stem = source.name + "_copy", name = stem;
                for (int suffix = 2; names.Contains(name); suffix++) name = stem + "_" + suffix;
                names.Add(name);
                copy.name = name;
                copy.x = (int)x;
                foreach (MapObject item in copy.objects) item.id = NewId(ids);
                copies.Add(copy);
            }
            session.Execute("Duplicate rooms", document => document.rooms.AddRange(copies));
            string[] result = copies.Select(room => room.id).ToArray();
            ReplaceSelection(result);
            return result;
        }

        public void DeleteSelected()
        {
            RequireIdle();
            var ids = new HashSet<string>(EditableSelection().Select(room => room.id), StringComparer.Ordinal);
            if (ids.Count == 0) return;
            session.Execute("Delete rooms", document => document.rooms.RemoveAll(room => ids.Contains(room.id)));
            PruneSelection();
        }

        public void MoveSelected(Vector2Int delta)
        {
            RequireIdle();
            List<MapRoom> rooms = EditableSelection();
            if (rooms.Count == 0 || delta == Vector2Int.zero) return;
            foreach (MapRoom room in rooms)
                ValidateBounds((long)room.x + delta.x, (long)room.y + delta.y, room.width, room.height);
            session.Execute("Move rooms", _ =>
            {
                foreach (MapRoom room in rooms) { room.x += delta.x; room.y += delta.y; }
            });
        }

        public void BeginMove()
        {
            RequireIdle();
            List<MapRoom> rooms = EditableSelection();
            if (rooms.Count == 0) return;
            session.BeginEdit("Move rooms");
            moveOrigins.Clear();
            foreach (MapRoom room in rooms) moveOrigins.Add(room.id, new Vector2Int(room.x, room.y));
            moveDocument = session.Document;
            moveEditToken = session.ActiveEditToken;
            IsDragging = true;
        }

        public void DragMove(Vector2Int deltaFromStart)
        {
            CheckAlive();
            if (!OwnsMove()) { ForgetMove(); return; }
            try
            {
                // Validate the whole group before moving any room, including near int limits.
                foreach (var origin in moveOrigins)
                {
                    MapRoom room = RequireRoom(origin.Key);
                    RequireUnlocked(room);
                    ValidateBounds((long)origin.Value.x + deltaFromStart.x, (long)origin.Value.y + deltaFromStart.y,
                        room.width, room.height);
                }
                foreach (var origin in moveOrigins)
                {
                    MapRoom room = RequireRoom(origin.Key);
                    room.x = (int)((long)origin.Value.x + deltaFromStart.x);
                    room.y = (int)((long)origin.Value.y + deltaFromStart.y);
                }
            }
            catch { CancelMove(); throw; }
        }

        public void EndMove()
        {
            CheckAlive();
            bool owns = OwnsMove();
            ForgetMove();
            if (owns) session.EndEdit();
        }

        public void CancelMove()
        {
            bool owns = OwnsMove();
            ForgetMove();
            if (owns) session.CancelEdit();
        }

        /// <summary>
        /// Changes room bounds while keeping its contents at their previous world positions.
        /// Rooms beyond changed edges and collision chains move with the full edge delta,
        /// preserving their gaps and local contents. Without crop, edges stop at terrain.
        /// Crop removes only target-room terrain.
        /// </summary>
        public void Resize(string id, RectInt worldBounds, bool crop = false)
            => Resize(id, worldBounds, crop, true);

        /// <summary>
        /// Applies an inspector-entered final rectangle while keeping the target room's
        /// contents at their existing local coordinates. The layout planner still sees
        /// the complete final rectangle, so a simultaneous position and size change
        /// cannot leave the target overlapping a neighbor.
        /// </summary>
        public void ResizeKeepingLocalContents(string id, RectInt worldBounds, bool crop = false)
            => Resize(id, worldBounds, crop, false);

        private void Resize(string id, RectInt worldBounds, bool crop, bool preserveWorldContentPosition)
        {
            RequireIdle();
            ValidateBounds(worldBounds.x, worldBounds.y, worldBounds.width, worldBounds.height);
            MapRoom room = RequireRoom(id);
            RequireUnlocked(room);
            ValidateBounds(room.x, room.y, room.width, room.height);
            if (!crop) worldBounds = ClampToTerrain(room, worldBounds, preserveWorldContentPosition);
            MapRoomResizePlan layout = MapRoomResizeLayout.Plan(session.Document.rooms, id, worldBounds);
            long offsetX = preserveWorldContentPosition ? (long)room.x - worldBounds.x : 0;
            long offsetY = preserveWorldContentPosition ? (long)room.y - worldBounds.y : 0;
            List<MapCell> foreground = RebaseCells(room.foreground, offsetX, offsetY, worldBounds, crop);
            List<MapCell> background = RebaseCells(room.background, offsetX, offsetY, worldBounds, crop);
            session.Execute("Resize room layout", document =>
            {
                foreach (MapRoomResizeChange change in layout.Changes)
                {
                    if (change.Id == id) continue;
                    MapRoom neighbor = document.rooms.Find(candidate => candidate.id == change.Id);
                    neighbor.x = change.After.x; neighbor.y = change.After.y;
                }
                room.x = worldBounds.x; room.y = worldBounds.y;
                room.width = worldBounds.width; room.height = worldBounds.height;
                room.foreground = foreground; room.background = background;
                foreach (MapObject item in room.objects)
                {
                    item.x = RebaseCoordinate(item.x, offsetX);
                    item.y = RebaseCoordinate(item.y, offsetY);
                    for (int i = 0; i < item.nodes.Count; i++)
                        item.nodes[i] = new Vector2(RebaseCoordinate(item.nodes[i].x, offsetX), RebaseCoordinate(item.nodes[i].y, offsetY));
                }
            });
        }

        public void Dispose()
        {
            if (disposed) return;
            try { CancelMove(); }
            finally
            {
                session.Changed -= OnDocumentChanged;
                disposed = true;
            }
        }

        private bool OwnsMove() => IsDragging && session.IsEditing && ReferenceEquals(moveDocument, session.Document)
            && moveEditToken != null && ReferenceEquals(moveEditToken, session.ActiveEditToken);

        private void ForgetMove()
        {
            IsDragging = false;
            moveOrigins.Clear();
            moveDocument = null;
            moveEditToken = null;
        }

        private void OnDocumentChanged()
        {
            if (IsDragging && !OwnsMove()) ForgetMove();
            PruneSelection();
        }

        private void PruneSelection()
        {
            var existing = new HashSet<string>(session.Document.rooms.Select(room => room.id), StringComparer.Ordinal);
            SelectedIds.RemoveWhere(id => !existing.Contains(id));
            if (PrimaryId == null || !SelectedIds.Contains(PrimaryId))
                PrimaryId = session.Document.rooms.FirstOrDefault(room => SelectedIds.Contains(room.id))?.id;
        }

        private void ReplaceSelection(IEnumerable<string> ids)
        {
            SelectedIds.Clear();
            PrimaryId = null;
            foreach (string id in ids)
            {
                SelectedIds.Add(id);
                if (PrimaryId == null) PrimaryId = id;
            }
        }

        private List<MapRoom> EditableSelection()
        {
            PruneSelection();
            List<MapRoom> rooms = session.Document.rooms.Where(room => SelectedIds.Contains(room.id)).ToList();
            foreach (MapRoom room in rooms)
            {
                RequireUnlocked(room);
                ValidateBounds(room.x, room.y, room.width, room.height);
            }
            return rooms;
        }

        private MapRoom RequireRoom(string id) => session.Document.rooms.Find(room => room.id == id)
            ?? throw new ArgumentException("The selected room no longer exists: " + id, nameof(id));

        private static void RequireUnlocked(MapRoom room)
        {
            if (room.locked) throw new InvalidOperationException("Room '" + room.name + "' is locked. Unlock it or remove it from the selection.");
        }

        private void RequireIdle()
        {
            CheckAlive();
            if (IsDragging || session.IsEditing) throw new InvalidOperationException("Finish or cancel the current map edit before editing rooms.");
        }

        private void CheckAlive()
        {
            if (disposed) throw new ObjectDisposedException(nameof(MapRoomEditing));
        }

        private HashSet<string> AllIds()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (MapRoom room in session.Document.rooms)
            {
                ids.Add(room.id);
                foreach (MapObject item in room.objects) ids.Add(item.id);
            }
            foreach (MapStyleground style in session.Document.stylegrounds) ids.Add(style.id);
            foreach (MapLayerGroup group in session.Document.layerGroups) ids.Add(group.id);
            return ids;
        }

        private static string NewId(HashSet<string> used)
        {
            string id;
            do { id = Guid.NewGuid().ToString("N"); } while (!used.Add(id));
            return id;
        }

        private static void ValidateBounds(long x, long y, int width, int height)
        {
            if (width < 1 || height < 1 || width > MapDocument.MaximumRoomDimension || height > MapDocument.MaximumRoomDimension)
                throw new ArgumentOutOfRangeException(nameof(width), "Room dimensions must be between 1 and 1024 tiles.");
            ValidateCoordinates(x, y, x + width, y + height);
        }

        private static void ValidateArea(RectInt area)
        {
            if (area.width < 0 || area.height < 0) throw new ArgumentException("The selection area cannot have negative dimensions.", nameof(area));
            ValidateCoordinates(area.x, area.y, (long)area.x + area.width, (long)area.y + area.height);
        }

        private static void ValidateCoordinates(long x, long y, long right, long top)
        {
            if (x < int.MinValue || y < int.MinValue || right > int.MaxValue || top > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(x), "Room coordinates and outer bounds must fit within the supported integer coordinate range.");
        }

        private static bool Overlaps(MapRoom room, RectInt area) =>
            (long)room.x < (long)area.x + area.width && (long)room.x + room.width > area.x
            && (long)room.y < (long)area.y + area.height && (long)room.y + room.height > area.y;

        private static RectInt ClampToTerrain(MapRoom room, RectInt bounds, bool preserveWorldContentPosition)
        {
            long left = bounds.x, bottom = bounds.y;
            long right = left + bounds.width, top = bottom + bounds.height;
            long originX = preserveWorldContentPosition ? room.x : bounds.x;
            long originY = preserveWorldContentPosition ? room.y : bounds.y;
            // Include every terrain group, even if its layer is hidden or locked.
            // Resolve these edges before planning neighbors, so only the final delta propagates.
            foreach (List<MapCell> cells in new[] { room.foreground, room.background })
                foreach (MapCell cell in cells)
                {
                    long x = originX + cell.x, y = originY + cell.y;
                    left = Math.Min(left, x); bottom = Math.Min(bottom, y);
                    right = Math.Max(right, x + 1); top = Math.Max(top, y + 1);
                }
            int width = checked((int)(right - left)), height = checked((int)(top - bottom));
            ValidateBounds(left, bottom, width, height);
            return new RectInt((int)left, (int)bottom, width, height);
        }

        private static List<MapCell> RebaseCells(List<MapCell> cells, long offsetX, long offsetY, RectInt bounds, bool crop)
        {
            var result = new List<MapCell>(cells.Count);
            foreach (MapCell cell in cells)
            {
                long x = cell.x + offsetX, y = cell.y + offsetY;
                if (x < 0 || y < 0 || x >= bounds.width || y >= bounds.height)
                {
                    if (!crop) throw new InvalidOperationException("The new room bounds would remove terrain. Enable crop or enlarge the room.");
                    continue;
                }
                result.Add(new MapCell { x = (int)x, y = (int)y, shape = cell.shape, material = cell.material, groupId = cell.groupId });
            }
            return result;
        }

        private static float RebaseCoordinate(float value, long offset)
        {
            double result = (double)value + offset;
            if (double.IsNaN(result) || result < -float.MaxValue || result > float.MaxValue)
                throw new InvalidOperationException("Resizing would place an object or node outside the supported coordinate range.");
            return (float)result;
        }
    }
}
