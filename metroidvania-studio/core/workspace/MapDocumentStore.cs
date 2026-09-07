using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public static class MapDocumentStore
    {
        public const long MaximumFileBytes = 32L * 1024 * 1024;
        private const int ErrorUnableToRemoveReplaced = 1175;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        [Serializable]
        private sealed class FormatHeader
        {
            public int formatVersion = 0;
            public int tileSize = 0;
        }

        public static string Serialize(MapDocument document, bool prettyPrint = false)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            document.Validate();
            string json = MapJson.ToJson(document, prettyPrint);
            ValidateSnapshotSize(json);
            return json;
        }

        public static MapDocument Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("Map file is empty.");
            if (Utf8.GetByteCount(json) > MaximumFileBytes) throw new InvalidDataException("Map file exceeds the 32 MiB limit.");
            try
            {
                var header = MapJson.FromJson<FormatHeader>(json);
                if (header == null || (header.formatVersion != 1 && header.formatVersion != MapDocument.CurrentFormatVersion))
                    throw new InvalidDataException("Unsupported or missing map format version. This editor supports version " + MapDocument.CurrentFormatVersion + ".");
                if (header.tileSize != MapDocument.RequiredTileSize)
                    throw new InvalidDataException("Map tiles must be exactly 16 pixels.");

                // MapJson ignores unknown fields and accepts some malformed values.
                // Check the shape first so opening and saving cannot silently lose data.
                new JsonShapeValidator(json, header.formatVersion).Validate();
                MapDocument document = MapJson.FromJson<MapDocument>(json);
                if (header.formatVersion == 1) MigrateVersionOne(document);
                document.Validate();
                return document;
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Invalid map JSON: " + exception.Message, exception);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidDataException(exception.Message, exception);
            }
        }

        private static void MigrateVersionOne(MapDocument document)
        {
            document.formatVersion = MapDocument.CurrentFormatVersion;
            document.layerGroups = new List<MapLayerGroup>();
            if (document.rooms == null) return;
            foreach (MapRoom room in document.rooms)
            {
                if (room == null) continue;
                if (room.foreground != null)
                    foreach (MapCell cell in room.foreground)
                        if (cell != null) cell.groupId = "";
                if (room.background != null)
                    foreach (MapCell cell in room.background)
                        if (cell != null) cell.groupId = "";
                if (room.objects != null)
                    foreach (MapObject item in room.objects)
                        if (item != null) item.groupId = "";
            }
        }

        public static MapDocument Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a map file path.", nameof(path));
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Map file exceeds the 32 MiB limit.");
                    return Deserialize(ReadUtf8(stream));
                }
            }
            catch (DecoderFallbackException exception)
            { throw new InvalidDataException("Map file is not valid UTF-8.", exception); }
        }

        private static string ReadUtf8(Stream stream)
        {
            // StreamReader's BOM auto-detection also accepts UTF-16 and UTF-32,
            // which violates the map format contract. Decode strictly as UTF-8
            // and consume only the optional UTF-8 BOM character.
            using (var reader = new StreamReader(stream, Utf8, false))
            {
                if (reader.Peek() == '\uFEFF') reader.Read();
                return reader.ReadToEnd();
            }
        }

        public static void Save(MapDocument document, string path) => Save(path, document);

        public static void Save(string path, MapDocument document)
            => SaveSnapshot(path, document);

        /// <summary>Writes a validated document and returns the exact UTF-8 text persisted.</summary>
        public static string SaveSnapshot(string path, MapDocument document)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a map file path.", nameof(path));
            string json = Serialize(document);
            WriteValidatedSnapshot(path, json);
            return json;
        }

        /// <summary>
        /// Atomically writes JSON that was produced by <see cref="Serialize(MapDocument, bool)"/>.
        /// This is used for hot recovery snapshots so they are not parsed and materialized a
        /// second time after the same in-memory document was already validated.
        /// </summary>
        public static void SaveValidatedSnapshot(string path, string json)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a map file path.", nameof(path));
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("A validated map snapshot is required.", nameof(json));
            WriteValidatedSnapshot(path, json);
        }

        private static void WriteValidatedSnapshot(string path, string json)
        {
            ValidateSnapshotSize(json);
            string destination = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(destination);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Map folder does not exist: " + directory);
            string temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    // Stream the cached JSON instead of allocating a second document-sized
                    // byte array for every autosave/recovery snapshot.
                    using (var writer = new StreamWriter(stream, Utf8, 16384, true))
                    {
                        writer.Write(json);
                        writer.Flush();
                    }
                    stream.Flush(true);
                }
                // A same-directory replacement is atomic. Never delete a previous save
                // as a fallback when replacement is unsupported or denied.
                if (File.Exists(destination))
                    ReplaceFile(temporary, destination);
                else
                    File.Move(temporary, destination);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void ReplaceFile(string replacement, string destination)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { File.Replace(replacement, destination, null); return; }
                catch (IOException error) when (attempt < 2
                    && (error.HResult & 0xffff) == ErrorUnableToRemoveReplaced
                    && File.Exists(replacement) && File.Exists(destination))
                {
                    // Win32 explicitly guarantees that error 1175 leaves both
                    // names intact, so this one transient failure is safe to
                    // retry. Other I/O errors retain their original behavior.
                    System.Threading.Thread.Sleep(attempt == 0 ? 5 : 15);
                }
            }
        }

        private static void ValidateSnapshotSize(string json)
        {
            if (Utf8.GetByteCount(json) > MaximumFileBytes)
                throw new InvalidDataException("Map file exceeds the 32 MiB limit.");
        }

        /// <summary>Checks JSON syntax and known field types without creating a second object model.</summary>
        private sealed class JsonShapeValidator
        {
            private readonly string text;
            private readonly Dictionary<Type, Dictionary<string, Type>> fields = new Dictionary<Type, Dictionary<string, Type>>();
            private int position;
            private readonly int formatVersion;

            public JsonShapeValidator(string text, int formatVersion) { this.text = text; this.formatVersion = formatVersion; }

            public void Validate()
            {
                Value(typeof(MapDocument), 0);
                Space();
                if (position != text.Length) Fail("Unexpected text after the map document");
            }

            private void Value(Type type, int depth)
            {
                if (depth > 32) Fail("Map JSON nesting is too deep");
                Space();
                if (position >= text.Length) Fail("Unexpected end of JSON");
                if (!type.IsValueType && Peek('n'))
                {
                    // MapJson can turn explicit null lists/strings into initialized
                    // defaults. Reject them before deserialization can conceal malformed data.
                    Fail("Map fields and list members cannot be null");
                }
                if (type == typeof(string)) { String(); return; }
                if (type == typeof(bool))
                {
                    if (Peek('t')) Literal("true"); else Literal("false");
                    return;
                }
                if (type == typeof(int) || type == typeof(float) || type.IsEnum)
                {
                    string number = Number();
                    if (type == typeof(float))
                    {
                        if (!float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                            || float.IsNaN(value) || float.IsInfinity(value)) Fail("Invalid finite number");
                    }
                    else if (!int.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                        Fail("Expected a 32-bit integer");
                    return;
                }
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                {
                    Expect('[');
                    if (Take(']')) return;
                    do { Value(type.GetGenericArguments()[0], depth + 1); } while (Take(','));
                    Expect(']');
                    return;
                }

                Expect('{');
                var seen = new HashSet<string>(StringComparer.Ordinal);
                Dictionary<string, Type> members = Members(type);
                if (!Take('}'))
                {
                    do
                    {
                        string key = String();
                        if (!seen.Add(key)) Fail("Duplicate JSON field '" + key + "'");
                        if (!members.TryGetValue(key, out Type fieldType))
                            Fail("Unknown field '" + key + "' in " + type.Name + "; use the properties list for custom data");
                        if (formatVersion == 1 && ((type == typeof(MapDocument) && key == "layerGroups")
                            || ((type == typeof(MapCell) || type == typeof(MapObject)) && key == "groupId")))
                            Fail("Layer groups require map format version 2");
                        Expect(':');
                        Value(fieldType, depth + 1);
                    } while (Take(','));
                    Expect('}');
                }
                if (type == typeof(MapDocument) && (!seen.Contains("formatVersion") || !seen.Contains("tileSize") || !seen.Contains("rooms")))
                    Fail("Map document requires formatVersion, tileSize, and rooms");
                if (formatVersion == 2 && type == typeof(MapDocument) && !seen.Contains("layerGroups"))
                    Fail("Map format version 2 requires layerGroups");
                if ((type == typeof(MapRoom) || type == typeof(MapObject) || type == typeof(MapStyleground) || type == typeof(MapLayerGroup)) && !seen.Contains("id"))
                    Fail(type.Name + " requires a stable ID");
            }

            private Dictionary<string, Type> Members(Type type)
            {
                if (fields.TryGetValue(type, out var result)) return result;
                result = new Dictionary<string, Type>(StringComparer.Ordinal);
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                    result.Add(field.Name, field.FieldType);
                fields.Add(type, result);
                return result;
            }

            private string String()
            {
                Expect('"');
                var value = new StringBuilder();
                while (position < text.Length)
                {
                    char character = text[position++];
                    if (character == '"') return value.ToString();
                    if (character < 32) Fail("Unescaped control character in JSON string");
                    if (character != '\\') { value.Append(character); continue; }
                    if (position == text.Length) Fail("Incomplete JSON string escape");
                    char escaped = text[position++];
                    switch (escaped)
                    {
                        case '"': case '\\': case '/': value.Append(escaped); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'u':
                            if (position + 4 > text.Length || !ushort.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier,
                                CultureInfo.InvariantCulture, out ushort code)) Fail("Invalid Unicode escape");
                            else { value.Append((char)code); position += 4; }
                            break;
                        default: Fail("Invalid JSON string escape"); break;
                    }
                }
                Fail("Unterminated JSON string");
                return null;
            }

            private string Number()
            {
                Space();
                int start = position;
                if (Peek('-')) position++;
                if (Peek('0')) position++;
                else Digits();
                if (Peek('.')) { position++; Digits(); }
                if (Peek('e') || Peek('E'))
                {
                    position++;
                    if (Peek('+') || Peek('-')) position++;
                    Digits();
                }
                return text.Substring(start, position - start);
            }

            private void Digits()
            {
                int start = position;
                while (position < text.Length && text[position] >= '0' && text[position] <= '9') position++;
                if (start == position) Fail("Expected a JSON number");
            }

            private void Literal(string expected)
            {
                if (position + expected.Length > text.Length || string.CompareOrdinal(text, position, expected, 0, expected.Length) != 0)
                    Fail("Expected '" + expected + "'");
                position += expected.Length;
            }

            private bool Peek(char character) => position < text.Length && text[position] == character;
            private bool Take(char character)
            {
                Space();
                if (!Peek(character)) return false;
                position++;
                return true;
            }

            private void Expect(char character)
            {
                if (!Take(character)) Fail("Expected '" + character + "'");
            }

            private void Space()
            {
                while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\r' || text[position] == '\n'))
                    position++;
            }

            private void Fail(string message) => throw new InvalidDataException(message + " at character " + position + ".");
        }
    }
}
