using UnityEngine;
namespace MetroidvaniaStudio.Integration
{
    /// <summary>Authored data; the consuming game owns portal contact handling and trigger event requests.</summary>
    public sealed class StudioPlacedObject : MonoBehaviour
    {
        public MapObject data;
        public bool IsPortal => data != null && string.Equals(data.definition, "Portal", System.StringComparison.OrdinalIgnoreCase);
        public bool IsInvisibleWall => data != null && string.Equals(data.definition, "InvisibleWall", System.StringComparison.OrdinalIgnoreCase);
        public bool TryRequestTrigger(out StudioTriggerInfo info)
        {
            info = null;
            var room = GetComponentInParent<StudioImportedRoom>();
            return room && data != null && room.Triggers.TryRequest(data.id, out info);
        }
    }
}
