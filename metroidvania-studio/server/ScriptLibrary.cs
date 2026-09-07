using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetroidvaniaStudio.Server;

/// <summary>Explicitly opened scripts; listing and saving never execute code.</summary>
public sealed class ScriptLibrary
{
    public const int MaximumBytes = 65536;
    public const int MaximumFiles = 256;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Examples = ["connected-rooms.lua", "seeded-platforms.lua", "region-properties.lua"];
    private readonly string workspace, scripts, examples;
    private readonly object gate = new();
    public ScriptLibrary(string workspace, string studioRoot)
    {
        this.workspace = Path.GetFullPath(workspace);
        scripts = Path.Combine(this.workspace, "Scripts");
        examples = Path.Combine(Path.GetFullPath(studioRoot), "docs", "examples");
    }
    public record Entry(string Id, string Name, bool ReadOnly);
    public record Script(string Id, string Name, string Source, string Revision, bool ReadOnly);
    private static string Name(string name)
    {
        if (name.Length is < 5 or > 100 || !name.EndsWith(".lua", StringComparison.Ordinal)
            || !char.IsLetterOrDigit(name[0]) || name.Contains("..")
            || name.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ')))
            throw new ArgumentException("Use a Lua filename containing letters, numbers, spaces, hyphens or underscores.");
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            throw new ArgumentException("This script filename is reserved by the operating system.");
        return name;
    }
    private static void NoLink(string path)
    {
        // Attributes also inspect a dangling file link instead of following it.
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Linked scripts are not supported."); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    private void CheckRoot() { NoLink(workspace); NoLink(scripts); }
    private string Resolve(string id, out bool readOnly)
    {
        string[] parts = id.Split('/');
        if (parts.Length != 2) throw new ArgumentException("Choose a script from the library.");
        string name = Name(parts[1]); readOnly = parts[0] == "examples";
        if (parts[0] != "workspace" && (!readOnly || !Examples.Contains(name))) throw new ArgumentException("Unknown script library.");
        if (readOnly) { NoLink(Path.GetDirectoryName(examples)!); NoLink(examples); } else CheckRoot();
        string file = Path.Combine(readOnly ? examples : scripts, name); NoLink(file); return file;
    }
    private static Script ReadFile(string file, string id, bool readOnly)
    {
        if (!File.Exists(file)) throw new KeyNotFoundException("Script not found.");
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new ArgumentException("Lua scripts cannot exceed 64 KiB.");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        string source = Utf8.GetString(bytes).TrimStart('\uFEFF');
        return new(id, Path.GetFileName(file), source, Convert.ToHexString(SHA256.HashData(bytes)), readOnly);
    }
    public Entry[] List()
    {
        lock (gate)
        {
            CheckRoot(); var result = new List<Entry>();
            if (Directory.Exists(scripts))
            {
                foreach (string file in Directory.EnumerateFiles(scripts, "*.lua").Take(MaximumFiles + 1))
                {
                    if (result.Count >= MaximumFiles) throw new ArgumentException("The script library supports up to 256 scripts.");
                    string name = Name(Path.GetFileName(file)); NoLink(file); result.Add(new("workspace/" + name, name, false));
                }
            }
            foreach (string name in Examples) if (File.Exists(Path.Combine(examples, name))) result.Add(new("examples/" + name, name, true));
            return result.OrderBy(e => e.ReadOnly).ThenBy(e => e.Name, StringComparer.Ordinal).ToArray();
        }
    }
    public Script Read(string id)
    {
        lock (gate) { string file = Resolve(id, out bool readOnly); return ReadFile(file, id, readOnly); }
    }
    public Script Save(string name, string source, string? expectedRevision)
    {
        name = Name(name);
        byte[] bytes = Utf8.GetBytes(source);
        if (bytes.Length is < 1 or > MaximumBytes) throw new ArgumentException("Script source must contain 1–65536 UTF-8 bytes.");
        lock (gate)
        {
            string id = "workspace/" + name, file = Resolve(id, out _);
            string? revision = File.Exists(file) ? ReadFile(file, id, false).Revision : null;
            if (revision != expectedRevision) throw new WorkspaceConflict("The script changed or already exists. Open it again before saving.");
            if (revision == null && Directory.Exists(scripts) && Directory.EnumerateFiles(scripts, "*.lua").Take(MaximumFiles).Count() >= MaximumFiles)
                throw new ArgumentException("The script library supports up to 256 scripts.");
            Directory.CreateDirectory(scripts); CheckRoot();
            string temporary = Path.Combine(scripts, ".script-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
                CheckRoot(); NoLink(file);
                if ((File.Exists(file) ? ReadFile(file, id, false).Revision : null) != revision)
                    throw new WorkspaceConflict("The script changed while saving. Open it again before saving.");
                File.Move(temporary, file, revision != null);
                return ReadFile(file, id, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}

public static class ScriptLibraryEndpoints
{
    public static void MapScriptLibrary(this WebApplication app, ScriptLibrary library)
    {
        app.MapGet("/api/v1/scripts", () => Results.Json(library.List()));
        app.MapGet("/api/v1/scripts/source", (string id) => Results.Json(library.Read(id)));
        app.MapPost("/api/v1/scripts", async (HttpRequest request) =>
        {
            using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            var value = body.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("A script object is required.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
                if (field.Name is not ("name" or "source" or "expectedRevision") || !seen.Add(field.Name)) throw new ArgumentException("Unknown or duplicate script field.");
            if (seen.Count != 3 || value.GetProperty("name").ValueKind != JsonValueKind.String || value.GetProperty("source").ValueKind != JsonValueKind.String
                || value.GetProperty("expectedRevision").ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new ArgumentException("Provide name, source, and expectedRevision.");
            return Results.Json(library.Save(value.GetProperty("name").GetString()!, value.GetProperty("source").GetString()!, value.GetProperty("expectedRevision").GetString()));
        });
    }
}
