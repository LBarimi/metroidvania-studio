using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

#nullable enable
#pragma warning disable CS8618 // Wire DTOs are populated by their producer or JSON deserializer.

namespace MetroidvaniaStudio
{
    [Serializable] public sealed class CameraProfile
    {
        [JsonInclude] public int ppu = 16, referenceWidth = 320, referenceHeight = 180;
        [JsonInclude] public float orthographicSize = 5.625f, x, y;
    }

    [Serializable] public sealed class SpriteData
    {
        public string asset;
        public int x, y, width, height, mask, shape;
    }

    [Serializable] public sealed class MaterialData
    {
        public string id, name, color;
        public string? themeId;
        public List<SpriteData> sprites = new List<SpriteData>();
    }

    [Serializable] public sealed class ObjectData
    {
        public string id, name, color;
        public int layer, placement, minimumNodes, maximumNodes;
        public float width, height, minimumWidth, minimumHeight;
        public bool resizable, rotatable, flippable;
        public SpriteData? sprite;
        public List<MapFieldDefinition> properties = new List<MapFieldDefinition>();
    }

    [Serializable] public sealed class CatalogData
    {
        public List<MaterialData> materials = new List<MaterialData>();
        public List<ObjectData> objects = new List<ObjectData>();
        public CameraProfile camera;
    }
}
