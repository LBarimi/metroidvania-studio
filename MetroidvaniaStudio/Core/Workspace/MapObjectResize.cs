using System;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    /// <summary>Handles refer to unrotated box axes (-1, 0, 1), before signed scale and rotation.</summary>
    public static class MapObjectResize
    {
        public static readonly Vector2Int[] Handles = { new Vector2Int(-1,-1), new Vector2Int(0,-1), new Vector2Int(1,-1),
            new Vector2Int(1,0), new Vector2Int(1,1), new Vector2Int(0,1), new Vector2Int(-1,1), new Vector2Int(-1,0) };

        public static Vector2 HandlePosition(MapObject item, Vector2Int handle)
        {
            ValidateHandle(handle);
            return Center(item) + Rotate(new Vector2(item.width * item.scaleX * handle.x, item.height * item.scaleY * handle.y) * .5f, item.rotation);
        }

        public static Vector2 SizeDelta(MapObject item, Vector2 worldDelta, Vector2Int handle)
        {
            ValidateHandle(handle);
            if (Math.Abs(item.scaleX) < .000001f || Math.Abs(item.scaleY) < .000001f)
                throw new InvalidOperationException("Cannot resize an object whose scale is zero.");
            Vector2 local = Rotate(worldDelta, -item.rotation);
            return new Vector2(local.x / item.scaleX * handle.x, local.y / item.scaleY * handle.y);
        }

        public static void Apply(MapObject item, Vector2 sizeDelta, Vector2Int handle, Vector2 minimum)
        {
            ValidateHandle(handle);
            float width = handle.x == 0 ? item.width : Checked(Math.Max(minimum.x, (double)item.width + sizeDelta.x));
            float height = handle.y == 0 ? item.height : Checked(Math.Max(minimum.y, (double)item.height + sizeDelta.y));
            if (width == item.width && height == item.height) return;
            Vector2 shift = Rotate(new Vector2((width - item.width) * item.scaleX * handle.x,
                (height - item.height) * item.scaleY * handle.y) * .5f, item.rotation);
            Vector2 center = Center(item);
            item.x = Checked((double)center.x + shift.x - width * .5);
            item.y = Checked((double)center.y + shift.y - height * .5);
            item.width = width; item.height = height;
        }

        public static void ValidateHandle(Vector2Int handle)
        {
            if (handle == Vector2Int.zero || Math.Abs((long)handle.x) > 1 || Math.Abs((long)handle.y) > 1)
                throw new ArgumentOutOfRangeException(nameof(handle), "A resize handle must be an edge or corner of the unit box.");
        }

        private static Vector2 Center(MapObject item) => new Vector2(Checked(item.x + item.width * .5), Checked(item.y + item.height * .5));
        private static Vector2 Rotate(Vector2 value, float degrees)
        {
            double angle = degrees * Math.PI / 180, cosine = Math.Cos(angle), sine = Math.Sin(angle);
            return new Vector2(Checked(value.x * cosine - value.y * sine), Checked(value.x * sine + value.y * cosine));
        }
        private static float Checked(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > float.MaxValue)
                throw new InvalidOperationException("Resize coordinates exceed the supported range.");
            return (float)value;
        }
    }
}
