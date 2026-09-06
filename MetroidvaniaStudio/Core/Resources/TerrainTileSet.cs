using System;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public sealed class TerrainTileSet
    {
        public string AssetId { get; set; } = "";
        public string name = "";
        private SpriteRegion[] solidSprites = new SpriteRegion[256];

        private SpriteRegion[] slopeSprites = new SpriteRegion[4];


        private string themeId = "";
        private Color themeColor = Color.white;

        public string ThemeId => themeId ?? "";
        public Color ThemeColor => themeColor;
        public bool HasTheme => !string.IsNullOrEmpty(themeId);

        /// <summary>Optional brush metadata. Existing tile sets keep their unthemed behavior.</summary>
        public void SetTheme(string id, Color color)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A terrain theme needs a stable ID.", nameof(id));
            if (!Finite(color.r) || !Finite(color.g) || !Finite(color.b) || !Finite(color.a))
                throw new ArgumentException("Terrain theme color must be finite.", nameof(color));
            themeId = id;
            themeColor = new Color(MathEx.Clamp01(color.r), MathEx.Clamp01(color.g), MathEx.Clamp01(color.b), 1);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public SpriteRegion GetSprite(int mask, TileShape shape)
        {
            SpriteRegion sprite = GetAssignedSprite(mask, shape);
            return sprite == null && shape == TileShape.Solid ? GetAssignedSprite(0, TileShape.Solid) : sprite;
        }

        /// <summary>The exact assigned slot, without the rendering fallback for incomplete tile sets.</summary>
        public SpriteRegion GetAssignedSprite(int mask, TileShape shape)
        {
            ValidateShape(shape);
            if (shape != TileShape.Solid)
            {
                int index = (int)shape - 1;
                return slopeSprites != null && index < slopeSprites.Length ? slopeSprites[index] : null;
            }

            mask = TileMask.Normalize(mask);
            if (solidSprites == null || solidSprites.Length == 0)
                return null;

            return mask < solidSprites.Length ? solidSprites[mask] : null;
        }

        public void SetSprite(int mask, SpriteRegion sprite)
        {
            if (mask < 0 || mask > 255)
                throw new ArgumentOutOfRangeException(nameof(mask), mask, "A tile mask must be between 0 and 255.");
            EnsureSize(ref solidSprites, 256);
            solidSprites[TileMask.Normalize(mask)] = sprite;
        }

        public void SetSlopeSprite(TileShape shape, SpriteRegion sprite)
        {
            ValidateShape(shape);
            if (shape == TileShape.Solid)
                throw new ArgumentException("Use SetSprite for a solid tile.", nameof(shape));
            EnsureSize(ref slopeSprites, 4);
            slopeSprites[(int)shape - 1] = sprite;
        }

        private static void ValidateShape(TileShape shape)
        {
            if (shape < TileShape.Solid || shape > TileShape.TopRight)
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown tile shape.");
        }

        private static void EnsureSize<T>(ref T[] array, int size)
        {
            if (array == null || array.Length != size)
                Array.Resize(ref array, size);
        }
    }
}
