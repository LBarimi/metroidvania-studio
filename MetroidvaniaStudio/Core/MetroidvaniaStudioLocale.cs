using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace MetroidvaniaStudio
{
    /// <summary>CSV-backed text adapter used by the room exporter. No editor UI dependency.</summary>
    public static class MetroidvaniaStudioLocale
    {
        private static readonly Lazy<Dictionary<string, string[]>> Entries = new Lazy<Dictionary<string, string[]>>(Read);
        public static string Column { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" ? "KR" : "EN";
        public static string Text(string key)
        {
            if (key == null || !Entries.Value.TryGetValue(key, out string[] row)) return "[" + key + "]";
            int index = Array.IndexOf(new[] { "Key", "KR", "EN", "JA", "ZH_CN", "ZH_TW", "RU" }, Column);
            return index > 0 && index < row.Length && !string.IsNullOrWhiteSpace(row[index]) ? row[index] : row[2];
        }
        public static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Text(key), args);
        private static Dictionary<string, string[]> Read()
        {
            using Stream stream = typeof(MetroidvaniaStudioLocale).Assembly.GetManifestResourceStream("MetroidvaniaStudioLocale.csv")
                ?? throw new InvalidOperationException("The shared localization CSV is missing.");
            using var parser = new TextFieldParser(stream, Encoding.UTF8, true) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
            parser.SetDelimiters(",");
            string[] header = parser.ReadFields();
            if (header == null || header.Length != 7 || header[0] != "Key" || header[1] != "KR" || header[2] != "EN")
                throw new FormatException("CSV header must be Key,KR,EN,JA,ZH_CN,ZH_TW,RU.");
            var entries = new Dictionary<string, string[]>(StringComparer.Ordinal);
            while (!parser.EndOfData)
            {
                string[] row = parser.ReadFields();
                if (row == null || row.Length == 1 && string.IsNullOrWhiteSpace(row[0])) continue;
                if (row.Length != header.Length || string.IsNullOrWhiteSpace(row[0]) || string.IsNullOrWhiteSpace(row[2]))
                    throw new FormatException("A localization entry requires a key and English fallback.");
                entries.Add(row[0].Trim(), row);
            }
            return entries;
        }
    }
}
