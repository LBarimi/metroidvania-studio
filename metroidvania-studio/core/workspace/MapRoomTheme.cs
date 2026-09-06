using System;
using System.Collections.Generic;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    /// <summary>The last terrain brush used in a room controls its minimap fill, independently of its canvas color.</summary>
    public static class MapRoomTheme
    {
        // Persisted v2 map keys retain their original spelling so existing room themes remain readable.
        public const string ColorProperty = "mapMaker.minimapColor";
        public const string ThemeProperty = "mapMaker.terrainTheme";

        /// <summary>Call only inside the terrain edit transaction, after at least one editable cell was painted.</summary>
        public static void Apply(MapRoom room, TerrainTileSet tileSet)
        {
            if (room == null) throw new ArgumentNullException(nameof(room));
            if (tileSet == null || !tileSet.HasTheme) return;
            if (room.properties == null) room.properties = new List<MapProperty>();
            Set(room, ThemeProperty, tileSet.ThemeId);
            Set(room, ColorProperty, "#" + ColorText.ToHtmlStringRGB(tileSet.ThemeColor));
        }

        /// <summary>Serialized color remains usable after resource reloads, moves, or missing tile-set assets.</summary>
        public static bool TryGetColor(MapRoom room, out Color color)
        {
            color = default;
            MapProperty property = room?.properties?.Find(item => item != null && item.key == ColorProperty);
            return property != null && ColorText.TryParseHtmlString(property.value, out color);
        }

        private static void Set(MapRoom room, string key, string value)
        {
            MapProperty property = room.properties.Find(item => item.key == key);
            if (property == null) room.properties.Add(new MapProperty { key = key, value = value });
            else property.value = value;
        }
    }
}
