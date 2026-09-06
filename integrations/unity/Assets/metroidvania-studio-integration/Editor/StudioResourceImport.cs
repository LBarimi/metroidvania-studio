using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MetroidvaniaStudio.Integration.Editor
{
    public static class StudioResourceImport
    {
        public const string ImportRoot = "Assets/MetroidvaniaStudioImports";
        public static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public static StudioResourceLibrary Create(CatalogData catalog, int ppu, IReadOnlyDictionary<string, byte[]> textures)
        {
            if (catalog == null || catalog.materials == null || catalog.objects == null) throw new InvalidOperationException("Resource catalog is missing.");
            var identity = new StringBuilder(JsonUtility.ToJson(catalog)).Append(':').Append(ppu);
            foreach (var pair in textures.OrderBy(pair => pair.Key, StringComparer.Ordinal)) identity.Append('\n').Append(pair.Key).Append(':').Append(Hash(pair.Value));
            string folder = ImportRoot + "/Resources-" + Hash(Encoding.UTF8.GetBytes(identity.ToString())).Substring(0, 24);
            string libraryPath = folder + "/Resources.asset";
            var existing = AssetDatabase.LoadAssetAtPath<StudioResourceLibrary>(libraryPath); if (existing) return existing;
            Directory.CreateDirectory(folder); AssetDatabase.Refresh();
            var library = ScriptableObject.CreateInstance<StudioResourceLibrary>(); library.ppu = ppu;
            try
            {
                AssetDatabase.CreateAsset(library, libraryPath);
                var loaded = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
                foreach (var pair in textures)
                {
                    string asset = folder + "/Texture-" + Hash(pair.Value).Substring(0, 24) + ".png";
                    if (!File.Exists(asset)) File.WriteAllBytes(asset, pair.Value);
                    AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceSynchronousImport);
                    var importer = AssetImporter.GetAtPath(asset) as TextureImporter;
                    if (!importer) throw new InvalidOperationException("Unsupported texture data.");
                    importer.textureType = TextureImporterType.Default; importer.isReadable = true; importer.mipmapEnabled = false;
                    importer.npotScale = TextureImporterNPOTScale.None; importer.filterMode = FilterMode.Point;
                    importer.textureCompression = TextureImporterCompression.Uncompressed; importer.maxTextureSize = 16384;
                    importer.spritePixelsPerUnit = ppu; importer.SaveAndReimport();
                    loaded[pair.Key] = AssetDatabase.LoadAssetAtPath<Texture2D>(asset);
                }
                Sprite MakeSprite(SpriteData data, string name, TileShape? shape)
                {
                    if (!loaded.TryGetValue(data.asset, out var texture) || data.width < 1 || data.height < 1 || data.x < 0 || data.y < 0
                        || (long)data.x + data.width > texture.width || (long)data.y + data.height > texture.height) throw new InvalidOperationException("Invalid sprite rectangle: " + name);
                    var sprite = Sprite.Create(texture, new Rect(data.x, data.y, data.width, data.height), new Vector2(.5f, .5f), ppu, 0, SpriteMeshType.FullRect);
                    sprite.name = name;
                    AssetDatabase.AddObjectToAsset(sprite, library); return sprite;
                }
                foreach (var material in catalog.materials)
                foreach (var data in material.sprites)
                {
                    if (data.width != 16 || data.height != 16 || data.shape < 0 || data.shape > 4) throw new InvalidOperationException("Terrain sprites must be 16 by 16 pixels with a supported shape.");
                    var shape = (TileShape)data.shape;
                    var tile = ScriptableObject.CreateInstance<Tile>(); tile.name = material.id + ":" + data.shape + ":" + data.mask;
                    tile.sprite = MakeSprite(data, tile.name, shape); tile.colliderType = shape == TileShape.Solid ? Tile.ColliderType.Grid : Tile.ColliderType.None;
                    AssetDatabase.AddObjectToAsset(tile, library);
                    library.tiles.Add(new StudioResourceLibrary.TileEntry { material = material.id, mask = data.mask, shape = shape, tile = tile });
                }
                foreach (var definition in catalog.objects)
                    if (!string.IsNullOrWhiteSpace(definition.sprite?.asset)) library.objects.Add(new StudioResourceLibrary.ObjectEntry { definition = definition.id, sprite = MakeSprite(definition.sprite, definition.id, null) });
                EditorUtility.SetDirty(library); AssetDatabase.SaveAssets(); return library;
            }
            catch { AssetDatabase.DeleteAsset(folder); if (library) UnityEngine.Object.DestroyImmediate(library); throw; }
        }
        public static string[] TextureNames(CatalogData catalog)
        {
            return catalog.materials.SelectMany(material => material.sprites).Concat(catalog.objects.Where(item => !string.IsNullOrWhiteSpace(item.sprite?.asset)).Select(item => item.sprite))
                .Select(sprite => sprite.asset).Distinct(StringComparer.Ordinal).ToArray();
        }
        public static string ResolveTexturePath(string directory, string relative)
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidOperationException("Texture names must be relative to the resource directory.");
            string candidate = Path.GetFullPath(Path.Combine(root, relative));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Texture path leaves the selected resource directory.");
            return candidate;
        }
    }
}
