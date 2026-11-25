using System;
using System.Linq;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public static class MapMinimapShape
    {
        public const string ShapeProperty = "mapMaker.minimapShape";
        public const string RegionColorProperty = "mapMaker.minimapRegionColor";

        // Rows run from the room's bottom edge upward, with one digit per drawn cell.
        internal static void Apply(MapRoom room, string shape, string color)
        {
            if (shape == null && color == null) return;
            if (color == null || color.Length != 7 || color[0] != '#' || !ColorText.TryParseHtmlString(color, out _))
                throw new ArgumentException("Invalid minimap region color.");
            if (shape != null)
            {
                if (shape.Length != room.height * (room.width + 1) - 1)
                    throw new ArgumentException("Invalid minimap shape dimensions.");
                string[] rows = ReadRows(shape);
                if (rows == null || rows.Length != room.height || rows[0].Length != room.width)
                    throw new ArgumentException("Invalid minimap shape cells.");
                room.properties.Add(new MapProperty { key = ShapeProperty, value = shape });
            }
            room.properties.Add(new MapProperty { key = RegionColorProperty, value = color });
        }

        internal static void Transform(MapRoom room, bool horizontal, bool rotate, bool clockwise)
        {
            MapProperty property = room.properties.Find(p => p.key == ShapeProperty);
            if (property == null) return;
            string[] rows = ReadRows(property.value);
            if (rows == null) return;
            int width = rows[0].Length, height = rows.Length;
            var transformed = new char[rotate ? width : height][];
            for (int y = 0; y < transformed.Length; y++) transformed[y] = new char[rotate ? height : width];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int tx = rotate ? (clockwise ? y : height - 1 - y) : (horizontal ? width - 1 - x : x);
                int ty = rotate ? (clockwise ? width - 1 - x : x) : (horizontal ? y : height - 1 - y);
                transformed[ty][tx] = rows[y][x];
            }
            property.value = string.Join("/", transformed.Select(row => new string(row)));
        }

        private static string[] ReadRows(string value)
        {
            int maximum = MapDocument.MaximumRoomDimension;
            if (string.IsNullOrEmpty(value) || value.Length > maximum * (maximum + 1) - 1) return null;
            string[] rows = value.Split('/');
            int width = rows[0].Length;
            if (width < 1 || width > maximum || rows.Length > maximum) return null;
            bool filled = false;
            foreach (string row in rows)
            {
                if (row.Length != width) return null;
                foreach (char cell in row)
                {
                    if (cell != '0' && cell != '1') return null;
                    filled |= cell == '1';
                }
            }
            return filled ? rows : null;
        }
    }
}
