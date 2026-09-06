using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MetroidvaniaStudio
{
    /// <summary>Optional field schema for a named background effect. Contains no executable effect.</summary>
    public sealed class MapStylegroundEffectDefinition
    {
        public string TypeId { get; internal set; }
        public string DisplayName { get; internal set; }
        public IReadOnlyList<MapFieldDefinition> Fields { get; internal set; }
        public bool RequiresTexture { get; internal set; }

        public IReadOnlyDictionary<string, string> ReadProperties(MapStyleground style)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (MapProperty property in style.properties) result.Add(property.key, property.value);
            foreach (MapFieldDefinition field in Fields)
                result[field.key] = MapObjectDefinition.NormalizeValue(field,
                    result.TryGetValue(field.key, out string value) ? value : field.defaultValue);
            return new ReadOnlyDictionary<string, string>(result);
        }

        public void Validate(MapStyleground style)
        {
            ReadProperties(style);
            if (RequiresTexture && string.IsNullOrWhiteSpace(style.texture))
                throw new InvalidOperationException("This effect schema requires a texture reference.");
        }
    }

    /// <summary>Explicit data schemas. Unknown effect IDs remain opaque JSON and are never executed.</summary>
    public static class MapStylegroundEffects
    {
        private static readonly Dictionary<string, MapStylegroundEffectDefinition> definitions =
            new Dictionary<string, MapStylegroundEffectDefinition>(StringComparer.Ordinal);

        public static IReadOnlyList<MapStylegroundEffectDefinition> Entries =>
            definitions.Values.OrderBy(item => item.DisplayName, StringComparer.Ordinal).ToArray();

        public static MapStylegroundEffectDefinition Resolve(string typeId) =>
            typeId != null && definitions.TryGetValue(typeId, out var result) ? result : null;

        public static void Register(string typeId, string displayName,
            IEnumerable<MapFieldDefinition> fields, bool requiresTexture = false)
        {
            if (string.IsNullOrWhiteSpace(typeId) || typeId == "parallax" || typeId == "group"
                || string.IsNullOrWhiteSpace(displayName) || fields == null)
                throw new ArgumentException("An effect schema needs a unique nonempty ID, name and field collection.");
            if (definitions.ContainsKey(typeId)) throw new InvalidOperationException("Duplicate effect schema ID: " + typeId);
            var snapshot = new List<MapFieldDefinition>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (MapFieldDefinition source in fields)
            {
                if (source == null || string.IsNullOrWhiteSpace(source.key) || !keys.Add(source.key)
                    || MapStylegroundEditing.IsCommonProperty(source.key))
                    throw new ArgumentException("Effect fields need unique keys and cannot replace common properties.");
                var field = MapJson.FromJson<MapFieldDefinition>(MapJson.ToJson(source));
                if (field.label == null || !Enum.IsDefined(typeof(MapFieldKind), field.kind) || field.choices == null
                    || !double.IsFinite(field.min) || !double.IsFinite(field.max) || field.min > field.max)
                    throw new ArgumentException("Invalid effect field: " + field.key);
                if (field.kind == MapFieldKind.Choice && (field.choices.Length == 0
                    || field.choices.Any(string.IsNullOrEmpty)
                    || field.choices.Distinct(StringComparer.Ordinal).Count() != field.choices.Length))
                    throw new ArgumentException("Choice fields need unique nonempty choices.");
                MapObjectDefinition.NormalizeValue(field, field.defaultValue);
                snapshot.Add(field);
            }
            definitions.Add(typeId, new MapStylegroundEffectDefinition
            {
                TypeId = typeId, DisplayName = displayName, Fields = snapshot.AsReadOnly(), RequiresTexture = requiresTexture
            });
        }
    }
}
