using System.Security.Cryptography;
using System.Text;

namespace MetroidvaniaStudio.Cli;

internal sealed record FileSnapshot(string RelativePath, string Text, string Revision);

internal sealed class WorkspaceFiles : IDisposable
{
    private readonly string root;
    private readonly bool readOnly;
    private readonly SemaphoreSlim gate = new(1, 1);
    internal const int MaximumFileBytes = 64 * 1024 * 1024;
    public WorkspaceFiles(string directory, bool readOnly)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        this.readOnly = readOnly;
        CheckAncestors(root);
        if (!Directory.Exists(root)) throw new CliException("invalid_input", "The workspace directory must already exist.");
    }
    public void Dispose() => gate.Dispose();
    public string Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Any(char.IsControl))
            throw new CliException("invalid_input", "Paths must be relative to the selected workspace.");
        string[] parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(part => part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
            throw new CliException("invalid_input", "Paths cannot contain traversal or ambiguous segments.");
        if (parts[0].Equals(".studio", StringComparison.OrdinalIgnoreCase)) throw new CliException("invalid_input", "Session files are reserved.");
        string full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new CliException("invalid_input", "The path is outside the workspace.");
        CheckAncestors(full);
        return full;
    }
    private static void CheckAncestors(string path)
    {
        for (string? item = path; item != null; item = Path.GetDirectoryName(item))
        {
            try
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new CliException("invalid_input", "Symbolic links and junctions are not allowed in workspace paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public FileSnapshot Read(string relative, int maximumBytes = MaximumFileBytes)
    {
        string full = Resolve(relative);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximumBytes) throw new CliException("limit_exceeded", "The file exceeds the supported size.");
        using var memory = new MemoryStream();
        byte[] chunk = new byte[32768];
        int count;
        while ((count = stream.Read(chunk)) > 0)
        {
            if (memory.Length + count > maximumBytes) throw new CliException("limit_exceeded", "The file exceeds the supported size.");
            memory.Write(chunk, 0, count);
        }
        if (memory.Length > maximumBytes) throw new CliException("limit_exceeded", "The file exceeds the supported size.");
        byte[] bytes = memory.ToArray();
        return new(relative, new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'), Hash(bytes));
    }
    public bool Exists(string relative) => File.Exists(Resolve(relative));
    public async Task<IDisposable> BeginEditAsync(bool dryRun, CancellationToken cancellation)
    {
        if (readOnly && !dryRun) throw new CliException("read_only", "This session is read-only. Use dryRun for a preview.");
        await gate.WaitAsync(cancellation);
        if (dryRun) return new Lease(gate, null);
        try
        {
            string session = Path.Combine(root, ".studio"), path = Path.Combine(session, "server.lock");
            CheckAncestors(path);
            Directory.CreateDirectory(session);
            FileStream handle;
            try { handle = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new CliException("workspace_busy", "This workspace is open in another editor or automation process. Close it before editing files directly, or use the live API."); }
            return new Lease(gate, handle);
        }
        catch { gate.Release(); throw; }
    }
    public IDisposable BeginEdit(bool dryRun) => BeginEditAsync(dryRun, CancellationToken.None).GetAwaiter().GetResult();
    public string Write(string relative, string text, string? expectedRevision, bool createOnly, CancellationToken cancellation)
    {
        if (readOnly) throw new CliException("read_only", "This session is read-only.");
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
        if (bytes.Length > MaximumFileBytes) throw new CliException("limit_exceeded", "The output exceeds the supported file size.");
        string full = Resolve(relative);
        CheckDestination();
        string directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        CheckAncestors(full);
        string temporary = Path.Combine(directory, ".studio-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            cancellation.ThrowIfCancellationRequested();
            CheckAncestors(full);
            CheckDestination();
            File.Move(temporary, full, !createOnly);
            return Hash(bytes);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        void CheckDestination()
        {
            bool exists = File.Exists(full);
            if (createOnly && exists) throw new CliException("conflict", "The output file already exists. Choose another output path.");
            if (expectedRevision != null && (!exists || Read(relative).Revision != expectedRevision))
                throw new CliException("conflict", "The file changed after it was read. Inspect it again before retrying.");
        }
    }
    public static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private sealed class Lease(SemaphoreSlim gate, FileStream? handle) : IDisposable
    {
        public void Dispose() { handle?.Dispose(); gate.Release(); }
    }
}
