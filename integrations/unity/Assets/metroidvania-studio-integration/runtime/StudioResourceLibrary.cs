using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MetroidvaniaStudio.Integration
{
    public sealed class StudioResourceLibrary : ScriptableObject
    {
        [Serializable] public sealed class TileEntry { public string material; public int mask; public TileShape shape; public Tile tile; }
        [Serializable] public sealed class ObjectEntry { public string definition; public Sprite sprite; }
        public int ppu = 16;
        public List<TileEntry> tiles = new List<TileEntry>();
        public List<ObjectEntry> objects = new List<ObjectEntry>();
        private Dictionary<string, Tile> tileIndex;
        private Dictionary<string, Sprite> objectIndex;
        private static string Key(string material, int mask, TileShape shape) => material + ":" + mask + ":" + (int)shape;
        private void OnEnable() { tileIndex = null; objectIndex = null; }
        public Tile ResolveTile(string material, int mask, TileShape shape)
        {
            if (tileIndex == null) { tileIndex = new Dictionary<string, Tile>(StringComparer.Ordinal); foreach (var entry in tiles) tileIndex[Key(entry.material, entry.mask, entry.shape)] = entry.tile; }
            if (tileIndex.TryGetValue(Key(material, shape == TileShape.Solid ? mask : 0, shape), out Tile tile)) return tile;
            if (tileIndex.TryGetValue(Key(material, 0, shape), out tile)) return tile;
            throw new InvalidOperationException("Missing tile resource: " + material);
        }
        public Sprite ResolveObject(string definition)
        {
            if (objectIndex == null) { objectIndex = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase); foreach (var entry in objects) objectIndex[entry.definition] = entry.sprite; }
            return objectIndex.TryGetValue(definition, out Sprite sprite) ? sprite : null;
        }
    }
}
