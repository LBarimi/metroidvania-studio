using MetroidvaniaStudio;

internal static class MinimapGenerationTests
{
    public static void Layouts()
    {
        Check(1, ["96x8"], ["#123456"], false);
        Check(64, ["8x96"], ["#123456", "#345678", "#abcdef"], false);
        Check(MapDocument.MaximumRoomCount, MapMinimapChips.ById.Keys.ToArray(),
            ["#ff0000", "#00ff00", "#0000ff", "#ffffff", "#aaaaaa", "#888888", "#222222"], true);
        var chips = MapMinimapChips.ById.Keys.ToArray();
        string[] colors = ["#ffaa11", "#122333", "#13aaef"];
        string Layout(int seed) => MapJson.ToJson(new MapDocument { rooms = MapMinimapGenerator.Generate(42, chips, colors, true, seed) });
        string first = Layout(-42);
        Array.Reverse(chips); Array.Reverse(colors);
        if (first != Layout(-42) || first == Layout(42)) throw new Exception("Expected repeatable seeded generation.");
    }

    private static void Check(int count, string[] chipIds, string[] colors, bool automatic)
    {
        var rooms = MapMinimapGenerator.Generate(count, chipIds, colors, automatic, int.MinValue);
        var document = new MapDocument { rooms = rooms };
        document.Validate();
        if (rooms.Count != count) throw new Exception("Wrong room count.");
        var cells = new HashSet<(int, int)>();
        var regions = new Dictionary<string, string>();
        foreach (MapRoom room in rooms)
        {
            if (!chipIds.Contains($"{room.width}x{room.height}") || room.x % 8 != 0 || room.y % 8 != 0
                || room.foreground.Count + room.background.Count + room.objects.Count != 0)
                throw new Exception("Expected a permitted empty chip on the minimap grid.");
            for (int x = room.x; x < room.x + room.width; x += 8)
                for (int y = room.y; y < room.y + room.height; y += 8)
                    if (!cells.Add((x, y))) throw new Exception("Room interiors overlap.");
            regions.Add(room.id, room.properties.Single(p => p.key == MapMinimapShape.RegionColorProperty).value);
        }
        var adjacent = rooms.ToDictionary(room => room.id, _ => new List<string>());
        foreach (var connection in MiniMapGeometry.GetConnections(document))
        {
            adjacent[connection.RoomAId].Add(connection.RoomBId);
            adjacent[connection.RoomBId].Add(connection.RoomAId);
        }
        void Connected(IEnumerable<string> ids)
        {
            var remaining = ids.ToHashSet();
            var queue = new Queue<string>();
            string start = remaining.First(); remaining.Remove(start); queue.Enqueue(start);
            while (queue.TryDequeue(out var id))
                foreach (var other in adjacent[id])
                    if (remaining.Remove(other)) queue.Enqueue(other);
            if (remaining.Count != 0) throw new Exception("Rooms must connect through shared edges.");
        }
        Connected(adjacent.Keys);
        var used = regions.Values.ToHashSet();
        if (!used.IsSubsetOf(colors) || (!automatic && !used.SetEquals(colors)))
            throw new Exception("Unexpected region palette.");
        foreach (var group in regions.GroupBy(pair => pair.Value)) Connected(group.Select(pair => pair.Key));
    }

    public static void RouteSpacing()
    {
        const int count = 96;
        string[][] palettes = [MapMinimapChips.ById.Keys.ToArray(), ["8x8"], ["8x96"], ["96x8"]];
        foreach (var chips in palettes)
        foreach (int seed in new[] { 0, 17, -42 })
        {
            var rooms = MapMinimapGenerator.Generate(count, chips, ["#123456"], true, seed);
            int width = rooms.Max(r => r.x + r.width) - rooms.Min(r => r.x);
            int height = rooms.Max(r => r.y + r.height) - rooms.Min(r => r.y);
            double coverage = (double)rooms.Sum(r => r.width * r.height) / (width * height);
            var passages = MiniMapGeometry.GetConnections(new MapDocument { rooms = rooms });
            int junctions = passages.SelectMany(p => new[] { p.RoomAId, p.RoomBId }).GroupBy(id => id).Count(g => g.Count() >= 3);
            var (shortcuts, _) = CountShortcuts(rooms, passages);
            // Allow useful loops in addition to the original limit on incidental side contacts.
            int incidental = passages.Length - (count - 1) - shortcuts;
            if (coverage > .45 || incidental > count / 4 || junctions < 4 || shortcuts == 0)
                throw new Exception($"Expected spaced, branching routes for seed {seed} ({chips[0]}, {chips.Length} chip sizes): coverage {coverage:P0}, {passages.Length} passages, {junctions} junctions.");
        }
    }

    public static void RegionalShortcuts()
    {
        string[] colors = ["#985082", "#449eaa", "#ad7555", "#728b54", "#d09e45", "#6273b0"];
        foreach (int count in new[] { 32, 96 })
        foreach (int seed in new[] { 17, 42 })
        {
            var rooms = MapMinimapGenerator.Generate(count, MapMinimapChips.ById.Keys.ToArray(), colors, true, seed);
            var passages = MiniMapGeometry.GetConnections(new MapDocument { rooms = rooms });
            var (useful, crossRegion) = CountShortcuts(rooms, passages);
            if (useful < count / 32 || crossRegion == 0)
                throw new Exception($"Expected alternate routes between regions for {count} rooms, seed {seed}. Found {useful} shortcuts and {crossRegion} between colors.");
        }
    }

    // Measure the final geometry independently. Removing a connector must leave a longer,
    // traversable route between two rooms that already existed when it was placed.
    private static (int Useful, int CrossRegion) CountShortcuts(List<MapRoom> rooms, MiniMapConnection[] passages)
    {
        var indices = rooms.Select((room, index) => (room.id, index)).ToDictionary(p => p.id, p => p.index);
        var links = rooms.Select(_ => new List<int>()).ToArray();
        foreach (var passage in passages)
        {
            int a = indices[passage.RoomAId], b = indices[passage.RoomBId];
            links[a].Add(b); links[b].Add(a);
        }
        var colors = rooms.Select(r => r.properties.Single(p => p.key == MapMinimapShape.RegionColorProperty).value).ToArray();
        int useful = 0, crossRegion = 0;
        for (int room = 0; room < rooms.Count; room++)
        {
            var earlier = links[room].Where(i => i < room).ToArray();
            bool bypass = false, crosses = false;
            for (int i = 0; i + 1 < earlier.Length; i++)
            {
                int start = earlier[i];
                var distances = Enumerable.Repeat(-1, rooms.Count).ToArray();
                var queue = new Queue<int>();
                distances[start] = 0; queue.Enqueue(start);
                while (queue.TryDequeue(out int here))
                    foreach (int next in links[here])
                        if (next != room && distances[next] < 0) { distances[next] = distances[here] + 1; queue.Enqueue(next); }
                foreach (int end in earlier.Skip(i + 1))
                {
                    if (distances[end] < 5) continue;
                    bypass = true;
                    crosses |= colors[start] != colors[end];
                }
            }
            if (bypass) useful++;
            if (crosses) crossRegion++;
        }
        return (useful, crossRegion);
    }
}
