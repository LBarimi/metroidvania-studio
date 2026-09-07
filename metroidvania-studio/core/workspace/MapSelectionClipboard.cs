using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    /// <summary>A detached selection. Its one room stores coordinates relative to the selection's lower-left.</summary>
    public sealed class MapSelectionClipboard
    {
        private readonly MapDocument fragment;
        public MapLayer SourceLayer { get; }
        public bool ObjectsOnly { get; }
        public bool NodesOnly { get; }
        public Vector2 Size { get; }
        public MapDocument Fragment => fragment.Clone();
        // The controller only reads this detached, constructor-owned snapshot.
        // Keeping this internal avoids another complete JSON clone while a paste
        // command is holding the editor workspace lock.
        internal MapDocument ReadOnlyFragment => fragment;

        public MapSelectionClipboard(MapDocument document, MapLayer sourceLayer, bool objectsOnly, Vector2 size, bool nodesOnly = false)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (sourceLayer < MapLayer.ForegroundTiles || sourceLayer > MapLayer.All)
                throw new InvalidDataException("Unknown clipboard source layer.");
            if (!Finite(size.x) || !Finite(size.y) || size.x < 0 || size.y < 0 || !nodesOnly && (size.x == 0 || size.y == 0)
                || size.x > 1000000 || size.y > 1000000)
                throw new InvalidDataException("Clipboard bounds must be finite and positive.");
            document.Validate();
            if (document.rooms.Count != 1 || document.stylegrounds.Count != 0 || document.properties.Count != 0)
                throw new InvalidDataException("A selection contains one room and no map settings or stylegrounds.");
            var room = document.rooms[0];
            if (room.x != 0 || room.y != 0 || !room.visible || room.locked || room.properties.Count != 0)
                throw new InvalidDataException("Invalid clipboard room metadata.");
            if (objectsOnly && (room.foreground.Count != 0 || room.background.Count != 0)
                || !objectsOnly && (size.x != room.width || size.y != room.height))
                throw new InvalidDataException("Clipboard bounds do not match its contents.");
            if (nodesOnly && (!objectsOnly || room.objects.Count != 1 || room.objects[0].nodes.Count == 0))
                throw new InvalidDataException("A node selection requires one source owner and at least one node.");
            if (room.foreground.Count + room.background.Count + room.objects.Count == 0)
                throw new InvalidDataException("The selection is empty.");
            if (sourceLayer != MapLayer.All
                && (room.foreground.Count > 0 && sourceLayer != MapLayer.ForegroundTiles
                    || room.background.Count > 0 && sourceLayer != MapLayer.BackgroundTiles
                    || room.objects.Any(item => item.layer != sourceLayer)))
                throw new InvalidDataException("Clipboard members do not match its source layer.");
            fragment = document.Clone(); SourceLayer = sourceLayer; ObjectsOnly = objectsOnly; NodesOnly = nodesOnly; Size = size;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>Versioned selection text reuses the map parser's strict field, type, null and size checks.</summary>
    public static class MapSelectionClipboardCodec
    {
        // Stable wire identifier: keep existing copied selections readable across the product rename.
        public const string Prefix = "Metroidvania.MapMaker.Selection/";
        public const string Header = Prefix + "1\n";
        private static readonly string[] Keys = { "selection.layer", "selection.objectsOnly", "selection.width", "selection.height", "selection.nodesOnly" };

        public static string Serialize(MapSelectionClipboard selection)
        {
            if (selection == null) throw new ArgumentNullException(nameof(selection));
            MapDocument fragment = selection.Fragment;
            string[] values = { ((int)selection.SourceLayer).ToString(CultureInfo.InvariantCulture), selection.ObjectsOnly ? "true" : "false",
                selection.Size.x.ToString("R", CultureInfo.InvariantCulture), selection.Size.y.ToString("R", CultureInfo.InvariantCulture), selection.NodesOnly ? "true" : "false" };
            for (int i = 0; i < Keys.Length; i++) fragment.properties.Add(new MapProperty { key = Keys[i], value = values[i] });
            string text = Header + MapDocumentStore.Serialize(fragment);
            if (Encoding.UTF8.GetByteCount(text) > MapDocumentStore.MaximumFileBytes)
                throw new InvalidDataException("Clipboard selection exceeds the 32 MiB limit.");
            return text;
        }

        public static MapSelectionClipboard Deserialize(string text)
        {
            if (text == null || !text.StartsWith(Header, StringComparison.Ordinal))
                throw new InvalidDataException(text != null && text.StartsWith(Prefix, StringComparison.Ordinal)
                    ? "Unsupported clipboard selection version." : "The clipboard does not contain a Metroidvania Studio selection.");
            if (Encoding.UTF8.GetByteCount(text) > MapDocumentStore.MaximumFileBytes)
                throw new InvalidDataException("Clipboard selection exceeds the 32 MiB limit.");
            MapDocument fragment = MapDocumentStore.Deserialize(text.Substring(Header.Length));
            if (fragment.properties.Count != Keys.Length || fragment.properties.Any(property => !Keys.Contains(property.key)))
                throw new InvalidDataException("Clipboard selection metadata is missing or unknown.");
            var values = fragment.properties.ToDictionary(property => property.key, property => property.value, StringComparer.Ordinal);
            if (!int.TryParse(values[Keys[0]], NumberStyles.None, CultureInfo.InvariantCulture, out int layer)
                || (values[Keys[1]] != "true" && values[Keys[1]] != "false")
                || (values[Keys[4]] != "true" && values[Keys[4]] != "false")
                || !float.TryParse(values[Keys[2]], NumberStyles.Float, CultureInfo.InvariantCulture, out float width)
                || !float.TryParse(values[Keys[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out float height))
                throw new InvalidDataException("Invalid clipboard selection metadata.");
            fragment.properties.Clear();
            return new MapSelectionClipboard(fragment, (MapLayer)layer, values[Keys[1]] == "true", new Vector2(width, height), values[Keys[4]] == "true");
        }
    }
}
