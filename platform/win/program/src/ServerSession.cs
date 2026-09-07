using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace MetroidvaniaStudio.Editor;

internal sealed class ServerSession : IDisposable
{
    private sealed record Health(string InstanceId, string WorkspacePath, string? LaunchToken, int ProcessId);
    private readonly DesktopOptions options;
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };
    private Process? process;
    private Health? health;
    public Uri Url { get; private set; } = null!;
    public bool Owned => process != null;
    public int? ProcessId => process?.Id;
    public bool Stopped => process == null || process.HasExited;

    public ServerSession(DesktopOptions options) => this.options = options;
    private async Task<Health?> ReadHealth(Uri url)
    {
        try { return await http.GetFromJsonAsync<Health>(new Uri(url, "/api/health")); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }
    public async Task Start()
    {
        options.ValidatePayload();
        if (!options.SelfTest)
        {
            Uri existing = new("http://127.0.0.1:18765/");
            Health? active = await ReadHealth(existing);
            if (active != null && SamePath(active.WorkspacePath, options.Workspace))
            {
                Url = existing; health = active; return;
            }
        }
        if (options.InspectExisting) throw new IOException("No existing server matches the requested workspace.");
        Directory.CreateDirectory(options.Workspace);
        string logDirectory = Path.Combine(options.Workspace, ".studio", "desktop-logs");
        Directory.CreateDirectory(logDirectory);
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        Url = new Uri($"http://127.0.0.1:{port}/");
        string token = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = options.Payload,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { "--server", "--studio-root", options.Payload, "--project", options.Workspace,
            "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--launch-token", token }) start.ArgumentList.Add(arg);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        process = Process.Start(start) ?? throw new IOException("Could not start the local server.");
        string logFile = Path.Combine(logDirectory, "session-" + process.Id + ".log");
        object logGate = new();
        void Log(string? line) { if (line == null) return; lock (logGate) { try { File.AppendAllText(logFile, line + Environment.NewLine); } catch (IOException) { } } }
        process.OutputDataReceived += (_, e) => Log(e.Data);
        process.ErrorDataReceived += (_, e) => Log(e.Data);
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited) throw new IOException("The workspace may already be open in another instance. Local server startup failed. Log: " + logFile);
                Health? ready = await ReadHealth(Url);
                if (ready != null)
                {
                    if (ready.ProcessId != process.Id || ready.LaunchToken != token || !SamePath(ready.WorkspacePath, options.Workspace))
                        throw new IOException("Another process acquired the local server port.");
                    health = ready; return;
                }
                await Task.Delay(100);
            }
            throw new TimeoutException("Local server startup timed out.");
        }
        catch
        {
            // No browser has loaded this process yet, so it cannot contain user edits.
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            throw;
        }
    }
    public async Task Stop()
    {
        if (process == null || process.HasExited || health == null) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Url, "/api/shutdown")) { Content = JsonContent.Create(new { }) };
        request.Headers.Add("X-Metroidvania-Studio-Instance", health.InstanceId);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new IOException("Saving before shutdown failed: " + await response.Content.ReadAsStringAsync());
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }
    public static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    public void Dispose() { process?.Dispose(); http.Dispose(); }
}
