using System;
using System.Collections.Generic;

namespace MetroidvaniaStudio
{
    [Serializable]
    public sealed class MapLayerGroup
    {
        public string id = Guid.NewGuid().ToString("N");
        public string name = "Group";
        public MapLayer layer = MapLayer.ForegroundTiles;
        public string parentId = "";
        public bool visible = true;
        public bool locked;
    }

    /// <summary>Layer-local group hierarchy and membership, independent of the editor view.</summary>
    public static class MapLayerGroups
    {
        public const int MaximumGroupCount = 4096;
        public const int MaximumGroupDepth = 128;

        public static bool IsVisible(MapDocument document, string groupId)
        {
            RequireDocument(document);
            if (groupId == null) throw new ArgumentNullException(nameof(groupId));
            if (groupId.Length == 0) return true;
            return new MapLayerGroupIndex(document).Access(groupId, "").Visible;
        }

        public static bool IsLocked(MapDocument document, string groupId)
        {
            RequireDocument(document);
            if (groupId == null) throw new ArgumentNullException(nameof(groupId));
            if (groupId.Length == 0) return false;
            return new MapLayerGroupIndex(document).Access(groupId, "").Locked;
        }

        public static bool Matches(MapDocument document, string memberGroupId, string selectedGroupId)
        {
            RequireDocument(document);
            if (selectedGroupId == null) throw new ArgumentNullException(nameof(selectedGroupId));
            if (memberGroupId == null) throw new ArgumentNullException(nameof(memberGroupId));
            return new MapLayerGroupIndex(document).Access(memberGroupId, selectedGroupId).Matches;
        }

        public static string Create(MapDocument document, MapLayer layer, string name, string parentId = "")
        {
            RequireDocument(document);
            ValidateLayer(layer);
            ValidateName(name);
            if (parentId == null) throw new ArgumentNullException(nameof(parentId));
            document.Validate();
            if (parentId.Length > 0 && Find(document, parentId).layer != layer)
                throw new ArgumentException("A group and its parent must use the same layer.", nameof(parentId));
            var group = new MapLayerGroup { layer = layer, name = name, parentId = parentId };
            document.layerGroups.Add(group);
            return group.id;
        }

        public static void Rename(MapDocument document, string id, string name)
        {
            RequireDocument(document);
            ValidateName(name);
            document.Validate();
            Find(document, id).name = name;
        }

        /// <summary>Removes only the group; children and members move to its parent without losing map data.</summary>
        public static void Delete(MapDocument document, string id)
        {
            RequireDocument(document);
            document.Validate();
            MapLayerGroup removed = Find(document, id);
            foreach (MapLayerGroup group in document.layerGroups)
                if (group.parentId == id) group.parentId = removed.parentId;
            foreach (MapRoom room in document.rooms)
            {
                foreach (MapCell cell in room.foreground)
                    if (cell.groupId == id) cell.groupId = removed.parentId;
                foreach (MapCell cell in room.background)
                    if (cell.groupId == id) cell.groupId = removed.parentId;
                foreach (MapObject item in room.objects)
                    if (item.groupId == id) item.groupId = removed.parentId;
            }
            document.layerGroups.Remove(removed);
        }

        internal static Dictionary<string, MapLayerGroup> Validate(MapDocument document, HashSet<string> allIds)
        {
            if (document.layerGroups == null) throw new InvalidOperationException("Map layer groups are missing.");
            if (document.layerGroups.Count > MaximumGroupCount)
                throw new InvalidOperationException("A map cannot contain more than " + MaximumGroupCount + " layer groups.");
            var groups = new Dictionary<string, MapLayerGroup>(StringComparer.Ordinal);
            foreach (MapLayerGroup group in document.layerGroups)
            {
                if (group == null) throw new InvalidOperationException("Map contains a null layer group.");
                if (string.IsNullOrWhiteSpace(group.id) || !allIds.Add(group.id))
                    throw new InvalidOperationException("Missing or duplicate layer group ID '" + group.id + "'.");
                if (string.IsNullOrWhiteSpace(group.name)) throw new InvalidOperationException("Layer group needs a name.");
                if (group.layer < MapLayer.ForegroundTiles || group.layer > MapLayer.BackgroundDecals)
                    throw new InvalidOperationException("Layer group must belong to one actual layer.");
                if (group.parentId == null) throw new InvalidOperationException("Layer group parent ID cannot be null.");
                groups.Add(group.id, group);
            }
            foreach (MapLayerGroup group in document.layerGroups)
            {
                if (group.parentId.Length == 0) continue;
                if (!groups.TryGetValue(group.parentId, out MapLayerGroup parent))
                    throw new InvalidOperationException("Layer group references missing parent '" + group.parentId + "'.");
                if (parent.layer != group.layer)
                    throw new InvalidOperationException("Layer group and parent must belong to the same layer.");
            }
            // Iterative traversal keeps deeply nested imported groups off the call stack.
            var depths = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (MapLayerGroup group in document.layerGroups)
            {
                var path = new HashSet<string>(StringComparer.Ordinal);
                var orderedPath = new List<string>();
                string current = group.id;
                while (current.Length > 0 && !depths.ContainsKey(current))
                {
                    if (!path.Add(current)) throw new InvalidOperationException("Layer group hierarchy contains a cycle.");
                    orderedPath.Add(current);
                    current = groups[current].parentId;
                }
                int depth = current.Length > 0 ? depths[current] : 0;
                for (int index = orderedPath.Count - 1; index >= 0; index--)
                {
                    if (++depth > MaximumGroupDepth)
                        throw new InvalidOperationException("Layer group hierarchy cannot be deeper than " + MaximumGroupDepth + " groups.");
                    depths.Add(orderedPath[index], depth);
                }
            }
            return groups;
        }

