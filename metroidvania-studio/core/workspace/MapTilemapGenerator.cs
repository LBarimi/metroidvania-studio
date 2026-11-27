using System;
using System.Collections.Generic;
using System.Linq;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio;

public static class MapTilemapGenerator
{
    public const string Ladder = "traversal-ladder", Floor = "traversal-floor";
    public const string DifficultyProperty = "mapMaker.tileDifficulty";

    internal readonly record struct Door(int X, int Y, bool Side)
    {
        internal const int Headroom = 3;
        internal int Width => Side ? 1 : 2;
        internal int Height => Side ? Headroom : 1;
    }

    public static void Fill(MapDocument map, IReadOnlyDictionary<string, TerrainTileSet> materials,
        string theme, string difficulty, int seed)
    {
        if (difficulty is not ("easy" or "normal" or "hard" or "progressive"))
            throw new ArgumentException("@generationInvalidDifficulty");
        if (!materials.ContainsKey(Ladder) || !materials.ContainsKey(Floor))
            throw new ArgumentException("@generationMissingChips");
        var available = materials.Values.Where(m => m.HasTheme && m.AssetId != Ladder && m.AssetId != Floor)
            .OrderBy(m => m.AssetId, StringComparer.Ordinal).ToArray();
        var biomes = available.Where(m => m.AssetId.StartsWith("biome-", StringComparison.Ordinal)).ToArray();
        var choices = theme == "auto" ? (biomes.Length > 0 ? biomes : available)
            : available.Where(m => m.AssetId == theme).ToArray();
        if (choices.Length == 0) throw new ArgumentException("@generationMissingTheme");

        var doors = map.rooms.ToDictionary(room => room.id, room => new List<Door>());
        var neighbors = map.rooms.ToDictionary(room => room.id, room => new List<string>());
        var rooms = map.rooms.ToDictionary(room => room.id);
        var objectIds = map.rooms.SelectMany(room => room.objects).Select(item => item.id).ToHashSet(StringComparer.Ordinal);
        foreach (var edge in MiniMapGeometry.GetConnections(map))
        {
            int mid = (int)Math.Floor((edge.Start + edge.End) / 2);
            foreach (string id in new[] { edge.RoomAId, edge.RoomBId })
            {
                var room = rooms[id];
                int x = edge.Vertical ? Math.Clamp((int)edge.Coordinate - room.x, 0, room.width - 1) : mid - room.x;
                int y = edge.Vertical ? mid - room.y : Math.Clamp((int)edge.Coordinate - room.y, 0, room.height - 1);
                var door = new Door(x, y, edge.Vertical);
                doors[id].Add(door);
                string neighborId = id == edge.RoomAId ? edge.RoomBId : edge.RoomAId;
                room.objects.Add(new MapObject {
                    id = MapObjectIds.Derive("portal\0" + id + "\0" + neighborId, objectIds),
                    definition = "Portal", layer = MapLayer.Entities,
                    x = door.X, y = door.Y, width = door.Width, height = door.Height,
                    properties = [new MapProperty { key = "desc", value = "" }]
                });
            }
            neighbors[edge.RoomAId].Add(edge.RoomBId);
            neighbors[edge.RoomBId].Add(edge.RoomAId);
        }
        var distance = new Dictionary<string, int> { [map.rooms[0].id] = 0 };
        var queue = new Queue<string>(distance.Keys);
        while (queue.TryDequeue(out var current))
            foreach (string next in neighbors[current])
                if (distance.TryAdd(next, distance[current] + 1)) queue.Enqueue(next);
        int farthest = distance.Values.Max();
        var random = new MapMinimapGenerator.LayoutRandom(seed);
        foreach (var room in map.rooms)
        {
            ColorText.TryParseHtmlString(room.properties.First(p => p.key == MapMinimapShape.RegionColorProperty).value, out var color);
            var terrain = choices.MinBy(m => Distance(color, m.ThemeColor));
            int tier = difficulty switch { "easy" => 0, "normal" => 1, "hard" => 2,
                _ => distance[room.id] * 3 / (farthest + 1) };
            MapRoomTheme.Apply(room, terrain);
            room.properties.Add(new MapProperty { key = DifficultyProperty, value = new[] { "easy", "normal", "hard" }[tier] });
            var roomRandom = new MapMinimapGenerator.LayoutRandom(random.Next(int.MaxValue));
            var layout = new RoomTerrainLayout(room.width, room.height, doors[room.id], terrain.ThemeId, tier, roomRandom);
            layout.Paint(room, terrain.AssetId, map.rooms.Count);
        }
    }

    private static float Distance(Color a, Color b) =>
        (a.r - b.r) * (a.r - b.r) + (a.g - b.g) * (a.g - b.g) + (a.b - b.b) * (a.b - b.b);

}
