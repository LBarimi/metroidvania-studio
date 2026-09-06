using System;
using System.Collections.Generic;
using System.Linq;

namespace MetroidvaniaStudio
{
    public readonly struct MapClearResult
    {
        public int Removed { get; }
        public int Protected { get; }
        public MapClearResult(int removed, int protectedCount) { Removed = removed; Protected = protectedCount; }
    }

    public sealed partial class MapCanvasController
    {
        public bool CanClearRoomObjects => Room != null && Room.visible && !Room.locked;
        public bool CanClearCurrentLayer => CanClearRoomObjects && Layer >= MapLayer.ForegroundTiles && Layer < MapLayer.All
            && !HiddenLayers.Contains(Layer) && !LockedLayers.Contains(Layer);

        /// <summary>Clears all four object layers in this room, preserving hidden or locked members and every terrain cell.</summary>
        public MapClearResult ClearRoomObjects()
        {
            RequireClearingIdle();
            MapRoom room = Room;
            if (room == null) return default;
            if (!CanClearRoomObjects) return new MapClearResult(0, room.objects.Count);
            return ClearObjects(room, null, "Clear room objects");
        }

        /// <summary>Clears one base layer across all its groups. ActiveGroupId and selection bounds do not narrow this operation.</summary>
        public MapClearResult ClearCurrentLayer()
        {
            RequireClearingIdle();
            if (Layer == MapLayer.All) throw new InvalidOperationException("Choose one layer before clearing its contents.");
            if (Layer < MapLayer.ForegroundTiles || Layer >= MapLayer.All) throw new ArgumentOutOfRangeException(nameof(Layer));
            MapRoom room = Room;
            if (room == null) return default;
            int total = IsTileLayer ? Cells(room, Layer).Count() : room.objects.Count(item => item.layer == Layer);
            if (!CanClearCurrentLayer) return new MapClearResult(0, total);
            if (!IsTileLayer) return ClearObjects(room, Layer, "Clear room layer");
            MapLayer layer = Layer;
            // Preflight the complete removal set before starting the edit; protected groups remain unchanged.
            var removed = new HashSet<MapCell>(Cells(room, layer).Where(cell => CanClearMember(layer, cell.groupId)));
            if (removed.Count == 0) return new MapClearResult(0, total);
            Session.Execute("Clear room layer", _ =>
            {
                if (layer == MapLayer.ForegroundTiles) room.foreground.RemoveAll(removed.Contains);
                else room.background.RemoveAll(removed.Contains);
            });
            return new MapClearResult(removed.Count, total - removed.Count);
        }

        private MapClearResult ClearObjects(MapRoom room, MapLayer? layer, string label)
        {
            MapObject[] candidates = room.objects.Where(item => !layer.HasValue || item.layer == layer.Value).ToArray();
            var removed = new HashSet<string>(candidates.Where(item => CanClearMember(item.layer, item.groupId)
                && (ObjectVisibilityFilter?.Invoke(item) ?? true)).Select(item => item.id), StringComparer.Ordinal);
            if (removed.Count > 0) Session.Execute(label, _ => room.objects.RemoveAll(item => removed.Contains(item.id)));
            return new MapClearResult(removed.Count, candidates.Length - removed.Count);
        }

        // Unlike CanEditMember this deliberately ignores the currently selected layer/group.
        private bool CanClearMember(MapLayer layer, string groupId) => IsMemberVisible(layer, groupId)
            && !LockedLayers.Contains(layer) && !MapLayerGroups.IsLocked(Session.Document, groupId);

        private void RequireClearingIdle()
        {
            if (Session.IsEditing || IsDragging || RoomEditor.IsDragging || ObjectEditor.IsDragging)
                throw new InvalidOperationException("Finish or cancel the current edit before clearing room contents.");
        }
    }
}
