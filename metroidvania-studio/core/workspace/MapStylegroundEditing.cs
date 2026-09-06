using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    /// <summary>Typed view over the version-2 styleground fields and extensible properties.</summary>
    [Serializable]
    public sealed class MapStylegroundSettings
    {
        public string Name = "Styleground", Type = "parallax", Texture = "", Color = "#FFFFFF";
        public MapLayer Layer = MapLayer.BackgroundDecals;
        public string ParentId = "", RoomFilter = "";
        public float ScrollX = 1, ScrollY = 1, X, Y, SpeedX, SpeedY, Alpha = 1;
        public bool LoopX = true, LoopY = true, FlipX, FlipY, Visible = true;
        public List<MapProperty> EffectProperties = new List<MapProperty>();
    }

    /// <summary>Styleground transactions. Coordinates and speeds use tiles and tiles/second.</summary>
    public sealed class MapStylegroundEditing
    {
        private static readonly string[] PropertyKeys = { "parentId", "x", "y", "speedX", "speedY", "loopX", "loopY", "flipX", "flipY", "alpha", "roomFilter", "visible" };
        private readonly MapEditSession session;

        public MapStylegroundEditing(MapEditSession session)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public static bool IsGroup(MapStyleground style) => style != null && style.type == "group";
        public static bool IsSupported(MapStyleground style) => style != null && (style.type == "parallax" || style.type == "group" || MapStylegroundEffects.Resolve(style.type) != null);
        public static bool IsCommonProperty(string key) => Array.IndexOf(PropertyKeys, key) >= 0;

        public static MapStylegroundSettings Read(MapStyleground style)
        {
            if (style == null) throw new ArgumentNullException(nameof(style));
            var result = new MapStylegroundSettings
            {
                Name = style.name, Type = style.type, Layer = style.layer, Texture = style.texture,
                Color = style.color, ScrollX = style.scrollX, ScrollY = style.scrollY,
                ParentId = Property(style, "parentId", ""), RoomFilter = Property(style, "roomFilter", ""),
                X = Number(style, "x", 0), Y = Number(style, "y", 0),
                SpeedX = Number(style, "speedX", 0), SpeedY = Number(style, "speedY", 0),
                Alpha = Number(style, "alpha", 1), LoopX = Boolean(style, "loopX", true), LoopY = Boolean(style, "loopY", true),
                FlipX = Boolean(style, "flipX", false), FlipY = Boolean(style, "flipY", false), Visible = Boolean(style, "visible", true),
                EffectProperties = style.properties.Where(item => !IsCommonProperty(item.key)).Select(item => new MapProperty { key = item.key, value = item.value }).ToList()
            };
            Validate(result);
            return result;
        }

        public static void Validate(MapStylegroundSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            Require(!string.IsNullOrWhiteSpace(settings.Name), "A styleground needs a name.");
            Require(settings.Type == "parallax" || settings.Type == "group" || MapStylegroundEffects.Resolve(settings.Type) != null, "No renderer is registered for this styleground effect.");
            Require(settings.Layer == MapLayer.ForegroundDecals || settings.Layer == MapLayer.BackgroundDecals,
                "Stylegrounds must use the foreground or background decal layer.");
            Require(settings.Texture != null && settings.ParentId != null && settings.RoomFilter != null, "Styleground text fields cannot be null.");
            Require(ColorText.TryParseHtmlString(settings.Color, out _), "Use a valid styleground color, such as #FFFFFF or #FFFFFF80.");
            Require(Finite(settings.X) && Finite(settings.Y) && Finite(settings.SpeedX) && Finite(settings.SpeedY)
                && Finite(settings.ScrollX) && Finite(settings.ScrollY) && Finite(settings.Alpha), "Styleground numeric values must be finite.");
            Require(settings.Alpha >= 0 && settings.Alpha <= 1, "Styleground alpha must be between 0 and 1.");
            Require(settings.EffectProperties != null, "Effect properties are missing.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (MapProperty property in settings.EffectProperties)
                Require(property != null && !string.IsNullOrWhiteSpace(property.key) && !IsCommonProperty(property.key)
                    && keys.Add(property.key) && property.value != null, "Effect properties need unique nonempty custom keys and non-null values.");
        }

        public static void Validate(MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            document.Validate();
        }

        // Called after MapDocument checks basic fields, unique IDs and properties.
        // Never call document.Validate here: the save/load validation path owns it.
        // Unknown effects have opaque properties and remain serializable unchanged.
        internal static void ValidateContents(MapDocument document)
        {
            var settings = document.stylegrounds.Where(IsSupported).ToDictionary(item => item.id, Read, StringComparer.Ordinal);
            foreach (MapStyleground style in document.stylegrounds)
                MapStylegroundEffects.Resolve(style.type)?.Validate(style);
            foreach (MapStylegroundSettings item in settings.Values)
            {
                if (item.ParentId.Length == 0) continue;
                Require(settings.TryGetValue(item.ParentId, out var parent), "Styleground references a missing parent group.");
                Require(parent.Type == "group", "A styleground parent must be a group.");
                Require(parent.Layer == item.Layer, "A styleground and its parent must use the same layer.");
            }
            var complete = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in settings.Keys)
            {
                var path = new HashSet<string>(StringComparer.Ordinal);
                string current = id;
                while (current.Length > 0 && !complete.Contains(current))
                {
                    Require(path.Add(current), "Styleground groups cannot contain a cycle.");
                    current = settings[current].ParentId;
                }
                complete.UnionWith(path);
            }
        }

        public string Create(MapLayer layer, bool group = false, string parentId = "") => Create(layer, group ? "group" : "parallax", parentId);

        public string Create(MapLayer layer, string typeId, string parentId = "")
        {
            string id = Guid.NewGuid().ToString("N");
            Edit("Create styleground", document =>
            {
                var style = new MapStyleground { id = id };
                MapStylegroundEffectDefinition effect = MapStylegroundEffects.Resolve(typeId);
                var settings = new MapStylegroundSettings { Layer = layer, Type = typeId, Name = effect?.DisplayName ?? (typeId == "group" ? "Group" : "Parallax"), ParentId = parentId };
                if (effect != null) foreach (MapFieldDefinition field in effect.Fields)
                    settings.EffectProperties.Add(new MapProperty { key = field.key, value = MapObjectDefinition.NormalizeValue(field, field.defaultValue) });
                Write(style, settings);
                document.stylegrounds.Add(style);
            });
            return id;
        }

        public void Update(string id, MapStylegroundSettings settings)
        {
            Validate(settings);
            Edit("Edit styleground", document =>
            {
                MapStyleground style = Find(document, id);
                Require(style.layer == settings.Layer, "Use SetLayer to move a styleground and its children between layers.");
                Write(style, settings);
            });
        }

        public void Rename(string id, string name)
        {
            Require(!string.IsNullOrWhiteSpace(name), "A styleground needs a name.");
            Edit("Rename styleground", document => Find(document, id).name = name);
        }

        /// <summary>Duplicates the selected node and all descendants with new IDs.</summary>
        public string Duplicate(string id)
        {
            string duplicateId = null;
            Edit("Duplicate styleground", document =>
            {
                MapStyleground original = Find(document, id);
                var descendants = Descendants(document, id);
                var source = document.stylegrounds.Where(item => descendants.Contains(item.id)).ToList();
                var ids = source.ToDictionary(item => item.id, item => Guid.NewGuid().ToString("N"), StringComparer.Ordinal);
                var copies = new List<MapStyleground>();
                foreach (MapStyleground item in source)
                {
                    var copy = MapJson.FromJson<MapStyleground>(MapJson.ToJson(item));
                    copy.id = ids[item.id];
                    if (!IsSupported(item))
                    {
                        if (item.id == id) copy.name += " copy";
                        copies.Add(copy);
                        continue;
                    }
                    var settings = Read(copy);
                    if (ids.TryGetValue(settings.ParentId, out var parent)) settings.ParentId = parent;
                    if (item.id == id) settings.Name += " copy";
                    Write(copy, settings);
                    copies.Add(copy);
                }
                duplicateId = ids[id];
                document.stylegrounds.InsertRange(document.stylegrounds.IndexOf(original) + 1, copies);
            });
            return duplicateId;
        }

        /// <summary>Deletes a node and its descendants as one reversible transaction.</summary>
        public void Delete(string id)
        {
            Edit("Delete styleground tree", document =>
            {
                var descendants = Descendants(document, id);
                document.stylegrounds.RemoveAll(item => descendants.Contains(item.id));
            });
        }

        /// <summary>Moves one position among siblings; group contents retain their internal order.</summary>
        public void Move(string id, int direction)
        {
            if (direction != -1 && direction != 1) throw new ArgumentOutOfRangeException(nameof(direction));
            Edit("Reorder styleground", document =>
            {
                MapStyleground item = Find(document, id);
                string parent = ParentId(item);
                var siblings = document.stylegrounds.Where(style => style.layer == item.layer && ParentId(style) == parent).ToList();
                int next = siblings.IndexOf(item) + direction;
                if (next < 0 || next >= siblings.Count) return;
                int fromIndex = document.stylegrounds.IndexOf(item), toIndex = document.stylegrounds.IndexOf(siblings[next]);
                document.stylegrounds[fromIndex] = siblings[next];
                document.stylegrounds[toIndex] = item;
            });
        }

        public void Reparent(string id, string parentId)
        {
            if (parentId == null) throw new ArgumentNullException(nameof(parentId));
            Edit("Reparent styleground", document =>
            {
                MapStyleground item = Find(document, id);
                var settings = Read(item); settings.ParentId = parentId; Write(item, settings);
            });
        }

        /// <summary>Moves the entire subtree, detaching its root from its previous parent.</summary>
        public void SetLayer(string id, MapLayer layer)
        {
            Require(layer == MapLayer.ForegroundDecals || layer == MapLayer.BackgroundDecals, "Choose the foreground or background styleground layer.");
            Edit("Move styleground layer", document =>
            {
                var descendants = Descendants(document, id);
                foreach (MapStyleground item in document.stylegrounds)
                {
                    if (!descendants.Contains(item.id)) continue;
                    if (!IsSupported(item)) { item.layer = layer; continue; }
                    var settings = Read(item); settings.Layer = layer;
                    if (item.id == id) settings.ParentId = "";
                    Write(item, settings);
                }
            });
        }

        public static MapStylegroundSettings GetEffectiveSettings(MapDocument document, string id)
        {
            var result = Read(Find(document, id));
            var visited = new HashSet<string>(StringComparer.Ordinal) { id };
            string parent = result.ParentId;
            while (parent.Length > 0)
            {
                Require(visited.Add(parent), "Styleground groups cannot contain a cycle.");
                var settings = Read(Find(document, parent));
                Require(settings.Type == "group" && settings.Layer == result.Layer, "Invalid styleground parent group.");
                result.Visible &= settings.Visible;
                result.Alpha *= settings.Alpha;
                parent = settings.ParentId;
            }
            return result;
        }

        public static bool MatchesRoom(MapDocument document, string id, string roomName)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string current = id;
            while (!string.IsNullOrEmpty(current))
            {
                Require(visited.Add(current), "Styleground groups cannot contain a cycle.");
                var settings = Read(Find(document, current));
                if (!MatchesFilter(settings.RoomFilter, roomName ?? "")) return false;
                current = settings.ParentId;
            }
            return true;
        }

        /// <summary>Comma-separated room-name globs; !pattern excludes, * and ? match wildcards.</summary>
        public static bool MatchesFilter(string filter, string roomName)
        {
            bool hasPositive = false, included = false;
            foreach (string value in (filter ?? "").Split(','))
            {
                string pattern = value.Trim();
                if (pattern.Length == 0) continue;
                bool exclude = pattern[0] == '!';
                if (exclude) pattern = pattern.Substring(1);
                else hasPositive = true;
                bool matches = Glob(pattern, roomName ?? "");
                if (exclude && matches) return false;
                if (!exclude && matches) included = true;
            }
            return !hasPositive || included;
        }

        /// <summary>Depth-first display/render order. Flat storage remains compatible with version 2.</summary>
        public static List<MapStyleground> GetOrdered(MapDocument document, MapLayer layer)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var children = new Dictionary<string, List<MapStyleground>>(StringComparer.Ordinal);
            foreach (MapStyleground style in document.stylegrounds)
            {
                if (style.layer != layer) continue;
                string parent = ParentId(style);
                if (!children.TryGetValue(parent, out var items)) children[parent] = items = new List<MapStyleground>();
                items.Add(style);
            }
            var result = new List<MapStyleground>();
            var pending = new Stack<MapStyleground>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            if (children.TryGetValue("", out var roots)) for (int i = roots.Count - 1; i >= 0; i--) pending.Push(roots[i]);
            while (pending.Count > 0)
            {
                MapStyleground item = pending.Pop();
                Require(visited.Add(item.id), "Styleground groups cannot contain a cycle.");
                result.Add(item);
                if (children.TryGetValue(item.id, out var nested)) for (int i = nested.Count - 1; i >= 0; i--) pending.Push(nested[i]);
            }
            Require(result.Count == document.stylegrounds.Count(item => item.layer == layer), "Styleground hierarchy contains a missing parent or cycle.");
            return result;
        }

        public static bool IsDescendant(MapDocument document, string id, string ancestorId)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string current = id;
            while (!string.IsNullOrEmpty(current))
            {
                Require(visited.Add(current), "Styleground groups cannot contain a cycle.");
                if (current == ancestorId) return true;
                current = ParentId(Find(document, current));
            }
            return false;
        }

        public static Vector2 GetOffset(MapStylegroundSettings settings, Vector2 cameraCenter, float time)
        {
            return new Vector2(settings.X + settings.SpeedX * time - cameraCenter.x * settings.ScrollX,
                settings.Y + settings.SpeedY * time - cameraCenter.y * settings.ScrollY);
        }

        private void Edit(string label, Action<MapDocument> change)
        {
            if (session.IsEditing) throw new InvalidOperationException("Finish or cancel the current map gesture first.");
            // Validate a detached candidate before opening a history transaction, so
            // invalid parents/settings cannot replace the live document or its redo.
            MapDocument candidate = session.Document.Clone();
            Validate(candidate);
            change(candidate);
            Validate(candidate);
            session.Execute(label, document => document.stylegrounds = candidate.stylegrounds);
        }

        private static HashSet<string> Descendants(MapDocument document, string id)
        {
            Find(document, id);
            return new HashSet<string>(document.stylegrounds.Where(item => IsDescendant(document, item.id, id)).Select(item => item.id), StringComparer.Ordinal);
        }

        private static MapStyleground Find(MapDocument document, string id)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            MapStyleground item = document.stylegrounds.Find(style => style.id == id);
            if (item == null) throw new ArgumentException("Unknown styleground '" + id + "'.", nameof(id));
            return item;
        }

        private static void Write(MapStyleground style, MapStylegroundSettings settings)
        {
            Validate(settings);
            style.name = settings.Name; style.type = settings.Type; style.layer = settings.Layer;
            style.texture = settings.Texture; style.color = settings.Color; style.scrollX = settings.ScrollX; style.scrollY = settings.ScrollY;
            var changedKeys = new HashSet<string>(settings.EffectProperties.Select(item => item.key), StringComparer.Ordinal);
            style.properties.RemoveAll(property => IsCommonProperty(property.key) || changedKeys.Contains(property.key));
            MapStylegroundEffectDefinition effect = MapStylegroundEffects.Resolve(settings.Type);
            foreach (MapProperty property in settings.EffectProperties)
            {
                MapFieldDefinition field = effect?.Fields.FirstOrDefault(item => item.key == property.key);
                Add(style, property.key, field != null ? MapObjectDefinition.NormalizeValue(field, property.value) : property.value);
            }
            Add(style, "parentId", settings.ParentId); Add(style, "x", Text(settings.X)); Add(style, "y", Text(settings.Y));
            Add(style, "speedX", Text(settings.SpeedX)); Add(style, "speedY", Text(settings.SpeedY));
            Add(style, "loopX", settings.LoopX.ToString()); Add(style, "loopY", settings.LoopY.ToString());
            Add(style, "flipX", settings.FlipX.ToString()); Add(style, "flipY", settings.FlipY.ToString());
            Add(style, "alpha", Text(settings.Alpha)); Add(style, "roomFilter", settings.RoomFilter); Add(style, "visible", settings.Visible.ToString());
        }

        private static void Add(MapStyleground item, string key, string value) => item.properties.Add(new MapProperty { key = key, value = value });
        private static string ParentId(MapStyleground item) => IsSupported(item) ? Property(item, "parentId", "") : "";
        private static string Property(MapStyleground style, string key, string fallback) => style.properties.Find(item => item.key == key)?.value ?? fallback;
        private static float Number(MapStyleground style, string key, float fallback)
        {
            string value = Property(style, key, null);
            if (value == null) return fallback;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) || !Finite(result))
                throw new InvalidOperationException("Styleground '" + style.name + "' has an invalid " + key + " value.");
            return result;
        }
        private static bool Boolean(MapStyleground style, string key, bool fallback)
        {
            string value = Property(style, key, null);
            if (value == null) return fallback;
            if (bool.TryParse(value, out bool result)) return result;
            throw new InvalidOperationException("Styleground '" + style.name + "' has an invalid " + key + " value.");
        }
        private static string Text(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private static bool Glob(string pattern, string value)
        {
            int p = 0, v = 0, star = -1, retry = 0;
            while (v < value.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == value[v])) { p++; v++; }
                else if (p < pattern.Length && pattern[p] == '*') { star = p++; retry = v; }
                else if (star >= 0) { p = star + 1; v = ++retry; }
                else return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }
    }
}
