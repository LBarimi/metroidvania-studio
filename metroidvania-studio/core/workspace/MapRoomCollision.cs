using System;
using System.Collections.Generic;
using System.Linq;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    /// <summary>Resolve actual overlap only. A free destination keeps its exact gap, regardless of proximity.</summary>
    public static class MapRoomCollision
    {
        public static Vector2Int Resolve(MapRoom source, Vector2Int delta, IReadOnlyList<MapRoom> rooms)
        {
            long x = (long)source.x + delta.x, y = (long)source.y + delta.y;
            if (x < int.MinValue || y < int.MinValue || x + source.width > int.MaxValue || y + source.height > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(delta), "The moved room exceeds the supported coordinate range.");
            var targets = rooms.Where(r => r.id != source.id).ToArray();
            var horizontal = targets.Where(r => y < (long)r.y + r.height && y + source.height > r.y)
                .Select(r => ((long)r.x - source.width, (long)r.x + r.width));
            var vertical = targets.Where(r => x < (long)r.x + r.width && x + source.width > r.x)
                .Select(r => ((long)r.y - source.height, (long)r.y + r.height));
            var candidates = new List<(long x, long y)>();
            foreach (long value in Exits(x, horizontal)) candidates.Add((value, y));
            foreach (long value in Exits(y, vertical)) candidates.Add((x, value));
            var valid = candidates.Where(p => p.x >= int.MinValue && p.y >= int.MinValue
                && p.x + source.width <= int.MaxValue && p.y + source.height <= int.MaxValue
                && p.x - source.x >= int.MinValue && p.x - source.x <= int.MaxValue
                && p.y - source.y >= int.MinValue && p.y - source.y <= int.MaxValue)
                .OrderBy(p => Math.Abs(p.x - x) + Math.Abs(p.y - y)).ToArray();
            if (valid.Length == 0) throw new InvalidOperationException("There is no non-overlapping room position within the supported range.");
            return new Vector2Int((int)(valid[0].x - source.x), (int)(valid[0].y - source.y));
        }

        private static IEnumerable<long> Exits(long at, IEnumerable<(long left, long right)> intervals)
        {
            var sorted = intervals.OrderBy(p => p.left).ThenBy(p => p.right).ToArray();
            for (int i = 0; i < sorted.Length; i++)
            {
                long left = sorted[i].left, right = sorted[i].right;
                // Open intervals: a shared endpoint is a valid exact fit.
                while (i + 1 < sorted.Length && sorted[i + 1].left < right) right = Math.Max(right, sorted[++i].right);
                if (left < at && at < right) { yield return left; yield return right; yield break; }
            }
            yield return at;
        }
    }
}
