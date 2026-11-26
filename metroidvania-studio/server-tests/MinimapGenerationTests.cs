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
}
