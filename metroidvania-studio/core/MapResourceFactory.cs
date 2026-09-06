using System;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    /// <summary>Creates validated catalog resources for painting and preview rendering.</summary>
    public static class MapResourceFactory
    {
        public static TerrainTileSet CreateTerrainMaterial(string assetId, string displayName,
            string themeId = null, string htmlColor = null)
        {
            if (string.IsNullOrWhiteSpace(assetId)) throw new ArgumentException("A material needs a stable asset ID.", nameof(assetId));
            var resource = new TerrainTileSet();
            resource.AssetId = assetId;
            resource.name = displayName ?? assetId;
            if (!string.IsNullOrWhiteSpace(themeId))
            {
                if (!ColorText.TryParseHtmlString(htmlColor, out Color color))
                    throw new ArgumentException("A terrain theme needs a valid HTML color.", nameof(htmlColor));
                resource.SetTheme(themeId, color);
            }
            return resource;
        }

        public static SpriteRegion CreateSprite(string assetId, long regionId, string name,
            TextureResource texture, Rect rect, Vector2 pivot, float pixelsPerUnit = MapDocument.RequiredTileSize)
        {
            if (string.IsNullOrWhiteSpace(assetId)) throw new ArgumentException("A sprite needs a stable asset ID.", nameof(assetId));
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            if (!float.IsFinite(pixelsPerUnit) || pixelsPerUnit <= 0 ||
                !float.IsFinite(rect.x) || !float.IsFinite(rect.y) || !float.IsFinite(rect.width) || !float.IsFinite(rect.height) ||
                rect.x < 0 || rect.y < 0 || rect.width <= 0 || rect.height <= 0 ||
                rect.xMax > texture.width || rect.yMax > texture.height ||
                !float.IsFinite(pivot.x) || !float.IsFinite(pivot.y))
                throw new ArgumentException("SpriteRegion geometry must be finite and inside its texture.");
            return new SpriteRegion { AssetId = assetId, RegionId = regionId, name = name ?? "",
                texture = texture, rect = rect, pivot = pivot, pixelsPerUnit = pixelsPerUnit };
        }
    }
}
