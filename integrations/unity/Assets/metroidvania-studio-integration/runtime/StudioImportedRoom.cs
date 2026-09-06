using UnityEngine;
namespace MetroidvaniaStudio.Integration
{
    public sealed class StudioImportedRoom : MonoBehaviour
    {
        public string roomId;
        [TextArea] public string documentJson;
        public StudioResourceLibrary resources;
    }
}