        private static MapLayerGroup Find(MapDocument document, string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Choose a layer group.", nameof(id));
            MapLayerGroup group = document.layerGroups.Find(item => item != null && item.id == id);
            if (group == null) throw new ArgumentException("Unknown layer group '" + id + "'.", nameof(id));
            return group;
        }

        private static void RequireDocument(MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (document.layerGroups == null) throw new InvalidOperationException("Map layer groups are missing.");
        }

        private static void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A layer group needs a name.", nameof(name));
        }

        private static void ValidateLayer(MapLayer layer)
        {
            if (layer < MapLayer.ForegroundTiles || layer > MapLayer.BackgroundDecals)
                throw new ArgumentOutOfRangeException(nameof(layer), "A group must belong to one actual layer.");
        }
    }

    internal readonly struct MapLayerGroupAccess
    {
        public readonly bool Visible, Locked, Matches;
        public MapLayerGroupAccess(bool visible, bool locked, bool matches)
        { Visible = visible; Locked = locked; Matches = matches; }
    }

    /// <summary>One O(n) index with bounded O(depth) hierarchy queries.</summary>
    internal sealed class MapLayerGroupIndex
    {
        private readonly Dictionary<string, MapLayerGroup> groups;

        public MapLayerGroupIndex(MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (document.layerGroups == null) throw new InvalidOperationException("Map layer groups are missing.");
            groups = new Dictionary<string, MapLayerGroup>(document.layerGroups.Count, StringComparer.Ordinal);
            foreach (MapLayerGroup group in document.layerGroups)
            {
                if (group == null || string.IsNullOrEmpty(group.id) || !groups.TryAdd(group.id, group))
                    throw new InvalidOperationException("Map contains a null, missing, or duplicate layer group ID.");
            }
        }

        public MapLayerGroupAccess Access(string memberGroupId, string selectedGroupId)
        {
            if (memberGroupId == null) throw new ArgumentNullException(nameof(memberGroupId));
            if (selectedGroupId == null) throw new ArgumentNullException(nameof(selectedGroupId));
            if (selectedGroupId.Length > 0 && !groups.ContainsKey(selectedGroupId))
                throw new ArgumentException("Unknown layer group '" + selectedGroupId + "'.", nameof(selectedGroupId));
            bool visible = true, locked = false, matches = selectedGroupId.Length == 0;
            string current = memberGroupId;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            int depth = 0;
            while (current.Length > 0)
            {
                if (++depth > MapLayerGroups.MaximumGroupDepth)
                    throw new InvalidOperationException("Layer group hierarchy cannot be deeper than " + MapLayerGroups.MaximumGroupDepth + " groups.");
                if (!visited.Add(current)) throw new InvalidOperationException("Layer group hierarchy contains a cycle.");
                if (!groups.TryGetValue(current, out MapLayerGroup group))
                    throw new ArgumentException("Unknown layer group '" + current + "'.", nameof(memberGroupId));
                visible &= group.visible;
                locked |= group.locked;
                matches |= group.id == selectedGroupId;
                current = group.parentId ?? throw new InvalidOperationException("Layer group parent ID cannot be null.");
            }
            return new MapLayerGroupAccess(visible, locked, matches);
        }
    }
}
