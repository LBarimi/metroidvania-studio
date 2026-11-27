using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static MetroidvaniaStudio.MapTilemapGenerator;

namespace MetroidvaniaStudio;

/// <summary>Builds side-view terrain around the actual entrances of one minimap room. Y increases upward.</summary>
internal sealed class RoomTerrainLayout
{
    private enum RoomForm { Hollow, Ridge, Ravine, Shaft, Alcove }
    private enum Tile : byte { Air, Rock, Landing, Bridge, Rung }

    private readonly int width, height, tier;
    private readonly List<Door> doors;
    private readonly string biome;
    private readonly MapMinimapGenerator.LayoutRandom random;
    private readonly Tile[,] tiles;
    private readonly bool[,] headroom;
    private readonly int[] ground;
    private readonly RoomForm form;
    private readonly int focus, span, relief;
    private int shelfDepth;

    internal RoomTerrainLayout(int width, int height, List<Door> doors, string biome, int tier, MapMinimapGenerator.LayoutRandom random)
    {
        this.width = width;
        this.height = height;
        this.doors = doors;
        this.biome = biome;
        this.tier = tier;
        this.random = random;
        tiles = new Tile[width, height];
        headroom = new bool[width, height];
        ground = new int[width];

        form = ChooseForm();
        focus = width / 3 + random.Next(Math.Max(1, width / 3));
        span = Math.Max(3, Math.Min(width / 3, 5 + random.Next(6)));
        relief = Math.Clamp(Math.Min(height - 6, width / 5 + 1), 1, 7);
    }

    private RoomForm ChooseForm()
    {
        if (height >= width * 1.5) return RoomForm.Shaft;
        if (doors.Count <= 1 && (width < 24 || random.Next(3) != 0)) return RoomForm.Alcove;
        if (width < 16) return RoomForm.Hollow;
        int roll = random.Next(5);
        if (biome == "biome-ancient-ruins" || biome == "biome-rock") roll++;
        return roll switch { 0 or 3 => RoomForm.Hollow, 1 or 4 => RoomForm.Ravine, _ => RoomForm.Ridge };
    }

