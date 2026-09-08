using System;
using UnityEngine;
namespace MetroidvaniaStudio.Integration
{
    public sealed class StudioImportedRoom : MonoBehaviour
    {
        public string roomId;
        [TextArea] public string documentJson;
        public StudioResourceLibrary resources;
        [NonSerialized] private StudioTriggerManager triggers;
        public StudioTriggerManager Triggers
        {
            get
            {
                if (triggers == null)
                {
                    var room = StudioRoomBuilder.Parse(documentJson).rooms.Find(value => value.id == roomId);
                    InitializeTriggers(room, new StudioTriggerManager());
                }
                return triggers;
            }
        }
        public void InitializeTriggers(MapRoom room, StudioTriggerManager manager)
        {
            if (manager == null) throw new ArgumentNullException(nameof(manager));
            manager.RegisterRoom(room);
            triggers = manager;
        }
    }
}
