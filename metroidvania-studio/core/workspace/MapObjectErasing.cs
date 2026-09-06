using System;
using System.Collections.Generic;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    /// <summary>
    /// Deletes editable bodies crossed by a continuous pointer stroke in one
    /// transaction. Selection is not a deletion target, and a stroke stays in the
    /// room/layer/group where it began.
    /// </summary>
    public sealed class MapObjectErasing : IDisposable
    {
        private readonly MapCanvasController controller;
        private MapEditSession Session => controller.Session;
        private MapDocument document;
        private object editToken;
        private MapObjectIncrementalEdit incremental;
        private string roomId, groupId;
        private MapLayer layer;
        private MetroidvaniaStudioTool tool;
        private Vector2 previousPoint;
        private bool disposed;

        public bool IsActive { get; private set; }
        public bool IsCurrent => IsActive && ReferenceEquals(document, Session.Document)
            && roomId == controller.ActiveRoomId && layer == controller.Layer
            && groupId == controller.ActiveGroupId && tool == controller.Tool && RoomEditable()
            && (editToken == null ? !Session.IsEditing : OwnsEdit());

        public MapObjectErasing(MapCanvasController controller)
            => this.controller = controller ?? throw new ArgumentNullException(nameof(controller));

        public bool Begin(Vector2 roomLocalPoint)
        {
            CheckAlive(); ValidatePoint(roomLocalPoint);
            if (Session.IsEditing && !OwnsEdit()) return false;
            Cancel();
            if (controller.IsTileLayer || !RoomEditable() || !InsideRoom(roomLocalPoint)) return false;
            document = Session.Document; roomId = controller.ActiveRoomId;
            layer = controller.Layer; groupId = controller.ActiveGroupId; tool = controller.Tool;
            previousPoint = roomLocalPoint; IsActive = true;
            try { EraseAt(roomLocalPoint); return true; }
            catch { Cancel(); throw; }
        }

        public void Drag(Vector2 roomLocalPoint)
        {
            CheckAlive(); ValidatePoint(roomLocalPoint);
            if (!IsActive) return;
            if (!IsCurrent) { Cancel(); return; }
            // MouseUp/repeated samples must not peel away an overlapping body.
            if (roomLocalPoint == previousPoint) return;
            Vector2 from = previousPoint;
            previousPoint = roomLocalPoint;
            try { EraseBetween(from, roomLocalPoint); }
            catch { Cancel(); throw; }
        }

        /// <summary>Erases a sampled path after one room/object scan.</summary>
        public void DragPath(IReadOnlyList<Vector2> roomLocalPoints)
        {
            CheckAlive();
            if (roomLocalPoints == null) throw new ArgumentNullException(nameof(roomLocalPoints));
            for (int index = 0; index < roomLocalPoints.Count; index++) ValidatePoint(roomLocalPoints[index]);
            if (!IsActive || roomLocalPoints.Count == 0) return;
            if (!IsCurrent) { Cancel(); return; }
            try
            {
                string[] hits = controller.ObjectEditor.HitsPath(previousPoint, roomLocalPoints);
                previousPoint = roomLocalPoints[roomLocalPoints.Count - 1];
                Erase(hits);
            }
            catch { Cancel(); throw; }
        }

        public void End()
        {
            CheckAlive();
            if (!IsActive) return;
            if (!IsCurrent) { Cancel(); return; }
            bool owns = OwnsEdit(); Forget();
            if (owns) Session.EndEdit();
        }

        public void Cancel()
        {
            bool owns = OwnsEdit(); Forget();
            if (owns) Session.CancelEdit();
        }

        public void Dispose()
        {
            if (disposed) return;
            Cancel(); disposed = true;
        }

        private void EraseAt(Vector2 point)
        {
            if (!InsideRoom(point)) return;
            string[] hits = controller.ObjectEditor.Hits(point);
            if (hits.Length == 0) return;
            List<MapObject> objects = controller.Room.objects;
            int index = objects.FindIndex(item => item.id == hits[0]);
            if (index < 0) return;
            BeginIncremental();
            incremental.Remove(new[] { objects[index] }, new[] { index });
            objects.RemoveAt(index);
            // Refresh selection and node indices immediately during the pending
            // edit, before the session's final Changed notification is emitted.
            _ = controller.ObjectEditor.SelectedNodes;
        }

        private void EraseBetween(Vector2 from, Vector2 to)
        {
            Erase(controller.ObjectEditor.HitsSegment(from, to));
        }

        private void Erase(string[] hits)
        {
            if (hits.Length == 0) return;
            var ids = new HashSet<string>(hits, StringComparer.Ordinal);
            List<MapObject> current = controller.Room.objects;
            var objects = new List<MapObject>();
            var indices = new List<int>();
            for (int index = 0; index < current.Count; index++)
                if (ids.Contains(current[index].id)) { objects.Add(current[index]); indices.Add(index); }
            if (objects.Count == 0) return;
            BeginIncremental();
            incremental.Remove(objects, indices);
            current.RemoveAll(item => ids.Contains(item.id));
            _ = controller.ObjectEditor.SelectedNodes;
        }

        private void BeginIncremental()
        {
            if (editToken != null) return;
            incremental = new MapObjectIncrementalEdit(controller.Room);
            Session.BeginIncrementalEdit("Erase objects", incremental);
            editToken = Session.ActiveEditToken;
        }

        private bool RoomEditable() => controller.Room != null && controller.Room.visible && !controller.Room.locked
            && !controller.HiddenLayers.Contains(controller.Layer) && !controller.LockedLayers.Contains(controller.Layer)
            && MapLayerGroups.IsVisible(Session.Document, controller.ActiveGroupId)
            && !MapLayerGroups.IsLocked(Session.Document, controller.ActiveGroupId);
        private bool InsideRoom(Vector2 point) => controller.Room != null && point.x >= 0 && point.y >= 0
            && point.x < controller.Room.width && point.y < controller.Room.height;
        private bool OwnsEdit() => IsActive && editToken != null && ReferenceEquals(document, Session.Document)
            && ReferenceEquals(editToken, Session.ActiveEditToken);
        private void Forget() { IsActive = false; document = null; editToken = null; incremental = null; roomId = groupId = null; }
        private void CheckAlive() { if (disposed) throw new ObjectDisposedException(nameof(MapObjectErasing)); }
        private static void ValidatePoint(Vector2 point)
        {
            if (float.IsNaN(point.x) || float.IsInfinity(point.x) || float.IsNaN(point.y) || float.IsInfinity(point.y))
                throw new ArgumentOutOfRangeException(nameof(point), "Pointer coordinates must be finite.");
        }
    }
}