    internal void Paint(MapRoom room, string material, int roomCount)
    {
        long roomBytes = MapDocumentStore.MaximumFileBytes / roomCount;
        int metadataBytes = Encoding.UTF8.GetByteCount(MapJson.ToJson(room));
        int budget = (int)((roomBytes - metadataBytes) / (62 + Encoding.UTF8.GetByteCount(material)));
        // When two outlines would consume the budget, use thin shelves and longer ladder flights.
        bool compact = budget < 4 * (width + height);
        shelfDepth = compact || biome == "biome-ice-cavern" ? 1 : 3;
        ShapeCavern(compact);
        if (form == RoomForm.Ravine) SpanRavine();
        ConnectUpperPassages(compact ? 10 : 5);
        OpenDoors();

        // A 1,024-room document must fit the same save format as a single room. On
        // large worlds retain the playable contour first, then pack the buried rock.
        int contour = 0;
        bool Buried(int x, int y) => tiles[x,y] == Tile.Rock && x > 0 && x < width-1 && y > 0 && y < height-1 &&
            Solid(x-1,y) && Solid(x+1,y) && Solid(x,y-1) && Solid(x,y+1);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (tiles[x,y] != Tile.Air && !Buried(x,y)) contour++;
        int fill = Math.Max(0, budget - contour);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var tile = tiles[x,y];
                if (tile == Tile.Air || (Buried(x,y) && fill-- <= 0)) continue;
                room.foreground.Add(new MapCell { x = x, y = y, material = tile switch {
                    Tile.Rung => Ladder, Tile.Bridge => Floor, _ => material } });
            }
    }

    private bool Solid(int x, int y) => tiles[x,y] is Tile.Rock or Tile.Landing;

    private void ShapeCavern(bool compact)
    {
        int left = SideEntry(0), right = SideEntry(width - 1);
        int roofFocus = width - 1 - focus;
        // Put a sheltered end in a dead end, and leave its longest approach open.
        bool farRight = doors.Any(d => d.Side && d.X == 0) || !doors.Any(d => d.Side && d.X == width - 1);
        int baseHeight = form == RoomForm.Ravine ? Math.Min(height - 5, relief + 2) : 2;
        for (int x = 0; x < width; x++)
        {
            int distance = Math.Abs(x - focus);
            int feature = Math.Min(relief, Math.Max(0, (span + 2 - distance) / (biome == "biome-ice-cavern" ? 3 : 2)));
            int rise = form switch {
                RoomForm.Ravine => -Math.Min(relief, Math.Max(0, span - distance)),
                RoomForm.Hollow => Math.Min(relief, distance / 3),
                RoomForm.Alcove => Math.Clamp(((farRight ? x : width - 1 - x) - width / 3) / 3, 0, relief),
                RoomForm.Shaft => Math.Min(3, distance / 3),
                _ => feature
            };
            int surface = baseHeight + rise;
            if (biome == "biome-volcanic") surface += Math.Max(0, 3 - Math.Abs(x - roofFocus));
            if (biome == "biome-ancient-ruins" && x > 2 && x < width - 3) surface = 1 + (surface / 2) * 2;
            // Blend a low side door into the earth so the threshold is a walkable bank.
            ground[x] = Math.Clamp(surface, 1, height - 5);
        }
        BlendBank(0, 1, left);
        BlendBank(width - 1, -1, right);
        for (int x = 1; x < width; x++) ground[x] = Math.Min(ground[x], ground[x-1] + 1);
        for (int x = width - 2; x >= 0; x--) ground[x] = Math.Min(ground[x], ground[x+1] + 1);

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < ground[x]; y++) tiles[x,y] = Tile.Rock;
            int end = Math.Min(x, width - 1 - x);
            int eave = Math.Max(0, Math.Min(4, width / 6) - end);
            int vault = Math.Max(0, (span - Math.Abs(x - roofFocus)) / (form == RoomForm.Hollow ? 2 : 3));
            int roof = compact ? height - 1 : Math.Max(ground[x] + 4, height - 1 - Math.Max(eave, vault));
            for (int y = roof; y < height; y++) tiles[x,y] = Tile.Rock;
            if (end == 0)
                for (int y = ground[x]; y < roof; y++) tiles[x,y] = Tile.Rock;
            else Reserve(x, ground[x]);
        }
        if (form == RoomForm.Shaft)
        {
            int y = 5 + random.Next(4), side = random.Next(2);
            while (y < height - 3)
            {
                int reach = Math.Clamp(width / 3, 2, 7);
                for (int dx = 1; dx <= reach; dx++)
                {
                    int x = side == 0 ? dx : width - 1 - dx;
                    int lower = Math.Max(ground[x] + 3, y - (reach - dx));
                    for (int h = lower; h <= y; h++) tiles[x,h] = Tile.Rock;
                }
                y += 7 + random.Next(5);
                side = 1 - side;
            }
        }
    }

    private int SideEntry(int x) => doors.Where(d => d.Side && d.X == x)
        .Select(d => d.Y).DefaultIfEmpty(-1).Min();

    private void BlendBank(int edge, int direction, int entry)
    {
        if (entry < 0) return;
        int target = Math.Min(entry, Math.Min(height - 5, Math.Max(3, Math.Min(10, width / 3))));
        int reach = Math.Min(width / 2, 3 + Math.Abs(target - ground[edge]));
        for (int i = 0; i < reach; i++)
        {
            int x = edge + i * direction;
            ground[x] = Math.Clamp(ground[x], target - Math.Max(0, i - 1), target + Math.Max(0, i - 1));
        }
    }

    private void SpanRavine()
    {
        int level = Math.Min(ground[focus] + relief, Math.Min(ground.Take(focus).Max(), ground.Skip(focus + 1).Max()));
        int a = focus, b = focus;
        while (a > 1 && ground[a] < level) a--;
        while (b < width-2 && ground[b] < level) b++;
        if (b - a < 4) return;
        int gap = Math.Min(tier, b - a - 3);
        for (int x = a; x <= b; x++)
        {
            if (x >= focus && x < focus + gap) continue;
            if (ground[x] < level) tiles[x,level-1] = Tile.Bridge;
            Reserve(x, level);
        }
    }

    private void ConnectUpperPassages(int flightHeight)
    {
        int maxGround = ground.Max(), roofLanding = height - 4;
        var stops = new SortedDictionary<int, List<int>>();
        void Stop(int y, int x)
        {
            if (!stops.TryGetValue(y, out var xs)) stops.Add(y, xs = new List<int>());
            xs.Add(x);
        }
        foreach (var d in doors)
        {
            if (!d.Side)
            {
                if (d.Y == height-1) Stop(roofLanding, d.X);
                continue;
            }
            if (d.Y == ground[d.X]) continue;
            if (d.Y >= maxGround + 3) { Stop(d.Y, d.X); continue; }
            int inner = d.X == 0 ? 2 : width - 3;
            Shelf(inner, d.X, d.Y);
            Climb(inner, Math.Min(d.Y, ground[inner]), Math.Max(d.Y, ground[inner]));
        }

        // An unconnected upper end of a shaft has a lookout to climb to. A level
        // corridor can stay entirely on foot, with no obligatory ladder or platform.
        if (form == RoomForm.Shaft && (stops.Count == 0 || stops.Last().Key < height / 2))
            Stop(height - 5 - random.Next(3), random.Next(2) == 0 ? 2 : width - 3);
        if (stops.Count == 0) return;

        int inset = Math.Clamp(width / 4, 2, 6);
        int x = stops.First().Value[0] < width / 2 ? inset : width - 1 - inset;
        int feet = ground[x];
        foreach (var (level, exits) in stops)
        {
            // Keep the ascent on short flights. Each flight ends on a terrace where
            // the player changes direction and can see the next landing.
            while (level > feet)
            {
                int remaining = level - feet;
                int rise = remaining <= flightHeight + 3 ? remaining : Math.Min(remaining - 3, flightHeight + random.Next(4));
                int y = feet + rise;
                int toward = x < width / 2 ? 1 : -1;
                int next = Math.Clamp(x + toward * (4 + random.Next(5) + tier), 2, width - 3);
                int left = Math.Min(x, next), right = Math.Max(x, next);
                if (y == level) { left = Math.Min(left, exits.Min()); right = Math.Max(right, exits.Max()); }
                if (left <= 3) left = 0;
                if (right >= width - 4) right = width - 1;
                Shelf(left, right, y);
                bool jumps = width >= 12 && tier > 0 && (rise <= 2 || (form == RoomForm.Shaft && random.Next(3) < tier));
                if (jumps) JumpFlight(x, next, feet, y);
                else Climb(x, feet, y);
                x = next;
                feet = y;
            }
        }
    }

    // Stagger short landings across the shaft. The one-way tip can be reached
    // from below. The solid half lets the player stop and line up the next jump.
    private void JumpFlight(int start, int end, int feet, int top)
    {
        int takeoff = start, totalRise = top - feet, bottom = feet;
        while (feet < top)
        {
            int y = Math.Min(top, feet + 2);
            int next = start + (end - start) * (y - bottom) / totalRise;
            if (y < top) Shelf(takeoff, next, y);
            for (int h = feet; h <= y + 1; h++) Reserve(takeoff, h);
            tiles[takeoff,y-1] = Tile.Bridge;
            takeoff = next;
            feet = y;
        }
    }

    private void Shelf(int a, int b, int feet)
    {
        int left = Math.Min(a,b), right = Math.Max(a,b);
        for (int x = left; x <= right; x++)
        {
            int edge = Math.Min(x - left, right - x);
            int depth = Math.Min(shelfDepth, 1 + edge / 3);
            for (int n = 1; n <= depth && feet >= n; n++)
            {
                int y = feet - n;
                if (tiles[x,y] is Tile.Rung or Tile.Landing or Tile.Bridge) continue;
                if (n == 1) tiles[x,y] = headroom[x,y] ? Tile.Bridge : Tile.Landing;
                else if (!headroom[x,y]) tiles[x,y] = Tile.Rock;
            }
            if (x > 0 && x < width - 1) Reserve(x, feet);
        }
    }

    private void Reserve(int x, int feet)
    {
        // Two cells fit the character. The third lets its head clear a one-tile step.
        for (int y = feet; y < height && y < feet + Door.Headroom; y++)
        {
            headroom[x,y] = true;
            if (tiles[x,y] == Tile.Landing) tiles[x,y] = Tile.Bridge;
            else if (tiles[x,y] == Tile.Rock) tiles[x,y] = Tile.Air;
        }
    }

    private void Climb(int x, int lower, int upper)
    {
        for (int y = lower; y <= upper; y++)
        {
            Reserve(x, y);
            tiles[x,y] = Tile.Rung;
        }
    }

    private void OpenDoors()
    {
        // Only the sockets shared with the minimap may cross a room boundary.
        for (int x = 0; x < width; x++) tiles[x,0] = tiles[x,height-1] = Tile.Rock;
        for (int y = 0; y < height; y++) tiles[0,y] = tiles[width-1,y] = Tile.Rock;
        foreach (var d in doors)
        {
            if (d.Side)
            {
                int inner = d.X == 0 ? 1 : width - 2;
                Reserve(d.X, d.Y);
                Reserve(inner, d.Y);
                continue;
            }
            bool top = d.Y == height - 1;
            int lower = top ? height - 4 : 0, upper = top ? height - 1 : ground[d.X];
            Climb(d.X, lower, upper);
            // Leave the full portal width clear beside the ladder.
            int outer = d.X + d.Width - 1;
            for (int y = lower; y <= upper; y++)
                if (tiles[outer,y] != Tile.Rung) tiles[outer,y] = Tile.Air;
            if (!top)
            {
                int lip = ground[outer] - 1;
                if (tiles[outer,lip] != Tile.Rung) tiles[outer,lip] = Tile.Bridge;
            }
        }
    }
}
