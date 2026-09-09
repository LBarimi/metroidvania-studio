using System;
using System.Collections.Generic;
using System.Linq;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public sealed class MapRoomResolutionFit
    {
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public int RemovedForeground { get; internal set; }
        public int RemovedBackground { get; internal set; }
        public int RemovedObjects { get; internal set; }
        public bool RemovesContent => RemovedForeground + RemovedBackground + RemovedObjects > 0;
        internal MapRoom Room;
        internal MapRoomResizePlan Layout;
        internal List<MapCell> Foreground, Background;
        internal List<MapObject> Objects;
    }

    public sealed partial class MapRoomEditing
    {
        /// <summary>Read-only plan. Whole tiles cover the reference resolution; room origin stays fixed.</summary>
        public MapRoomResolutionFit PlanResolutionFit(string id, int pixelWidth, int pixelHeight)
        {
            RequireIdle();
            if (pixelWidth < 1 || pixelHeight < 1 || pixelWidth > MapCameraSettings.MaximumResolution || pixelHeight > MapCameraSettings.MaximumResolution)
                throw new ArgumentOutOfRangeException(nameof(pixelWidth), "Unsupported room reference resolution.");
            var room = RequireRoom(id); RequireUnlocked(room);
            int width = (pixelWidth + MapDocument.RequiredTileSize - 1) / MapDocument.RequiredTileSize;
            int height = (pixelHeight + MapDocument.RequiredTileSize - 1) / MapDocument.RequiredTileSize;
            var bounds = new RectInt(room.x, room.y, width, height);
            ValidateBounds(bounds.x, bounds.y, bounds.width, bounds.height);
            var plan = new MapRoomResolutionFit
            {
                Room = room, Width = width, Height = height,
                Layout = MapRoomResizeLayout.Plan(session.Document.rooms, id, bounds),
                Foreground = RebaseCells(room.foreground, 0, 0, bounds, true),
                Background = RebaseCells(room.background, 0, 0, bounds, true),
                Objects = room.objects.Where(item => FitsResolutionRoom(item, width, height)).ToList()
            };
            plan.RemovedForeground = room.foreground.Count - plan.Foreground.Count;
            plan.RemovedBackground = room.background.Count - plan.Background.Count;
            plan.RemovedObjects = room.objects.Count - plan.Objects.Count;
            return plan;
        }

        public void FitResolution(string id, int pixelWidth, int pixelHeight, bool allowCrop = false)
        {
            var plan = PlanResolutionFit(id, pixelWidth, pixelHeight);
            if (plan.RemovesContent && !allowCrop) throw new InvalidOperationException("@roomFitResolutionNeedsConfirm");
            session.Execute("Fit room to resolution", document =>
            {
                foreach (var change in plan.Layout.Changes)
                {
                    if (change.Id == id) continue;
                    var neighbor = document.rooms.Find(room => room.id == change.Id);
                    neighbor.x = change.After.x; neighbor.y = change.After.y;
                }
                var room = plan.Room;
                room.width = plan.Width; room.height = plan.Height;
                room.foreground = plan.Foreground; room.background = plan.Background; room.objects = plan.Objects;
            });
        }

        private static bool FitsResolutionRoom(MapObject item, int width, int height)
        {
            // Float transforms may produce tiny round-off errors at a boundary.
            const float tolerance = .00001f;
            bool Inside(Vector2 point) => point.x >= -tolerance && point.y >= -tolerance
                && point.x <= width + tolerance && point.y <= height + tolerance;
            return MapObjectEditing.Corners(item).All(Inside) && item.nodes.All(Inside);
        }
    }
}
