using System.Security.Cryptography;
using System.Text.Json;

namespace MetroidvaniaStudio.Storage;

/// <summary>Copies legacy default storage once, keeping the original files as a backup.</summary>
public static class PortableWorkspace
{
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static string LegacyUserWorkspace => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MetroidvaniaStudio", "workspace")
        : OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MetroidvaniaStudio", "workspace")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "metroidvania-studio", "workspace");

    public static bool IsPortable(string root) => File.Exists(Safe(root, ".studio/portable-storage.json"));
    public static string Prepare(string root, string legacy)
    {
        root = Path.GetFullPath(root); legacy = Path.GetFullPath(legacy);
        Directory.CreateDirectory(root);
        string metadata = Safe(root, ".studio"); Directory.CreateDirectory(metadata);
        using var migrationLock = new FileStream(Safe(root, ".studio/storage-migration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (IsPortable(root)) return root;
        using var destinationLock = new FileStream(Safe(root, ".studio/server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var copies = new List<(string Source, string Destination)>();
        using var sourceLock = Directory.Exists(legacy) && !string.Equals(root, legacy, Comparison)
            ? LockLegacy(legacy) : null;
        if (sourceLock != null)
        {
            string maps = "Maps", textures = "Textures", catalog = ".studio/catalog.json";
            string configuration = Safe(legacy, ".studio/workspace.json");
            if (File.Exists(configuration))
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(configuration));
                foreach (var property in settings.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Null) continue;
                    string value = property.Value.GetString() ?? throw new InvalidDataException("Invalid workspace path.");
                    if (property.Name.Equals("mapsRoot", StringComparison.OrdinalIgnoreCase)) maps = value;
                    if (property.Name.Equals("texturesRoot", StringComparison.OrdinalIgnoreCase)) textures = value;
                    if (property.Name.Equals("catalogPath", StringComparison.OrdinalIgnoreCase)) catalog = value;
                }
            }
            Collect(legacy, maps, root, "Maps", copies);
            Collect(legacy, textures, root, "Textures", copies);
            Collect(legacy, "Scripts", root, "Scripts", copies);
            string sourceCatalog = Safe(legacy, catalog);
            if (File.Exists(sourceCatalog)) copies.Add((sourceCatalog, Safe(root, "catalog.json")));
        }
        string settingsPath = Safe(root, ".studio/workspace.json");
        const string settingsJson = "{\"mapsRoot\":\"Maps\",\"texturesRoot\":\"Textures\",\"catalogPath\":\"catalog.json\"}";
        if (File.Exists(settingsPath) && File.ReadAllText(settingsPath) != settingsJson)
            throw new IOException("Existing workspace settings were preserved. Use --project to open this folder without changing its storage layout.");
        // Validate every collision before copying. Interrupted copies can resume only when bytes match.
        foreach (var pair in copies)
            if (File.Exists(pair.Destination) && !Equal(pair.Source, pair.Destination))
                throw new IOException("Storage migration found different files with the same name. Both were preserved: " + Path.GetRelativePath(root, pair.Destination));
        foreach (var pair in copies)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pair.Destination)!);
            if (!File.Exists(pair.Destination)) CopyAtomic(pair.Source, pair.Destination);
        }
        Directory.CreateDirectory(Safe(root, "Maps")); Directory.CreateDirectory(Safe(root, "Textures"));
        if (!File.Exists(settingsPath)) WriteNew(settingsPath, settingsJson);
        WriteNew(Safe(root, ".studio/portable-storage.json"), "{\"version\":1}");
        return root;
    }

    // Called under the server workspace lock. Default resources must travel with exported JSON too.
    public static void SeedResources(string root, string studioRoot)
    {
        if (!IsPortable(root)) return;
        var copies = new List<(string Source, string Destination)>();
        Collect(studioRoot, "samples/textures", root, "Textures", copies);
        foreach (var pair in copies)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pair.Destination)!);
            if (!File.Exists(pair.Destination)) CopyAtomic(pair.Source, pair.Destination);
        }
        string catalog = Safe(root, "catalog.json"), sample = Safe(studioRoot, "samples/catalog.json");
        if (!File.Exists(catalog) && File.Exists(sample)) CopyAtomic(sample, catalog);
        if (!File.Exists(Safe(root, ".studio/portable-resources.json"))) WriteNew(Safe(root, ".studio/portable-resources.json"), "{\"version\":1}");
    }

    private static FileStream LockLegacy(string legacy)
    {
        Directory.CreateDirectory(Safe(legacy, ".studio"));
        try { return new FileStream(Safe(legacy, ".studio/server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException("Close the studio using the previous storage folder before migrating. Its files have not been moved or deleted.", e); }
    }
    private static void Collect(string sourceRoot, string sourceRelative, string targetRoot, string targetRelative, List<(string, string)> files)
    {
        string source = Safe(sourceRoot, sourceRelative);
        if (!Directory.Exists(source)) return;
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            string name = Path.GetFileName(entry), relative = Path.Combine(sourceRelative, name), target = Path.Combine(targetRelative, name);
            _ = Safe(sourceRoot, relative); _ = Safe(targetRoot, target);
            if (Directory.Exists(entry)) Collect(sourceRoot, relative, targetRoot, target, files);
            else files.Add((entry, Safe(targetRoot, target)));
        }
    }
    private static string Safe(string root, string relative)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, Comparison)) throw new IOException("Storage paths must stay within their workspace.");
        for (string? part = path; part != null; part = Path.GetDirectoryName(part))
        {
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked storage paths are not supported.");
            if (string.Equals(part, root, Comparison)) break;
        }
        return path;
    }
    private static bool Equal(string a, string b)
    {
        using var left = File.OpenRead(a); using var right = File.OpenRead(b);
        return left.Length == right.Length && SHA256.HashData(left).SequenceEqual(SHA256.HashData(right));
    }
    private static void CopyAtomic(string source, string destination)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(source, temporary); if (!Equal(source, temporary)) throw new IOException("Storage changed during migration."); File.Move(temporary, destination); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void WriteNew(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(System.Text.Encoding.UTF8.GetBytes(text)); stream.Flush(true);
    }
}
