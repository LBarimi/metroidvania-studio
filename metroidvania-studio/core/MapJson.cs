using System;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MetroidvaniaStudio
{
    /// <summary>Field-based map JSON. Uses cached typed accessors to avoid per-cell boxing.</summary>
    public static class MapJson
    {
        private static readonly JsonSerializerOptions Compact = CreateOptions(false);
        private static readonly JsonSerializerOptions Pretty = CreateOptions(true);

        private static JsonSerializerOptions CreateOptions(bool pretty)
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(info =>
            {
                if (info.Kind != JsonTypeInfoKind.Object) return;
                // Keep the framework's compiled field getters and standard JSON attributes.
                // Calculated properties are not part of the persistent map contract.
                for (int index = info.Properties.Count - 1; index >= 0; index--)
                {
                    if (info.Properties[index].AttributeProvider is not FieldInfo field
                        || !field.IsPublic || field.IsStatic || field.IsInitOnly
                        || field.IsDefined(typeof(NonSerializedAttribute))) info.Properties.RemoveAt(index);
                }
                if (!info.Type.IsAbstract && info.CreateObject == null)
                    info.CreateObject = () => Activator.CreateInstance(info.Type, true);
            });
            return new JsonSerializerOptions
            {
                // System.Text.Json numbers use the JSON format independently of CurrentCulture.
                TypeInfoResolver = resolver, IncludeFields = true, WriteIndented = pretty,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNameCaseInsensitive = false, MaxDepth = 64
            };
        }

        public static string ToJson(object value, bool prettyPrint = false)
        {
            if (value == null) return "null";
            try { return JsonSerializer.Serialize(value, value.GetType(), prettyPrint ? Pretty : Compact); }
            catch (JsonException error) { throw new ArgumentException("Cannot serialize JSON: " + error.Message, nameof(value), error); }
        }

        public static T FromJson<T>(string json) => (T)FromJson(json, typeof(T));
        public static object FromJson(string json, Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            try { return JsonSerializer.Deserialize(json, type, Compact); }
            catch (JsonException error) { throw new ArgumentException("Invalid JSON: " + error.Message, nameof(json), error); }
        }
    }
}
