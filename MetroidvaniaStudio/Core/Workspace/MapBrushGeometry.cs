using System;
using System.Collections.Generic;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    /// <summary>Deterministic integer-cell geometry shared by painting tools and previews.</summary>
    public static class MapBrushGeometry
    {
        public const int MaximumCellCount = MapDocument.MaximumRoomDimension * MapDocument.MaximumRoomDimension;
        public const int MaximumBrushSize = 16;

        /// <summary>Includes both ends; consecutive cells share an edge or corner.</summary>
        public static IEnumerable<Vector2Int> Line(Vector2Int from, Vector2Int to)
        {
            long dx = Math.Abs((long)to.x - from.x);
            long dy = Math.Abs((long)to.y - from.y);
            if (Math.Max(dx, dy) >= MaximumCellCount)
                throw new ArgumentOutOfRangeException(nameof(to), "A line cannot exceed " + MaximumCellCount + " cells.");
            int stepX = from.x < to.x ? 1 : -1;
            int stepY = from.y < to.y ? 1 : -1;
            long error = dx - dy;
            int x = from.x;
            int y = from.y;
            while (true)
            {
                yield return new Vector2Int(x, y);
                if (x == to.x && y == to.y)
                    yield break;
                long twiceError = error * 2;
                if (twiceError > -dy)
                {
                    error -= dy;
                    x += stepX;
                }
                if (twiceError < dx)
                {
                    error += dx;
                    y += stepY;
                }
            }
        }

        /// <summary>Opposite corners are inclusive. Cells are returned bottom to top, left to right.</summary>
        public static IEnumerable<Vector2Int> Rectangle(Vector2Int from, Vector2Int to, bool filled)
        {
            int minX = Math.Min(from.x, to.x), maxX = Math.Max(from.x, to.x);
            int minY = Math.Min(from.y, to.y), maxY = Math.Max(from.y, to.y);
            ValidateArea((long)maxX - minX + 1, (long)maxY - minY + 1, nameof(to));
            for (long y = minY; y <= maxY; y++)
                for (long x = minX; x <= maxX; x++)
                    if (filled || x == minX || x == maxX || y == minY || y == maxY)
                        yield return new Vector2Int((int)x, (int)y);
        }

        /// <summary>
        /// Rasterizes the ellipse inside inclusive cell bounds. Half-cell padding keeps
        /// even-sized and one-cell-wide ellipses connected and touching their bounds.
        /// An outline contains filled cells with at least one empty cardinal neighbor.
        /// </summary>
        public static IEnumerable<Vector2Int> Ellipse(Vector2Int from, Vector2Int to, bool filled)
        {
            int minX = Math.Min(from.x, to.x), maxX = Math.Max(from.x, to.x);
            int minY = Math.Min(from.y, to.y), maxY = Math.Max(from.y, to.y);
            long width = (long)maxX - minX + 1, height = (long)maxY - minY + 1;
            ValidateArea(width, height, nameof(to));
            double centerX = minX + (width - 1) * 0.5;
            double centerY = minY + (height - 1) * 0.5;
            double radiusX = width * 0.5;
            double radiusY = height * 0.5;
            bool Contains(long x, long y)
            {
                if (x < minX || x > maxX || y < minY || y > maxY)
                    return false;
                double nx = (x - centerX) / radiusX;
                double ny = (y - centerY) / radiusY;
                return nx * nx + ny * ny <= 1.0;
            }
            return Rasterize(minX, minY, maxX, maxY, filled, Contains);
        }

        /// <summary>Uses the exact Euclidean radius from center to edge, including the edge cell.</summary>
        public static IEnumerable<Vector2Int> Circle(Vector2Int center, Vector2Int edge, bool filled)
        {
            double dx = (long)edge.x - center.x;
            double dy = (long)edge.y - center.y;
            double squaredRadius = dx * dx + dy * dy;
            long extent = (long)Math.Floor(Math.Sqrt(squaredRadius));
            long diameter = extent * 2 + 1;
            ValidateArea(diameter, diameter, nameof(edge));
            long minX = (long)center.x - extent, maxX = (long)center.x + extent;
            long minY = (long)center.y - extent, maxY = (long)center.y + extent;
            ValidateCoordinates(minX, minY, maxX, maxY, nameof(edge));
            bool Contains(long x, long y)
            {
                double offsetX = x - center.x;
                double offsetY = y - center.y;
                return offsetX * offsetX + offsetY * offsetY <= squaredRadius;
            }
            return Rasterize(minX, minY, maxX, maxY, filled, Contains);
        }

        /// <summary>Visits only the four-connected matching region inside bounds; never recurses.</summary>
        public static IEnumerable<Vector2Int> FloodFill(Vector2Int seed, RectInt bounds, Func<Vector2Int, bool> matches)
        {
            if (matches == null)
                throw new ArgumentNullException(nameof(matches));
            if (bounds.width < 0 || bounds.height < 0)
                throw new ArgumentOutOfRangeException(nameof(bounds), "Fill bounds cannot have negative dimensions.");
            if (bounds.width == 0 || bounds.height == 0)
                yield break;
            ValidateArea(bounds.width, bounds.height, nameof(bounds));
            long minX = bounds.x, minY = bounds.y;
            long maxX = minX + bounds.width - 1, maxY = minY + bounds.height - 1;
            ValidateCoordinates(minX, minY, maxX, maxY, nameof(bounds));
            if (seed.x < minX || seed.x > maxX || seed.y < minY || seed.y > maxY)
                yield break;

            var visited = new HashSet<Vector2Int> { seed };
            var pending = new Queue<Vector2Int>();
            pending.Enqueue(seed);
            void Enqueue(long x, long y)
            {
                if (x < minX || x > maxX || y < minY || y > maxY)
                    return;
                var point = new Vector2Int((int)x, (int)y);
                if (visited.Add(point))
                    pending.Enqueue(point);
            }
            while (pending.Count > 0)
            {
                Vector2Int point = pending.Dequeue();
                if (!matches(point))
                    continue;
                yield return point;
                Enqueue((long)point.x + 1, point.y);
                Enqueue(point.x, (long)point.y + 1);
                Enqueue((long)point.x - 1, point.y);
                Enqueue(point.x, (long)point.y - 1);
            }
        }

        /// <summary>Expands each cell to a square. Even sizes place the extra cell on +X and +Y.</summary>
        public static IEnumerable<Vector2Int> Expand(IEnumerable<Vector2Int> points, int brushSize)
        {
            if (points == null)
                throw new ArgumentNullException(nameof(points));
            if (brushSize < 1 || brushSize > MaximumBrushSize)
                throw new ArgumentOutOfRangeException(nameof(brushSize), $"Brush size must be between 1 and {MaximumBrushSize}.");
            int offset = (brushSize - 1) / 2;
            int sourceCount = 0;
            var visited = new HashSet<Vector2Int>();
            foreach (Vector2Int center in points)
            {
                if (++sourceCount > MaximumCellCount)
                    throw new ArgumentOutOfRangeException(nameof(points), "A brush stroke cannot exceed " + MaximumCellCount + " input cells.");
                long minX = (long)center.x - offset, minY = (long)center.y - offset;
                ValidateCoordinates(minX, minY, minX + brushSize - 1, minY + brushSize - 1, nameof(points));
                for (int y = 0; y < brushSize; y++)
                {
                    for (int x = 0; x < brushSize; x++)
                    {
                        var cell = new Vector2Int((int)(minX + x), (int)(minY + y));
                        if (!visited.Add(cell))
                            continue;
                        if (visited.Count > MaximumCellCount)
                            throw new ArgumentOutOfRangeException(nameof(points), "An expanded stroke cannot exceed " + MaximumCellCount + " cells.");
                        yield return cell;
                    }
                }
            }
        }

        /// <summary>
        /// Expands an edge-or-corner-connected path without visiting the whole
        /// brush square again for every cell. The first stamp is complete; each
        /// following stamp contributes only the row and/or column entering the
        /// moved square. Revisited cells may be returned again, which is harmless
        /// for a single paint/erase transaction and keeps the work strictly
        /// bounded by the path length and brush perimeter.
        /// </summary>
        public static IEnumerable<Vector2Int> ExpandContinuousPath(IEnumerable<Vector2Int> points, int brushSize)
        {
            if (points == null)
                throw new ArgumentNullException(nameof(points));
            if (brushSize < 1 || brushSize > MaximumBrushSize)
                throw new ArgumentOutOfRangeException(nameof(brushSize), $"Brush size must be between 1 and {MaximumBrushSize}.");
            int offset = (brushSize - 1) / 2;
            int sourceCount = 0;
            bool hasPrevious = false;
            Vector2Int previous = default;
            foreach (Vector2Int center in points)
            {
                if (++sourceCount > MaximumCellCount)
                    throw new ArgumentOutOfRangeException(nameof(points), "A brush stroke cannot exceed " + MaximumCellCount + " input cells.");
                long left = (long)center.x - offset, bottom = (long)center.y - offset;
                ValidateCoordinates(left, bottom, left + brushSize - 1, bottom + brushSize - 1, nameof(points));
                if (!hasPrevious)
                {
                    for (int y = 0; y < brushSize; y++)
                        for (int x = 0; x < brushSize; x++)
                            yield return new Vector2Int((int)(left + x), (int)(bottom + y));
                    previous = center;
                    hasPrevious = true;
                    continue;
                }

                long deltaX = (long)center.x - previous.x, deltaY = (long)center.y - previous.y;
                if (Math.Abs(deltaX) > 1 || Math.Abs(deltaY) > 1)
                    throw new ArgumentException("A continuous brush path must move by at most one cell per step.", nameof(points));
                if (deltaX == 0 && deltaY == 0) continue;
                long? edgeX = null;
                if (deltaX != 0)
                {
                    edgeX = deltaX > 0 ? left + brushSize - 1 : left;
                    for (int y = 0; y < brushSize; y++)
                        yield return new Vector2Int((int)edgeX.Value, (int)(bottom + y));
                }
                if (deltaY != 0)
                {
                    long edgeY = deltaY > 0 ? bottom + brushSize - 1 : bottom;
                    for (int x = 0; x < brushSize; x++)
                        if (left + x != edgeX)
                            yield return new Vector2Int((int)(left + x), (int)edgeY);
                }
                previous = center;
            }
        }

        /// <summary>Horizontal mirrors X; vertical mirrors Y.</summary>
        public static TileShape FlipShape(TileShape shape, bool horizontal)
        {
            ValidateShape(shape);
            if (shape == TileShape.Solid)
                return shape;
            switch (shape)
            {
                case TileShape.BottomLeft: return horizontal ? TileShape.BottomRight : TileShape.TopLeft;
                case TileShape.BottomRight: return horizontal ? TileShape.BottomLeft : TileShape.TopRight;
                case TileShape.TopLeft: return horizontal ? TileShape.TopRight : TileShape.BottomLeft;
                default: return horizontal ? TileShape.TopLeft : TileShape.BottomRight;
            }
        }

        /// <summary>Rotates the filled corner by 90 degrees using +Y-up map coordinates.</summary>
        public static TileShape RotateShape(TileShape shape, bool clockwise)
        {
            ValidateShape(shape);
            if (shape == TileShape.Solid)
                return shape;
            switch (shape)
            {
                case TileShape.BottomLeft: return clockwise ? TileShape.TopLeft : TileShape.BottomRight;
                case TileShape.BottomRight: return clockwise ? TileShape.BottomLeft : TileShape.TopRight;
                case TileShape.TopLeft: return clockwise ? TileShape.TopRight : TileShape.BottomLeft;
                default: return clockwise ? TileShape.BottomRight : TileShape.TopLeft;
            }
        }

        private static IEnumerable<Vector2Int> Rasterize(long minX, long minY, long maxX, long maxY,
            bool filled, Func<long, long, bool> contains)
        {
            for (long y = minY; y <= maxY; y++)
            {
                for (long x = minX; x <= maxX; x++)
                {
                    if (!contains(x, y))
                        continue;
                    if (filled || !contains(x - 1, y) || !contains(x + 1, y)
                        || !contains(x, y - 1) || !contains(x, y + 1))
                        yield return new Vector2Int((int)x, (int)y);
                }
            }
        }

        private static void ValidateArea(long width, long height, string parameter)
        {
            if (width < 1 || height < 1 || width > MaximumCellCount || height > MaximumCellCount
                || width * height > MaximumCellCount)
                throw new ArgumentOutOfRangeException(parameter, "A tool's bounding area cannot exceed " + MaximumCellCount + " cells.");
        }

        private static void ValidateCoordinates(long minX, long minY, long maxX, long maxY, string parameter)
        {
            if (minX < int.MinValue || minY < int.MinValue || maxX > int.MaxValue || maxY > int.MaxValue)
                throw new ArgumentOutOfRangeException(parameter, "Tool geometry exceeds integer cell coordinates.");
        }

        private static void ValidateShape(TileShape shape)
        {
            if (shape < TileShape.Solid || shape > TileShape.TopRight)
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }
}
