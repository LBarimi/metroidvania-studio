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
    internal const string ErrorCodeKey = "ScriptLibrary.ErrorCode";
    private static T WithCode<T>(T error, string code) where T : Exception
    {
        error.Data[ErrorCodeKey] = code;
        return error;
    }
    private static string Name(string name)
    {
        if (name.Length is < 5 or > 100 || !name.EndsWith(".lua", StringComparison.Ordinal)
            || !char.IsLetterOrDigit(name[0]) || name.Contains("..")
            || name.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ')))
            throw WithCode(new ArgumentException("Use a Lua filename containing letters, numbers, spaces, hyphens or underscores."), "scripts.filenameInvalid");
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            throw WithCode(new ArgumentException("This script filename is reserved by the operating system."), "scripts.filenameReserved");
        return name;
    }
    private static void NoLink(string path)
    {
        // Attributes also inspect a dangling file link instead of following it.
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw WithCode(new ArgumentException("Linked scripts are not supported."), "scripts.linkUnsupported"); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    private void CheckRoot() { NoLink(workspace); NoLink(scripts); }
    private string Resolve(string id, out bool readOnly)
    {
        string[] parts = id.Split('/');
        if (parts.Length != 2) throw WithCode(new ArgumentException("Choose a script from the library."), "scripts.chooseScript");
        string name = Name(parts[1]); readOnly = parts[0] == "examples";
        if (parts[0] != "workspace" && (!readOnly || !Examples.Contains(name))) throw WithCode(new ArgumentException("Unknown script library."), "scripts.chooseScript");
        if (readOnly) { NoLink(Path.GetDirectoryName(examples)!); NoLink(examples); } else CheckRoot();
        string file = Path.Combine(readOnly ? examples : scripts, name); NoLink(file); return file;
    }
    private static Script ReadFile(string file, string id, bool readOnly)
    {
        if (!File.Exists(file)) throw WithCode(new KeyNotFoundException("Script not found."), "scripts.notFound");
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw WithCode(new ArgumentException("Lua scripts cannot exceed 64 KiB."), "scripts.sourceLimit");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        string source;
        try { source = Utf8.GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException error) { error.Data[ErrorCodeKey] = "scripts.invalidUtf8"; throw; }
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
                    if (result.Count >= MaximumFiles) throw WithCode(new ArgumentException("The script library supports up to 256 scripts."), "scripts.libraryFull");
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
        if (bytes.Length is < 1 or > MaximumBytes) throw WithCode(new ArgumentException("Script source must contain 1–65536 UTF-8 bytes."), "scripts.sourceLimit");
        lock (gate)
        {
            string id = "workspace/" + name, file = Resolve(id, out _);
            string? revision = File.Exists(file) ? ReadFile(file, id, false).Revision : null;
            if (revision != expectedRevision) throw WithCode(new WorkspaceConflict("The script changed or already exists. Open it again before saving."), "scripts.saveConflict");
            if (revision == null && Directory.Exists(scripts) && Directory.EnumerateFiles(scripts, "*.lua").Take(MaximumFiles).Count() >= MaximumFiles)
                throw WithCode(new ArgumentException("The script library supports up to 256 scripts."), "scripts.libraryFull");
            Directory.CreateDirectory(scripts); CheckRoot();
            string temporary = Path.Combine(scripts, ".script-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
                CheckRoot(); NoLink(file);
                if ((File.Exists(file) ? ReadFile(file, id, false).Revision : null) != revision)
                    throw WithCode(new WorkspaceConflict("The script changed while saving. Open it again before saving."), "scripts.saveConflict");
                File.Move(temporary, file, revision != null);
                return ReadFile(file, id, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
