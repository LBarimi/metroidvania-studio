using System;
using System.Collections.Generic;

namespace MetroidvaniaStudio
{
    public sealed class StudioTriggerInfo
    {
        public string RoomId { get; }
        public string RoomName { get; }
        public int RoomX { get; }
        public int RoomY { get; }
        public string ObjectId { get; }
        public string Definition { get; }
        public StudioTriggerEvent Event { get; }
        public bool Once { get; }
        public string Description { get; }
        public StudioTriggerInfo(string roomId, string roomName, int roomX, int roomY, string objectId, string definition,
            StudioTriggerEvent triggerEvent, bool once, string description)
        {
            RoomId = roomId; RoomName = roomName; RoomX = roomX; RoomY = roomY; ObjectId = objectId;
            Definition = definition; Event = triggerEvent; Once = once; Description = description;
        }
    }

    /// <summary>Explicit trigger requests for a loaded world. No physics or gameplay callbacks are installed.</summary>
    public sealed class StudioTriggerManager
    {
        private readonly Dictionary<string, StudioTriggerInfo> entries = new Dictionary<string, StudioTriggerInfo>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> owners = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
        public event Action<StudioTriggerInfo> TriggerRequested;

        public void RegisterRoom(MapRoom room)
        {
            if (room == null || string.IsNullOrWhiteSpace(room.id) || room.objects == null) throw new ArgumentException("Invalid trigger room.");
            var nextOwners = new HashSet<string>(StringComparer.Ordinal);
            var nextEntries = new Dictionary<string, StudioTriggerInfo>(StringComparer.Ordinal);
            foreach (MapObject item in room.objects)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.id) || item.properties == null || !nextOwners.Add(item.id)
                    || owners.TryGetValue(item.id, out string owner) && owner != room.id)
                    throw new ArgumentException("Placed object IDs must be nonempty and unique across registered rooms.");
                bool portal = string.Equals(item.definition, "Portal", StringComparison.OrdinalIgnoreCase);
                if (portal || string.Equals(item.definition, "InvisibleWall", StringComparison.OrdinalIgnoreCase) || item.layer != MapLayer.Triggers) continue;
                string Value(string key) => item.properties.Find(p => p != null && p.key == key)?.value ?? "";
                StudioTriggerEvents.TryParse(Value("event"), out StudioTriggerEvent triggerEvent);
                bool once = bool.TryParse(Value("once"), out bool value) && value;
                nextEntries.Add(item.id, new StudioTriggerInfo(room.id, room.name ?? "", room.x, room.y, item.id,
                    item.definition ?? "", triggerEvent, once, Value("desc")));
            }
            UnregisterRoom(room.id);
            foreach (string id in nextOwners) owners.Add(id, room.id);
            foreach (var entry in nextEntries) entries.Add(entry.Key, entry.Value);
        }

        public void UnregisterRoom(string roomId)
        {
            var remove = new List<string>();
            foreach (var item in owners) if (item.Value == roomId) remove.Add(item.Key);
            foreach (string id in remove) { owners.Remove(id); entries.Remove(id); }
            // Re-entering a room does not reactivate consumed one-shot objects.
        }

        public bool TryGet(string objectId, out StudioTriggerInfo info)
        {
            info = null;
            return objectId != null && entries.TryGetValue(objectId, out info);
        }

        public bool TryRequest(string objectId, out StudioTriggerInfo info)
        {
            if (!TryGet(objectId, out info) || info.Event == StudioTriggerEvent.None || info.Once && !consumed.Add(objectId))
            { info = null; return false; }
            // Consume before delivery so a callback cannot recursively activate the same one-shot object.
            TriggerRequested?.Invoke(info);
            return true;
        }

        public bool ResetOnce(string objectId) => objectId != null && consumed.Remove(objectId);
        public void ResetAllOnce() => consumed.Clear();
        public void Clear() { entries.Clear(); owners.Clear(); consumed.Clear(); }
    }
}
