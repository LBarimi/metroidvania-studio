using MetroidvaniaStudio;
using MetroidvaniaStudio.Primitives;

internal static class TilemapGenerationTests
{
    public static void Layouts()
    {
        var materials = new Dictionary<string, TerrainTileSet>(StringComparer.Ordinal);
        foreach (var (id, color) in new[] { ("biome-rock", "#827661"), ("biome-ice-cavern", "#749da4"),
            ("biome-ancient-ruins", "#aa5533"), (MapTilemapGenerator.Ladder, ""), (MapTilemapGenerator.Floor, "") })
        {
            var brush = new TerrainTileSet { AssetId = id };
            if (ColorText.TryParseHtmlString(color, out var tint)) brush.SetTheme(id, tint);
            materials.Add(id, brush);
        }
        MapDocument Generate(string level, string theme = "auto", int count = 64, string[]? chips = null, int seed = 42)
        {
            var doc = new MapDocument { rooms = MapMinimapGenerator.Generate(count, chips ?? MapMinimapChips.ById.Keys.ToArray(),
                ["#827661", "#749da4"], false, seed) };
            MapTilemapGenerator.Fill(doc, materials, theme, level, seed);
            return doc;
        }
        var map = Generate("progressive");
        string snapshot = MapDocumentStore.Serialize(map);
        if (snapshot != MapDocumentStore.Serialize(Generate("progressive"))) throw new Exception("Tile generation changed for identical settings.");
        var levels = new HashSet<string>();
        foreach (var room in map.rooms)
        {
            levels.Add(room.properties.Single(p => p.key == MapTilemapGenerator.DifficultyProperty).value);
            string region = room.properties.Single(p => p.key == MapMinimapShape.RegionColorProperty).value;
            string expected = region == "#827661" ? "biome-rock" : "biome-ice-cavern";
            if (!room.foreground.Any(c => c.material == expected) || room.foreground.Any(c => c.material != expected
                && c.material != MapTilemapGenerator.Ladder && c.material != MapTilemapGenerator.Floor)) throw new Exception("Wrong region terrain.");
        }
        if (levels.Count != 3) throw new Exception("Expected increasing difficulty along the route.");
        var easy = Generate("easy", "biome-rock");
        var hard = Generate("hard", "biome-rock");
        int LadderCount(MapDocument doc) => doc.rooms.Sum(r => r.foreground.Count(c => c.material == MapTilemapGenerator.Ladder));
        if (LadderCount(easy) <= LadderCount(hard)) throw new Exception("Hard shafts should trade some climbs for jumps.");
        CheckPassages(map);
        CheckPassages(hard);
        CheckPassages(Generate("normal", count: 32, seed: -17));
        CheckPassages(Generate("easy", count: 32, chips: ["8x8", "8x96", "96x8"], seed: 7));
        var corridors = Generate("normal", count: 40, chips: ["32x16"]);
        if (!corridors.rooms.Any(r => r.foreground.All(c => c.material != MapTilemapGenerator.Ladder)))
            throw new Exception("Expected passages that can be crossed entirely on foot.");
        MapDocumentStore.Serialize(Generate("easy", "biome-rock", 1024, ["64x64"]));
        MapDocumentStore.Serialize(Generate("progressive", "biome-ancient-ruins", 1024, ["64x64"]));
    }

