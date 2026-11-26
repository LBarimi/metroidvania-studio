using System;
using System.Collections.Generic;
using System.Linq;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public static class MapMinimapGenerator
    {
        public const string SeedProperty = "mapMaker.minimapGenerationSeed";

        // SplitMix64 gives saved seeds the same sequence on every .NET runtime.
        private sealed class LayoutRandom(int seed)
        {
            private ulong state = unchecked((uint)seed);

            public int Next(int limit)
            {
                unchecked
                {
                    ulong value = state += 0x9E3779B97F4A7C15UL;
                    value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                    value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                    return (int)((value ^ (value >> 31)) % (uint)limit);
                }
            }

            public void Shuffle<T>(T[] items)
            {
                for (int i = items.Length - 1; i > 0; i--)
                {
                    int j = Next(i + 1);
                    (items[i], items[j]) = (items[j], items[i]);
                }
            }
        }

        public static List<MapRoom> Generate(int count, string[] chipIds, string[] colors, bool automaticThemes, int seed)
        {
            if (count < 1 || count > MapDocument.MaximumRoomCount)
                throw new ArgumentException($"@generationRoomRange:{MapDocument.MaximumRoomCount}");
            if (chipIds.Length == 0 || chipIds.Any(id => id == null || !MapMinimapChips.ById.ContainsKey(id)))
                throw new ArgumentException("@generationChipsRequired");
            if (colors.Length == 0 || colors.Any(c => c == null || c.Length != 7 || c[0] != '#' || !ColorText.TryParseHtmlString(c, out _)))
                throw new ArgumentException("@generationColorsRequired");
            var chips = chipIds.Distinct().Order(StringComparer.Ordinal).Select(id => MapMinimapChips.ById[id]).ToArray();
            colors = colors.Select(color => color.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToArray();
            var random = new LayoutRandom(seed);
            if (automaticThemes)
            {
                random.Shuffle(colors);
                int regions = Math.Clamp((int)Math.Ceiling(Math.Sqrt(count) / 2), 1, 6);
                colors = colors.Take(regions).ToArray();
            }
            if (colors.Length > count) throw new ArgumentException("@generationTooManyColors");

            const int unit = MapMinimapChips.Unit;
            var rooms = new List<MapRoom>(count);
            var occupied = new HashSet<(int X, int Y)>();
            var availableEdges = chips.ToDictionary(chip => chip, _ => new List<(int Room, int Side)>());
            var neighbors = new List<int>[count];
            for (int i = 0; i < count; i++)
            {
                var chip = chips[random.Next(chips.Length)];
                var edges = availableEdges[chip];
                int width = chip.Width / unit, height = chip.Height / unit;
                int x = 0, y = 0;
                neighbors[i] = new List<int>();
                while (i > 0)
                {
                    int edge = random.Next(edges.Count);
                    var (parent, side) = edges[edge];
                    MapRoom anchor = rooms[parent];
                    bool verticalEdge = side < 2;
                    int span = verticalEdge ? height : width;
                    int anchorSpan = (verticalEdge ? anchor.height : anchor.width) / unit;
                    int offsets = anchorSpan + span - 1, start = random.Next(offsets);
                    bool placed = false;
                    for (int step = 0; step < offsets; step++)
                    {
                        int offset = (start + step) % offsets - span + 1;
                        x = anchor.x / unit + (verticalEdge ? (side == 0 ? -width : anchor.width / unit) : offset);
                        y = anchor.y / unit + (verticalEdge ? offset : (side == 2 ? -height : anchor.height / unit));
                        if (!IsFree(x, y, width, height)) continue;
                        neighbors[i].Add(parent);
                        neighbors[parent].Add(i);
                        placed = true;
                        break;
                    }
                    if (placed) break;
                    // Occupancy only grows. This chip can never fit on this edge again.
                    // At least one outermost edge always has space.
                    (edges[edge], edges[^1]) = (edges[^1], edges[edge]);
                    edges.RemoveAt(edges.Count - 1);
                }
                rooms.Add(new MapRoom { id = FormattableString.Invariant($"gen_{unchecked((uint)seed):x8}_{i:x3}"),
                    name = FormattableString.Invariant($"room_{i:00}"), x = x * unit, y = y * unit, width = chip.Width, height = chip.Height });
                for (int dx = 0; dx < width; dx++)
                    for (int dy = 0; dy < height; dy++) occupied.Add((x + dx, y + dy));
                foreach (var candidates in availableEdges.Values)
                    for (int side = 0; side < 4; side++) candidates.Add((i, side));
            }

            // Flood each theme from a distinct room along actual shared edges.
            var seeds = Enumerable.Range(0, count).ToArray();
            random.Shuffle(seeds);
            var queue = new Queue<int>();
            var region = new int[count];
            Array.Fill(region, -1);
            for (int i = 0; i < colors.Length; i++) { region[seeds[i]] = i; queue.Enqueue(seeds[i]); }
            while (queue.TryDequeue(out int room))
            {
                MapMinimapShape.Apply(rooms[room], null, colors[region[room]]);
                foreach (int next in neighbors[room])
                {
                    if (region[next] != -1) continue;
                    region[next] = region[room];
                    queue.Enqueue(next);
                }
            }
            return rooms;

            bool IsFree(int x, int y, int width, int height)
            {
                for (int dx = 0; dx < width; dx++)
                    for (int dy = 0; dy < height; dy++)
                        if (occupied.Contains((x + dx, y + dy))) return false;
                return true;
            }
        }
    }
}
