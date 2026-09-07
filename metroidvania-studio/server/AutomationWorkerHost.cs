using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MetroidvaniaStudio.Server;

/// <summary>Only this process's worker is terminated on a script resource limit.</summary>
public sealed class AutomationWorkerHost(string studioRoot)
{
    public const int TimeoutSeconds = 15;
    public const long MaximumProcessBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public async Task<AutomationExecution> RunAsync(AutomationInput input, CancellationToken cancellationToken)
    {
        string relative = "metroidvania-studio/cli/MetroidvaniaStudio.Cli.dll";
        string worker = Path.Combine(studioRoot, relative);
        if (!File.Exists(worker)) worker = Path.Combine(studioRoot, "metroidvania-studio/cli/bin/Release/net10.0/MetroidvaniaStudio.Cli.dll");
        if (!File.Exists(worker)) throw new InvalidOperationException("The automation worker is missing. Run the Build script to update the studio.");
        string? currentHost = Environment.ProcessPath;
        string dotnet = currentHost != null && Path.GetFileNameWithoutExtension(currentHost).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? currentHost : ResolveDotnet();
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true) };
        start.ArgumentList.Add(worker); start.ArgumentList.Add("worker");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        using var parent = Process.GetCurrentProcess();
        start.Environment["METROIDVANIA_STUDIO_WORKER_PARENT_PID"] = parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["METROIDVANIA_STUDIO_WORKER_PARENT_TICKS"] = parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["DOTNET_GCHeapHardLimit"] = "0x10000000";
        using var process = Process.Start(start) ?? throw new IOException("Could not start the automation worker.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        using var killed = timeout.Token.Register(() => Stop(process));
        Task<string> output = ReadBounded(process.StandardOutput, 70 * 1024 * 1024, process, timeout.Token);
        Task<string> errors = ReadBounded(process.StandardError, 65536, process, timeout.Token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input, Json).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            while (!process.HasExited)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    process.Refresh();
                    if (process.PrivateMemorySize64 > MaximumProcessBytes)
                        throw new InvalidDataException("Automation exceeded the 512 MiB process memory limit.");
                }
                catch (InvalidOperationException) when (process.HasExited) { break; }
                await Task.Delay(50, timeout.Token);
            }
            string text = await output;
            await errors;
            timeout.Token.ThrowIfCancellationRequested();
            using var response = JsonDocument.Parse(text);
            if (response.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException(error.TryGetProperty("message", out var message) ? message.GetString() : "Automation failed.");
            if (process.ExitCode != 0) throw new InvalidOperationException("The automation worker stopped without applying its result.");
            var result = JsonSerializer.Deserialize<AutomationExecution>(text, Json) ?? throw new InvalidDataException("Invalid automation result.");
            if (result.Logs == null || result.Logs.Length > 256 || result.Logs.Any(line => line == null || line.Length > 4096)
                || result.Logs.Sum(line => (long)line.Length) > 65536 || result.OperationCount < 0)
                throw new InvalidDataException("Automation output exceeded its limits.");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("Automation exceeded the 15 second execution limit."); }
        finally
        {
            Stop(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            try { await Task.WhenAll(output, errors); } catch { }
        }
    }

    private string ResolveDotnet()
    {
        string name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string bundled = Path.Combine(studioRoot, "runtime/win-x64", name);
        if (OperatingSystem.IsWindows() && File.Exists(bundled)) return bundled;
        string? configured = Environment.GetEnvironmentVariable("METROIDVANIA_STUDIO_DOTNET");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", name);
        return OperatingSystem.IsWindows() && File.Exists(installed) ? installed : name;
    }

    private static void Stop(Process process)
    { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }

    private static async Task<string> ReadBounded(StreamReader reader, int maximum, Process process, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[8192];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
            {
                if ((long)text.Length + count > maximum) throw new InvalidDataException("Automation output exceeded its limit.");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }
        catch { Stop(process); throw; }
    }
}
