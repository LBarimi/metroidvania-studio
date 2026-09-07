using UnityEngine;

namespace MetroidvaniaStudio.Integration
{
    [ExecuteAlways, RequireComponent(typeof(Camera)), DisallowMultipleComponent]
    public sealed class StudioPixelCamera : MonoBehaviour
    {
        [Min(1)] public int ppu = 16;
        [Min(1)] public int referenceWidth = 320, referenceHeight = 180;
        private Camera target;
        private Rect previousRect;
        private void OnEnable() { target = GetComponent<Camera>(); previousRect = target.rect; Apply(); }
        private void OnDisable() { if (target) target.rect = previousRect; }
        private void LateUpdate()
        {
            Apply();
            // Align source-pixel edges even when the reference resolution is odd.
            Vector3 position = transform.position; float pixels = Mathf.Max(1, ppu);
            position.x = (Mathf.Round(position.x * pixels - referenceWidth * .5f) + referenceWidth * .5f) / pixels;
            position.y = (Mathf.Round(position.y * pixels - referenceHeight * .5f) + referenceHeight * .5f) / pixels;
            transform.position = position;
        }
        public void Configure(CameraProfile profile)
        {
            ppu = profile.ppu; referenceWidth = profile.referenceWidth; referenceHeight = profile.referenceHeight; Apply();
        }
        public void Apply()
        {
            if (!target) target = GetComponent<Camera>();
            target.orthographic = true;
            target.orthographicSize = Mathf.Max(1, referenceHeight) / (2f * Mathf.Max(1, ppu));
            int width = target.targetTexture ? target.targetTexture.width : Screen.width;
            int height = target.targetTexture ? target.targetTexture.height : Screen.height;
            if (width < 1 || height < 1) return;
            float fit = Mathf.Min(width / (float)Mathf.Max(1, referenceWidth), height / (float)Mathf.Max(1, referenceHeight));
            float scale = fit >= 1 ? Mathf.Floor(fit) : 1f / Mathf.Ceil(1f / Mathf.Max(.00001f, fit));
            float w = referenceWidth * scale / width, h = referenceHeight * scale / height;
            target.rect = new Rect(Mathf.Floor((width - referenceWidth * scale) * .5f) / width, Mathf.Floor((height - referenceHeight * scale) * .5f) / height, w, h);
        }
    }
}
