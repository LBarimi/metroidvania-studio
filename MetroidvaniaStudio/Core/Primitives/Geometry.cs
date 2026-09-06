using System;
using System.Globalization;

namespace MetroidvaniaStudio.Primitives
{
    [Serializable]
    public struct Vector2 : IEquatable<Vector2>
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0, 0);
        public static Vector2 one => new Vector2(1, 1);
        public static Vector2 up => new Vector2(0, 1);
        public static Vector2 down => new Vector2(0, -1);
        public static Vector2 right => new Vector2(1, 0);
        public static Vector2 left => new Vector2(-1, 0);
        public float sqrMagnitude => x * x + y * y;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public Vector2 normalized => magnitude > 0.00001f ? this / magnitude : zero;
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator -(Vector2 a) => new Vector2(-a.x, -a.y);
        public static Vector2 operator *(Vector2 a, float b) => new Vector2(a.x * b, a.y * b);
        public static Vector2 operator *(float b, Vector2 a) => a * b;
        public static Vector2 operator /(Vector2 a, float b) => new Vector2(a.x / b, a.y / b);
        // Geometry comparisons tolerate float rounding; dictionary keys retain exact equality.
        public static bool operator ==(Vector2 a, Vector2 b) => (a - b).sqrMagnitude < 9.99999944E-11f;
        public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);
        public bool Equals(Vector2 other) => x.Equals(other.x) && y.Equals(other.y);
        public override bool Equals(object obj) => obj is Vector2 other && Equals(other);
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2);
        public static float Dot(Vector2 a, Vector2 b) => a.x * b.x + a.y * b.y;
        public static float Distance(Vector2 a, Vector2 b) => (a - b).magnitude;
        public static Vector2 Min(Vector2 a, Vector2 b) => new Vector2(MathF.Min(a.x, b.x), MathF.Min(a.y, b.y));
        public static Vector2 Max(Vector2 a, Vector2 b) => new Vector2(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y));
        public static Vector2 Scale(Vector2 a, Vector2 b) => new Vector2(a.x * b.x, a.y * b.y);
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({x}, {y})");
    }

    [Serializable]
    public struct Vector2Int : IEquatable<Vector2Int>
    {
        public int x, y;
        public Vector2Int(int x, int y) { this.x = x; this.y = y; }
        public static Vector2Int zero => new Vector2Int(0, 0);
        public static Vector2Int one => new Vector2Int(1, 1);
        public static Vector2Int up => new Vector2Int(0, 1);
        public static Vector2Int down => new Vector2Int(0, -1);
        public static Vector2Int right => new Vector2Int(1, 0);
        public static Vector2Int left => new Vector2Int(-1, 0);
        public static Vector2Int operator +(Vector2Int a, Vector2Int b) => new Vector2Int(a.x + b.x, a.y + b.y);
        public static Vector2Int operator -(Vector2Int a, Vector2Int b) => new Vector2Int(a.x - b.x, a.y - b.y);
        public static Vector2Int operator -(Vector2Int a) => new Vector2Int(-a.x, -a.y);
        public static Vector2Int operator *(Vector2Int a, int b) => new Vector2Int(a.x * b, a.y * b);
        public static Vector2Int operator *(int b, Vector2Int a) => a * b;
        public static bool operator ==(Vector2Int a, Vector2Int b) => a.x == b.x && a.y == b.y;
        public static bool operator !=(Vector2Int a, Vector2Int b) => !(a == b);
        public bool Equals(Vector2Int other) => this == other;
        public override bool Equals(object obj) => obj is Vector2Int other && Equals(other);
        public override int GetHashCode() => unchecked(x * 73856093 ^ y * 83492791);
        public static implicit operator Vector2(Vector2Int value) => new Vector2(value.x, value.y);
        public static Vector2Int FloorToInt(Vector2 value) => new Vector2Int(MathEx.FloorToInt(value.x), MathEx.FloorToInt(value.y));
        public static Vector2Int CeilToInt(Vector2 value) => new Vector2Int(MathEx.CeilToInt(value.x), MathEx.CeilToInt(value.y));
        public static Vector2Int RoundToInt(Vector2 value) => new Vector2Int(MathEx.RoundToInt(value.x), MathEx.RoundToInt(value.y));
        public override string ToString() => $"({x}, {y})";
    }

    [Serializable]
    public struct Rect : IEquatable<Rect>
    {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height) { this.x = x; this.y = y; this.width = width; this.height = height; }
        public Rect(Vector2 position, Vector2 size) : this(position.x, position.y, size.x, size.y) { }
        public float xMin { get => x; set { float max = xMax; x = value; width = max - x; } }
        public float yMin { get => y; set { float max = yMax; y = value; height = max - y; } }
        public float xMax { get => x + width; set => width = value - x; }
        public float yMax { get => y + height; set => height = value - y; }
        public Vector2 min { get => new Vector2(xMin, yMin); set { xMin = value.x; yMin = value.y; } }
        public Vector2 max { get => new Vector2(xMax, yMax); set { xMax = value.x; yMax = value.y; } }
        public Vector2 position { get => new Vector2(x, y); set { x = value.x; y = value.y; } }
        public Vector2 size { get => new Vector2(width, height); set { width = value.x; height = value.y; } }
        public Vector2 center { get => new Vector2(x + width / 2, y + height / 2); set { x = value.x - width / 2; y = value.y - height / 2; } }
        public bool Contains(Vector2 p) => p.x >= xMin && p.x < xMax && p.y >= yMin && p.y < yMax;
        public bool Overlaps(Rect other) => other.xMax > xMin && other.xMin < xMax && other.yMax > yMin && other.yMin < yMax;
        public static Rect MinMaxRect(float minX, float minY, float maxX, float maxY) => new Rect(minX, minY, maxX - minX, maxY - minY);
        public static bool operator ==(Rect a, Rect b) => a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height;
        public static bool operator !=(Rect a, Rect b) => !(a == b);
        public bool Equals(Rect other) => this == other;
        public override bool Equals(object obj) => obj is Rect other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(x, y, width, height);
        public override string ToString() => $"({x}, {y}, {width}, {height})";
    }

    [Serializable]
    public struct RectInt : IEquatable<RectInt>
    {
        public int x, y, width, height;
        public RectInt(int x, int y, int width, int height) { this.x = x; this.y = y; this.width = width; this.height = height; }
        public RectInt(Vector2Int position, Vector2Int size) : this(position.x, position.y, size.x, size.y) { }
        public int xMin { get => Math.Min(x, x + width); set { int max = xMax; x = value; width = max - x; } }
        public int yMin { get => Math.Min(y, y + height); set { int max = yMax; y = value; height = max - y; } }
        public int xMax { get => Math.Max(x, x + width); set => width = value - x; }
        public int yMax { get => Math.Max(y, y + height); set => height = value - y; }
        public Vector2Int min { get => new Vector2Int(xMin, yMin); set { xMin = value.x; yMin = value.y; } }
        public Vector2Int max { get => new Vector2Int(xMax, yMax); set { xMax = value.x; yMax = value.y; } }
        public Vector2Int position { get => new Vector2Int(x, y); set { x = value.x; y = value.y; } }
        public Vector2Int size { get => new Vector2Int(width, height); set { width = value.x; height = value.y; } }
        public Vector2 center => new Vector2(x + width / 2f, y + height / 2f);
        public bool Contains(Vector2Int p) => p.x >= xMin && p.x < xMax && p.y >= yMin && p.y < yMax;
        public bool Overlaps(RectInt other) => other.xMax > xMin && other.xMin < xMax && other.yMax > yMin && other.yMin < yMax;
        public static bool operator ==(RectInt a, RectInt b) => a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height;
        public static bool operator !=(RectInt a, RectInt b) => !(a == b);
        public bool Equals(RectInt other) => this == other;
        public override bool Equals(object obj) => obj is RectInt other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(x, y, width, height);
        public override string ToString() => $"({x}, {y}, {width}, {height})";
    }

    public static class MathEx
    {
        public const float PI = MathF.PI;
        public static int Min(int a, int b) => Math.Min(a, b);
        public static float Min(float a, float b) => MathF.Min(a, b);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static float Max(float a, float b) => MathF.Max(a, b);
        public static int Abs(int value) => Math.Abs(value);
        public static float Abs(float value) => MathF.Abs(value);
        public static float Round(float value) => MathF.Round(value, MidpointRounding.ToEven);
        public static float Floor(float value) => MathF.Floor(value);
        public static float Ceil(float value) => MathF.Ceiling(value);
        public static int RoundToInt(float value) => (int)Round(value);
        public static int FloorToInt(float value) => (int)Floor(value);
        public static int CeilToInt(float value) => (int)Ceil(value);
        public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
        public static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;
        public static float Clamp01(float value) => Clamp(value, 0, 1);
        public static float Repeat(float value, float length) => Clamp(value - Floor(value / length) * length, 0, length);
        public static float DeltaAngle(float current, float target) { float delta = Repeat(target - current, 360); return delta > 180 ? delta - 360 : delta; }
        public static bool Approximately(float a, float b) => Abs(b - a) < Max(0.000001f * Max(Abs(a), Abs(b)), float.Epsilon * 8);
    }
}
