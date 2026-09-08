using UnityEngine;
namespace MetroidvaniaStudio.Integration
{
    /// <summary>Authored data; the consuming game decides when to request an event.</summary>
    public sealed class StudioPlacedObject : MonoBehaviour
    {
        public MapObject data;
        public bool TryRequestTrigger(out StudioTriggerInfo info)
        {
            info = null;
            var room = GetComponentInParent<StudioImportedRoom>();
            return room && data != null && room.Triggers.TryRequest(data.id, out info);
        }
    }
}
