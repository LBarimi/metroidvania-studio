using System;
using System.Collections.Generic;
using System.Globalization;

namespace MetroidvaniaStudio.Primitives
{
    [Serializable]
    public struct Color : IEquatable<Color>
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => new Color(1, 1, 1);
        public static Color black => new Color(0, 0, 0);
        public static Color clear => new Color(0, 0, 0, 0);
        public static Color red => new Color(1, 0, 0);
        public static Color green => new Color(0, 1, 0);
        public static Color blue => new Color(0, 0, 1);
        public bool Equals(Color other) => r.Equals(other.r) && g.Equals(other.g) && b.Equals(other.b) && a.Equals(other.a);
        public override bool Equals(object obj) => obj is Color other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(r, g, b, a);
        public static bool operator ==(Color x, Color y) => x.Equals(y);
        public static bool operator !=(Color x, Color y) => !x.Equals(y);
    }
    [Serializable]
    public struct Color32 : IEquatable<Color32>
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static implicit operator Color(Color32 value) => new Color(value.r / 255f, value.g / 255f, value.b / 255f, value.a / 255f);
        public static implicit operator Color32(Color value) => new Color32(Channel(value.r), Channel(value.g), Channel(value.b), Channel(value.a));
        private static byte Channel(float value) => (byte)MathEx.RoundToInt(MathEx.Clamp01(value) * 255);
        public bool Equals(Color32 other) => r == other.r && g == other.g && b == other.b && a == other.a;
        public override bool Equals(object obj) => obj is Color32 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(r, g, b, a);
    }
    public static class ColorText
    {
        private static readonly Dictionary<string, string> Named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["red"]="FF0000", ["cyan"]="00FFFF", ["blue"]="0000FF", ["darkblue"]="00008B", ["lightblue"]="ADD8E6",
            ["purple"]="800080", ["yellow"]="FFFF00", ["lime"]="00FF00", ["fuchsia"]="FF00FF", ["white"]="FFFFFF",
            ["silver"]="C0C0C0", ["grey"]="808080", ["black"]="000000", ["orange"]="FFA500", ["brown"]="A52A2A",
            ["maroon"]="800000", ["green"]="008000", ["olive"]="808000", ["navy"]="000080", ["teal"]="008080", ["aqua"]="00FFFF",
            ["magenta"]="FF00FF", ["clear"]="00000000", ["transparent"]="00000000"
        };
        public static bool TryParseHtmlString(string text, out Color color)
        {
            color = default;
            if (string.IsNullOrEmpty(text)) return false;
            if (text[0] == '#') text = text.Substring(1);
            else if (!Named.TryGetValue(text, out text)) return false;
            if (text.Length == 3 || text.Length == 4)
            {
                string expanded = ""; foreach (char c in text) expanded += new string(c, 2); text = expanded;
            }
            if (text.Length == 6) text += "FF";
            if (text.Length != 8 || !uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgba)) return false;
            color = new Color32((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba); return true;
        }
        public static string ToHtmlStringRGB(Color color) { Color32 value = color; return $"{value.r:X2}{value.g:X2}{value.b:X2}"; }
        public static string ToHtmlStringRGBA(Color color) { Color32 value = color; return $"{value.r:X2}{value.g:X2}{value.b:X2}{value.a:X2}"; }
    }
}
