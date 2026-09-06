using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    /// <summary>Texture dimensions and an opaque catalog identity. SourcePath is workspace relative.</summary>
    public sealed class TextureResource
    {
        public string AssetId { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string name = "";
        public int width, height;
    }

    /// <summary>A rectangular atlas region with a pixel-space pivot.</summary>
    public sealed class SpriteRegion
    {
        public string AssetId { get; set; } = "";
        public long RegionId { get; set; }
        public string name = "";
        public TextureResource texture;
        public Rect rect;
        public Vector2 pivot;
        public float pixelsPerUnit = MapDocument.RequiredTileSize;
        public Rect textureRect => rect;
    }
}
