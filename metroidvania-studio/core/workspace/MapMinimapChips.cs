using System;
using System.Collections.Generic;
using System.Linq;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public static class MapMinimapChips
    {
        public const int Unit = 8;
        public const string DefaultColor = "#c72b36";

        public readonly record struct Chip(int Width, int Height)
        {
            public string Id => FormattableString.Invariant($"{Width}x{Height}");
        }

        private static readonly Chip[] Horizontal = [new(16, 8), new(24, 8), new(32, 16), new(48, 24)];
        private static readonly Chip[] Wide = [new(24, 16), new(32, 24), new(40, 24), new(48, 32), new(64, 32)];
        private static readonly Chip[] Long = [new(32, 8), new(48, 8), new(64, 8), new(96, 8), new(64, 16)];
        private static Chip[] Rotate(Chip[] chips) => chips.Select(c => new Chip(c.Height, c.Width)).ToArray();

        public static readonly (string Label, Chip[] Chips)[] Groups =
        [
            ("chipHorizontal", Horizontal), ("chipVertical", Rotate(Horizontal)),
            ("chipSquare", [new(8, 8), new(16, 16), new(24, 24), new(32, 32), new(48, 48), new(64, 64)]),
            ("chipWideHorizontal", Wide), ("chipWideVertical", Rotate(Wide)),
            ("chipLongHorizontal", Long), ("chipLongVertical", Rotate(Long))
        ];
        public static readonly IReadOnlyDictionary<string, Chip> ById =
            Groups.SelectMany(group => group.Chips).ToDictionary(chip => chip.Id, StringComparer.Ordinal);

        // Every existing room edge must fall on a grid line, including on older, offset maps.
        public static (int X, int Y, int Step) Grid(IReadOnlyList<MapRoom> rooms)
        {
            if (rooms.Count == 0) return (0, 0, Unit);
            int x = rooms[0].x, y = rooms[0].y, step = Unit;
            foreach (MapRoom room in rooms)
            {
                while (((long)room.x - x) % step != 0 || ((long)room.y - y) % step != 0
                    || room.width % step != 0 || room.height % step != 0) step /= 2;
                if (step == 1) break;
            }
            return (Remainder(x, step), Remainder(y, step), step);
        }

        public static RectInt BoundsAt(string chipId, int x, int y, IReadOnlyList<MapRoom> rooms)
        {
            if (!ById.TryGetValue(chipId, out Chip chip))
                throw new ArgumentException($"Unknown minimap chip '{chipId}'. Use a size listed in capabilities.");
            var grid = Grid(rooms);
            return new RectInt(checked(x - Remainder((long)x - grid.X, grid.Step)),
                checked(y - Remainder((long)y - grid.Y, grid.Step)), chip.Width, chip.Height);
        }

        private static int Remainder(long n, int step) => (int)((n % step + step) % step);
    }
}
