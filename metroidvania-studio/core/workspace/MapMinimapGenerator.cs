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

            var neighbors = new List<int>[count];
            var rooms = GrowRoutes(chips, seed, random, neighbors);

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
        }

        private readonly record struct Attachment(int Parent, int Side, int X, int Y);

        private static List<MapRoom> GrowRoutes(MapMinimapChips.Chip[] chips, int seed, LayoutRandom random, List<int>[] neighbors)
        {
            const int unit = MapMinimapChips.Unit;
            var rooms = new List<MapRoom>(neighbors.Length);
            var occupied = new Dictionary<(int X, int Y), int>();
            int heading = random.Next(4), remaining = 0;
            for (int i = 0; i < neighbors.Length; i++)
            {
                var chip = chips[random.Next(chips.Length)];
                int width = chip.Width / unit, height = chip.Height / unit, x = 0, y = 0;
                neighbors[i] = new List<int>();
                if (i > 0)
                {
                    int parent = i - 1;
                    if (remaining == 0)
                    {
                        // Start a branch away from busy junctions, then extend its tip for several rooms.
                        parent = random.Next(i);
                        int other = random.Next(i);
                        if (neighbors[parent].Count >= 3) parent = other;
                        heading = random.Next(4);
                        remaining = 3 + random.Next(6);
                    }
                    else if (random.Next(3) == 0) heading = (heading < 2 ? 2 : 0) + random.Next(2);
                    var next = FindAttachment(rooms, occupied, width, height, parent, heading, random);
                    neighbors[i].Add(next.Parent);
                    neighbors[next.Parent].Add(i);
                    (x, y, heading) = (next.X, next.Y, next.Side);
                    remaining--;
                }
                rooms.Add(new MapRoom { id = FormattableString.Invariant($"gen_{unchecked((uint)seed):x8}_{i:x3}"),
                    name = FormattableString.Invariant($"room_{i:00}"), x = x * unit, y = y * unit, width = chip.Width, height = chip.Height });
                for (int dx = 0; dx < width; dx++)
                    for (int dy = 0; dy < height; dy++) occupied.Add((x + dx, y + dy), i);
            }
            return rooms;
        }

        private static Attachment FindAttachment(List<MapRoom> rooms, Dictionary<(int X, int Y), int> occupied,
            int width, int height, int preferredParent, int heading, LayoutRandom random)
        {
            const int unit = MapMinimapChips.Unit;
            int start = random.Next(rooms.Count), bestCost = int.MaxValue;
            Attachment best = default;
            // Try other anchors when a tip runs into a cluster. An outermost face always has space.
            for (int attempt = 0; attempt <= rooms.Count; attempt++)
            {
                int parent = attempt == 0 ? preferredParent : (start + attempt - 1) % rooms.Count;
                if (attempt > 0 && parent == preferredParent) continue;
                MapRoom anchor = rooms[parent];
                for (int side = 0; side < 4; side++)
                {
                    bool horizontal = side < 2;
                    int span = horizontal ? height : width;
                    int anchorSpan = (horizontal ? anchor.height : anchor.width) / unit;
                    int offsets = anchorSpan + span - 1, first = random.Next(offsets), samples = Math.Min(offsets, 4);
                    for (int s = 0; s < samples; s++)
                    {
                        int offset = (first + s * offsets / samples) % offsets - span + 1;
                        int x = anchor.x / unit + (horizontal ? (side == 0 ? -width : anchor.width / unit) : offset);
                        int y = anchor.y / unit + (horizontal ? offset : (side == 2 ? -height : anchor.height / unit));
                        int cost = side == heading ? 0 : side == (heading ^ 1) ? 14 : 5;
                        cost += random.Next(6);
                        if (cost >= bestCost || !Fits(x, y)) continue;
                        // Count nearby cells from other rooms. A shared wall costs more than a narrow gap.
                        cost += ClearanceCost(x, y, parent);
                        if (cost >= bestCost) continue;
                        bestCost = cost;
                        best = new Attachment(parent, side, x, y);
                    }
                }
                // Keep an open route on its chosen anchor. In tight quarters compare up to eight
                // alternatives, continuing the search only if the chip still has nowhere to fit.
                if (bestCost <= 10 || (attempt >= 8 && bestCost < int.MaxValue)) return best;
            }
            if (bestCost < int.MaxValue) return best;
            throw new InvalidOperationException("No exterior room edge found.");

            bool Fits(int x, int y)
            {
                for (int dx = 0; dx < width; dx++)
                    for (int dy = 0; dy < height; dy++)
                        if (occupied.ContainsKey((x + dx, y + dy))) return false;
                return true;
            }

            int ClearanceCost(int x, int y, int parent)
            {
                int cost = 0;
                for (int distance = 1; distance <= 2; distance++)
                {
                    int weight = distance == 1 ? 6 : 2;
                    for (int dx = 0; dx < width; dx++)
                    {
                        if (OtherRoom(x + dx, y - distance)) cost += weight;
                        if (OtherRoom(x + dx, y + height - 1 + distance)) cost += weight;
                    }
                    for (int dy = 0; dy < height; dy++)
                    {
                        if (OtherRoom(x - distance, y + dy)) cost += weight;
                        if (OtherRoom(x + width - 1 + distance, y + dy)) cost += weight;
                    }
                }
                return cost;
                bool OtherRoom(int cx, int cy) => occupied.TryGetValue((cx, cy), out int owner) && owner != parent;
            }
        }
    }
}