    private static void CheckPassages(MapDocument doc)
    {
        var exits = doc.rooms.ToDictionary(r => r.id, r => new List<(int X, int Y)>());
        var rooms = doc.rooms.ToDictionary(r => r.id);
        foreach (var edge in MiniMapGeometry.GetConnections(doc))
        {
            foreach (string id in new[] { edge.RoomAId, edge.RoomBId })
            {
                var r = rooms[id];
                int mid = (int)((edge.Start + edge.End) / 2);
                var (x, y) = edge.Vertical
                    ? (Math.Clamp((int)edge.Coordinate - r.x, 0, r.width - 1), mid - r.y)
                    : (mid - r.x, Math.Clamp((int)edge.Coordinate - r.y, 0, r.height - 1));
                exits[id].Add((x, y));
                var portal = r.objects.Single(o => o.definition == "Portal" && o.x == x && o.y == y);
                if (portal.layer != MapLayer.Entities || portal.width != (edge.Vertical ? 1 : 2)
                    || portal.height != (edge.Vertical ? 3 : 1)) throw new Exception("Portal does not cover the entrance in " + id);
            }
        }
        foreach (var room in doc.rooms)
        {
            var cells = room.foreground.ToDictionary(c => (c.x, c.y), c => c.material);
            string? At(int x, int y) => cells.GetValueOrDefault((x, y));
            bool Space(int x, int y) => At(x,y) is null or MapTilemapGenerator.Floor or MapTilemapGenerator.Ladder;
            if (room.objects.Count != exits[room.id].Count) throw new Exception("Unexpected portal in " + room.id);
            foreach (var portal in room.objects)
                for (int y = (int)portal.y; y < portal.y + portal.height; y++)
                    for (int x = (int)portal.x; x < portal.x + portal.width; x++)
                        if (!room.Contains(x,y) || !Space(x,y)) throw new Exception("Portal overlaps a wall in " + room.id);
            bool Fits(int x, int y) => room.Contains(x,y) && Space(x,y) && Space(x,y+1);
            bool Supported(int x, int y) => At(x,y) == MapTilemapGenerator.Ladder || At(x,y-1) != null;
            var portals = exits[room.id].Distinct().ToArray();
            bool Socket(int x, int y) => portals.Any(p =>
                (p.X == 0 || p.X == room.width-1) ? x == p.X && y >= p.Y && y < p.Y + 3
                : y == p.Y && x >= p.X && x <= p.X + 1);
            for (int x = 0; x < room.width; x++)
                foreach (int y in new[] {0,room.height-1})
                    if (Space(x,y) && !Socket(x,y)) throw new Exception("Unexpected vertical exit in " + room.id);
            for (int y = 0; y < room.height; y++)
                foreach (int x in new[] {0,room.width-1})
                    if (Space(x,y) && !Socket(x,y)) throw new Exception("Unexpected side exit in " + room.id);
            foreach (var start in portals)
            {
                var reached = new HashSet<(int X,int Y)> { start };
                var queue = new Queue<(int X,int Y)>(reached);
                while (queue.TryDequeue(out var p) && !portals.All(reached.Contains))
                {
                    var (x,y) = p;
                    void Visit(int nx,int ny)
                    {
                        if (Fits(nx,ny) && reached.Add((nx,ny))) queue.Enqueue((nx,ny));
                    }
                    Visit(x,y-1);
                    if (At(x,y) == MapTilemapGenerator.Ladder || At(x,y+1) == MapTilemapGenerator.Ladder) Visit(x,y+1);
                    if (!Supported(x,y)) continue;
                    foreach (int step in new[] { -1,1 })
                    {
                        Visit(x+step,y);
                        if (!Space(x+step,y) && Fits(x,y+1)) Visit(x+step,y+1);
                    }
                    foreach (var jump in Jumps)
                    {
                        var end = jump[^1];
                        if (reached.Contains((x+end.X,y+end.Y)) || !Supported(x+end.X,y+end.Y)) continue;
                        if (jump.All(t => Fits(x+t.X,y+t.Y))) Visit(x+end.X,y+end.Y);
                    }
                }
                var missed = portals.Where(p => !Fits(p.X,p.Y) || !reached.Contains(p)).ToArray();
                if (missed.Length == 0) continue;
                var picture = Enumerable.Range(0, room.height).Reverse().Select(y => new string(Enumerable.Range(0,room.width)
                    .Select(x => portals.Contains((x,y)) ? 'D' : At(x,y) switch { MapTilemapGenerator.Ladder => 'H',
                        MapTilemapGenerator.Floor => '=', null => reached.Contains((x,y)) ? '.' : ' ', _ => '#' }).ToArray()));
                throw new Exception($"Unreachable passage in {room.id}, from {start} to {string.Join(" ", missed)}\n" + string.Join("\n",picture));
            }
        }
    }
    // Sample the full arc of a two-tile-tall actor, including overhead clearance.
    private static readonly (int X,int Y)[][] Jumps = (
        from dx in Enumerable.Range(-3,7)
        from dy in Enumerable.Range(-1,4)
        where dx != 0 || dy > 0
        select Enumerable.Range(1,12).Select(i => {
            double t = i / 12.0;
            return ((int)Math.Round(dx*t), (int)Math.Floor(dy*t + 8*t*(1-t) + 0.001));
        }).ToArray()).ToArray();
}
