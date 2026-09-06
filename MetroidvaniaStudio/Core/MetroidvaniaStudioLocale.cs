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
            return Column == "KR" && !string.IsNullOrWhiteSpace(row[1]) ? row[1] : row[2];
        }
        public static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Text(key), args);
        private static Dictionary<string, string[]> Read()
        {
            using Stream stream = typeof(MetroidvaniaStudioLocale).Assembly.GetManifestResourceStream("MetroidvaniaStudioLocale.csv")
                ?? throw new InvalidOperationException("The shared localization CSV is missing.");
            using var parser = new TextFieldParser(stream, Encoding.UTF8, true) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
            parser.SetDelimiters(",");
            string[] header = parser.ReadFields();
            if (header == null || header.Length != 3 || header[0] != "Key" || header[1] != "KR" || header[2] != "EN")
                throw new FormatException("CSV header must be Key,KR,EN.");
            var entries = new Dictionary<string, string[]>(StringComparer.Ordinal);
            while (!parser.EndOfData)
            {
                string[] row = parser.ReadFields();
                if (row == null || row.Length == 1 && string.IsNullOrWhiteSpace(row[0])) continue;
                if (row.Length != 3 || string.IsNullOrWhiteSpace(row[0]) || string.IsNullOrWhiteSpace(row[2]))
                    throw new FormatException("A localization entry requires a key and English fallback.");
                entries.Add(row[0].Trim(), row);
            }
            return entries;
        }
    }
}
