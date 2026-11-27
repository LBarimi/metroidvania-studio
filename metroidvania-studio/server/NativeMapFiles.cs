using System.Diagnostics;
using System.Text;
using MetroidvaniaStudio;

namespace MetroidvaniaStudio.Server;

/// <summary>A local OS picker grants access to one selected JSON, never a client-supplied path.</summary>
public sealed class NativeMapFiles(ProjectFiles files, Func<string, string, string, CancellationToken, Task<string?>>? picker = null) : IDisposable
{
    public sealed record Selection(string token, string name);
    private sealed record Grant(string Path, DateTime Expires);
    private readonly Dictionary<string, Grant> grants = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim dialogGate = new(1, 1);
    public bool Available => picker != null || OperatingSystem.IsWindows();

    public async Task<Selection?> Pick(string mode, string name, CancellationToken cancellation)
    {
        if (!Available || mode is not ("open" or "save")) throw new ArgumentException(MetroidvaniaStudioLocale.Text("fileDialogUnavailable"));
        name = Path.GetFileName(name);
        if (name.Length > 200 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) name = "world.map.json";
        if (!await dialogGate.WaitAsync(0, cancellation)) throw new ArgumentException(MetroidvaniaStudioLocale.Text("fileDialogBusy"));
        try
        {
            Directory.CreateDirectory(files.MapsPath);
            string? path = await (picker ?? WindowsPicker)(mode, files.MapsPath, name, cancellation);
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = ValidatePath(path);
            if (mode == "open" && !File.Exists(path)) throw new IOException(MetroidvaniaStudioLocale.Text("fileMapMissing"));
            lock (grants)
            {
                foreach (var entry in grants.Where(p => p.Value.Expires < DateTime.UtcNow).ToArray()) grants.Remove(entry.Key);
                if (grants.Count >= 128) throw new IOException(MetroidvaniaStudioLocale.Text("fileSelectionLimit"));
                string token = Guid.NewGuid().ToString("N"); grants.Add(token, new(path, DateTime.UtcNow.AddDays(1)));
                return new(token, Path.GetFileName(path));
            }
        }
        finally { dialogGate.Release(); }
    }
    public byte[] Read(string token)
    {
        lock (grants)
        {
            string path = Granted(token);
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MapDocumentStore.MaximumFileBytes) throw new InvalidDataException("@importTooLarge");
            byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
        }
    }
    public void Write(string token, string text, string hash)
    {
        lock (grants)
        {
            string path = Granted(token);
            if (Encoding.UTF8.GetByteCount(text) > MapDocumentStore.MaximumFileBytes) throw new InvalidDataException("@importTooLarge");
            _ = MapDocumentStore.Deserialize(text);
            var expected = ProjectFiles.Fingerprint(path);
            string current = expected?.Hash ?? ProjectFiles.FingerprintUtf8("").Hash;
            if (!string.Equals(current, hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("@fileChangedOutside");
            files.PublishMap(path, text, expected);
        }
    }
    private string Granted(string token)
    {
        if (!grants.TryGetValue(token, out var grant) || grant.Expires < DateTime.UtcNow) throw new UnauthorizedAccessException(MetroidvaniaStudioLocale.Text("fileSelectAgain"));
        return ValidatePath(grant.Path);
    }
    private static string ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(MetroidvaniaStudioLocale.Text("fileChooseJson"));
        path = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && path[2..].Contains(':')) throw new ArgumentException(MetroidvaniaStudioLocale.Text("fileChooseJson"));
        for (string? item = path; item != null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(MetroidvaniaStudioLocale.Text("fileChooseUnlinked"));
        return path;
    }
    internal const string DialogScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        Add-Type -AssemblyName System.Windows.Forms
        $save = $env:STUDIO_DIALOG_MODE -eq 'save'
        $dialog = if ($save) { [System.Windows.Forms.SaveFileDialog]::new() } else { [System.Windows.Forms.OpenFileDialog]::new() }
        $owner = [System.Windows.Forms.Form]::new()
        try {
            $owner.ShowInTaskbar = $false
            $owner.Opacity = 0
            $owner.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
            $owner.TopMost = $true
            $owner.Show()
            $owner.Activate()
            $dialog.InitialDirectory = $env:STUDIO_DIALOG_DIRECTORY
            $dialog.RestoreDirectory = $true
            $dialog.Filter = 'JSON (*.json)|*.json'
            $dialog.DefaultExt = 'json'
            $dialog.AddExtension = $true
            $dialog.FileName = $env:STUDIO_DIALOG_NAME
            if ($save) { $dialog.OverwritePrompt = $true } else { $dialog.CheckFileExists = $true; $dialog.Multiselect = $false }
            if ($dialog.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) { [Console]::Write($dialog.FileName) }
        } finally { $dialog.Dispose(); $owner.Dispose() }
        """;
    private static async Task<string?> WindowsPicker(string mode, string directory, string name, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            // Hide the console, not the process's first GUI window (the file dialog).
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-STA", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(DialogScript)) }) start.ArgumentList.Add(arg);
        start.Environment["STUDIO_DIALOG_MODE"] = mode; start.Environment["STUDIO_DIALOG_DIRECTORY"] = directory; start.Environment["STUDIO_DIALOG_NAME"] = name;
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new IOException(MetroidvaniaStudioLocale.Text("fileDialogUnavailable")); }
        catch (System.ComponentModel.Win32Exception error) { throw new IOException(MetroidvaniaStudioLocale.Text("fileDialogUnavailable"), error); }
        var output = process.StandardOutput.ReadToEndAsync(cancellation); var errors = process.StandardError.ReadToEndAsync(cancellation);
        try { await process.WaitForExitAsync(cancellation); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) { _ = await errors; throw new IOException(MetroidvaniaStudioLocale.Text("fileDialogUnavailable")); }
        return await output;
    }
    public void Dispose() => dialogGate.Dispose();
}
