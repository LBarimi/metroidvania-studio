using System;
using System.Globalization;

namespace MetroidvaniaStudio
{
    /// <summary>Camera units are separate from the document's fixed source-pixel tile grid.</summary>
    public static class MapCameraSettings
    {
        public const string PpuKey = "metroidvaniaStudio.camera.ppu";
        public const string WidthKey = "metroidvaniaStudio.camera.width";
        public const string HeightKey = "metroidvaniaStudio.camera.height";
        public const int MaximumPpu = 8192;
        public const int MaximumResolution = 16384;

        public static CameraProfile Resolve(MapDocument document, CameraProfile fallback = null)
        {
            fallback = fallback ?? new CameraProfile();
            int ppu = Read(document, PpuKey, fallback.ppu, MaximumPpu);
            int width = Read(document, WidthKey, fallback.referenceWidth, MaximumResolution);
            int height = Read(document, HeightKey, fallback.referenceHeight, MaximumResolution);
            return new CameraProfile { ppu = ppu, referenceWidth = width, referenceHeight = height,
                orthographicSize = height / (2f * ppu), x = fallback.x, y = fallback.y };
        }

        public static void Apply(MapDocument document, int ppu, int width, int height)
        {
            Check(ppu, MaximumPpu, "PPU"); Check(width, MaximumResolution, "Width"); Check(height, MaximumResolution, "Height");
            Set(document, PpuKey, ppu); Set(document, WidthKey, width); Set(document, HeightKey, height);
        }

        private static int Read(MapDocument document, string key, int fallback, int maximum)
        {
            foreach (MapProperty property in document.properties)
                if (property.key == key)
                {
                    if (!int.TryParse(property.value, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                        throw new InvalidOperationException("Camera settings require positive integers.");
                    Check(value, maximum, key); return value;
                }
            // Legacy catalogs can have a larger reference resolution than the editable settings.
            return Math.Max(1, Math.Min(maximum, fallback));
        }
        private static void Set(MapDocument document, string key, int value)
        {
            string text = value.ToString(CultureInfo.InvariantCulture);
            foreach (MapProperty property in document.properties)
                if (property.key == key) { property.value = text; return; }
            document.properties.Add(new MapProperty { key = key, value = text });
        }
        private static void Check(int value, int maximum, string name)
        {
            if (value < 1 || value > maximum) throw new InvalidOperationException(name + " must be between 1 and " + maximum + ".");
        }
    }
}
