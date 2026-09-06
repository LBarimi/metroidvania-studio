using System;
using System.Collections.Generic;
using System.Globalization;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public enum MapPlacementKind { Point, Rectangle, Nodes }
    public enum MapFieldKind { String, Integer, Number, Boolean, Color, Choice, Path }

    [Serializable]
    public sealed class MapFieldDefinition
    {
        public string key = "field", label = "Field";
        public MapFieldKind kind;
        public string defaultValue = "";
        public bool required;
        public double min = double.MinValue, max = double.MaxValue;
        public string[] choices = Array.Empty<string>();
    }

    public sealed class MapObjectDefinition
    {
        public string id = "custom", displayName = "Custom object", category = "Objects";
        public MapLayer layer = MapLayer.Entities;
        public MapPlacementKind placement;
        public Vector2 defaultSize = Vector2.one, minimumSize = Vector2.one;
        public bool resizable = true, rotatable = true, flippable = true;
        public int minimumNodes, maximumNodes;
        public SpriteRegion sprite;
        public Color color = Color.white;
        public List<MapFieldDefinition> fields = new List<MapFieldDefinition>();

        public bool SupportsLayer(MapLayer targetLayer) =>
            IsDecal(layer) ? IsDecal(targetLayer) : targetLayer == layer;

        public void ValidateDefinition()
        {
            Require(!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(displayName), "Object definitions need an ID and display name.");
            Require(category != null, "Object definition category cannot be null.");
            Require(layer >= MapLayer.Entities && layer <= MapLayer.BackgroundDecals, "Object definitions must use an entity, trigger, or decal layer.");
            Require(Enum.IsDefined(typeof(MapPlacementKind), placement), "Unknown object placement kind.");
            Require(Positive(defaultSize) && Positive(minimumSize) && defaultSize.x >= minimumSize.x && defaultSize.y >= minimumSize.y,
                "Default size must be finite, positive, and at least the minimum size.");
            Require(minimumNodes >= 0 && minimumNodes <= MapDocument.MaximumNodesPerObject
                && (maximumNodes == -1 || maximumNodes >= minimumNodes && maximumNodes <= MapDocument.MaximumNodesPerObject),
                "Invalid minimum/maximum node counts; -1 means unlimited up to the map safety limit.");
            Require(placement == MapPlacementKind.Nodes || (minimumNodes == 0 && maximumNodes == 0), "Only node placements can define nodes.");
            Require(Finite(color.r) && Finite(color.g) && Finite(color.b) && Finite(color.a), "Definition color must be finite.");
            Require(fields != null, "Object definition fields are missing.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (MapFieldDefinition field in fields)
            {
                Require(field != null && !string.IsNullOrWhiteSpace(field.key) && keys.Add(field.key), "Object field keys must be nonempty and unique.");
                Require(field.label != null && Enum.IsDefined(typeof(MapFieldKind), field.kind), "Object field label or kind is invalid.");
                Require(!double.IsNaN(field.min) && !double.IsInfinity(field.min) && !double.IsNaN(field.max) && !double.IsInfinity(field.max)
                    && field.min <= field.max, "Object field numeric bounds must be finite and ordered.");
                Require(field.choices != null, "Object field choices are missing.");
                if (field.kind == MapFieldKind.Choice)
                {
                    var choices = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string choice in field.choices) Require(!string.IsNullOrEmpty(choice) && choices.Add(choice), "Choice values must be nonempty and unique.");
                    Require(choices.Count > 0, "Choice fields need at least one value.");
                }
                NormalizeValue(field, field.defaultValue);
            }
        }

        public MapObject Create(Vector2 position, Vector2 size, MapLayer targetLayer, string groupId)
        {
            ValidateDefinition();
            Require(SupportsLayer(targetLayer), "Definition '" + id + "' cannot be placed on " + targetLayer + ".");
            Require(Finite(position.x) && Finite(position.y) && Finite(size.x) && Finite(size.y), "Object position and size must be finite.");
            Require(groupId != null, "Object group ID cannot be null.");
            Vector2 dimensions = resizable ? new Vector2(MathEx.Max(minimumSize.x, size.x), MathEx.Max(minimumSize.y, size.y)) : defaultSize;
            var item = new MapObject { definition = id, layer = targetLayer, groupId = groupId,
                x = position.x, y = position.y, width = dimensions.x, height = dimensions.y };
            foreach (MapFieldDefinition field in fields)
                item.properties.Add(new MapProperty { key = field.key, value = NormalizeValue(field, field.defaultValue) });
            for (int i = 0; i < minimumNodes; i++) item.nodes.Add(position + new Vector2(i + 1, 0));
            ValidateObject(item);
            return item;
        }

        /// <summary>Checks an object without changing its original definition spelling or custom properties.</summary>
        public void ValidateObject(MapObject item)
        {
            ValidateDefinition();
            Require(item != null, "Object data is missing.");
            Require(!string.IsNullOrWhiteSpace(item.id) && string.Equals(item.definition, id, StringComparison.OrdinalIgnoreCase), "Object uses a different or missing definition ID.");
            Require(SupportsLayer(item.layer), "Object '" + item.id + "' is on a layer not supported by its definition.");
            Require(item.groupId != null && Finite(item.x) && Finite(item.y) && Finite(item.width) && Finite(item.height)
                && Finite(item.rotation) && Finite(item.scaleX) && Finite(item.scaleY) && item.scaleX != 0 && item.scaleY != 0,
                "Object transform must be finite and have nonzero scale.");
            Require(item.width >= minimumSize.x && item.height >= minimumSize.y, "Object size is below the definition minimum.");
            Require(resizable || (MathEx.Approximately(item.width, defaultSize.x) && MathEx.Approximately(item.height, defaultSize.y)), "This object definition does not support resizing.");
            Require(rotatable || MathEx.Approximately(MathEx.DeltaAngle(0, item.rotation), 0), "This object definition does not support rotation.");
            Require(flippable || (item.scaleX > 0 && item.scaleY > 0), "This object definition does not support flipping.");
            Require(item.nodes != null && item.nodes.Count >= minimumNodes && (maximumNodes < 0 || item.nodes.Count <= maximumNodes), "Object node count is outside the definition limits.");
            foreach (Vector2 node in item.nodes) Require(Finite(node.x) && Finite(node.y), "Object nodes must be finite.");
            Require(item.properties != null, "Object properties are missing.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (MapProperty property in item.properties)
            {
                Require(property != null && !string.IsNullOrWhiteSpace(property.key) && property.value != null && !values.ContainsKey(property.key), "Object properties need unique keys and non-null values.");
                values.Add(property.key, property.value);
            }
            foreach (MapFieldDefinition field in fields)
            {
                if (values.TryGetValue(field.key, out string value)) NormalizeValue(field, value);
                else Require(!field.required, "Required object field '" + field.key + "' is missing.");
            }
        }

        public static string NormalizeValue(MapFieldDefinition field, string value)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            value = value ?? "";
            if (string.IsNullOrWhiteSpace(value))
            {
                Require(!field.required, "Field '" + field.key + "' is required.");
                return "";
            }
            switch (field.kind)
            {
                case MapFieldKind.String: return value;
                case MapFieldKind.Path: return value.Trim().Replace('\\', '/');
                case MapFieldKind.Integer:
                    Require(long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer)
                        && integer >= field.min && integer <= field.max, "Field '" + field.key + "' needs an integer within its allowed range.");
                    return integer.ToString(CultureInfo.InvariantCulture);
                case MapFieldKind.Number:
                    Require(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                        && !double.IsNaN(number) && !double.IsInfinity(number) && number >= field.min && number <= field.max,
                        "Field '" + field.key + "' needs a finite number within its allowed range.");
                    return number.ToString("R", CultureInfo.InvariantCulture);
                case MapFieldKind.Boolean:
                    Require(bool.TryParse(value.Trim(), out bool flag), "Field '" + field.key + "' needs true or false.");
                    return flag ? "true" : "false";
                case MapFieldKind.Color:
                    Require(ColorText.TryParseHtmlString(value.Trim(), out Color parsed), "Field '" + field.key + "' needs a valid HTML color.");
                    return "#" + ColorText.ToHtmlStringRGBA(parsed);
                case MapFieldKind.Choice:
                    Require(field.choices != null && Array.IndexOf(field.choices, value) >= 0, "Field '" + field.key + "' needs one of the defined choices.");
                    return value;
                default: throw new InvalidOperationException("Unknown object field kind.");
            }
        }

        private static bool IsDecal(MapLayer value) => value == MapLayer.ForegroundDecals || value == MapLayer.BackgroundDecals;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Positive(Vector2 value) => Finite(value.x) && Finite(value.y) && value.x > 0 && value.y > 0;
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
