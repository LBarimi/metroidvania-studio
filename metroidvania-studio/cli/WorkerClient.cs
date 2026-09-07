using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MetroidvaniaStudio.Cli;

internal sealed record WorkerResult(string DocumentJson, string[] Logs, int OperationCount, bool Changed, string[]? CreatedIds);

internal static class WorkerClient
{
    internal static async Task<WorkerResult> RunAsync(object request, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath ?? "dotnet";
        bool isDotnet = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false) };
        if (isDotnet) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("worker");
        start.Environment["DOTNET_GCHeapHardLimit"] = "0x10000000";
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        start.Environment["METROIDVANIA_STUDIO_WORKER_PARENT_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["METROIDVANIA_STUDIO_WORKER_PARENT_TICKS"] = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var process = Process.Start(start) ?? throw new CliException("io_error", "The script worker could not start.");
        try
        {
            Task<string> output = BoundedText.ReadAsync(process.StandardOutput.BaseStream, Program.MaximumInputBytes, deadline.Token);
            Task<string> errors = BoundedText.ReadAsync(process.StandardError.BaseStream, 65536, deadline.Token);
            string input = JsonSerializer.Serialize(request, Program.Json);
            if (Encoding.UTF8.GetByteCount(input) > Program.MaximumInputBytes) throw new CliException("limit_exceeded", "Worker input exceeds the supported size.");
            await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            while (!process.HasExited)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (output.IsFaulted) await output;
                if (errors.IsFaulted) await errors;
                try
                {
                    process.Refresh();
                    if (process.PrivateMemorySize64 > 512L * 1024 * 1024) throw new CliException("limit_exceeded", "The operation exceeded the worker memory limit.");
                }
                catch (InvalidOperationException) when (process.HasExited) { break; }
                await Task.Delay(50, deadline.Token);
            }
            string json = await output;
            await errors;
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.TryGetProperty("error", out var failure))
                throw new CliException(failure.GetProperty("code").GetString() ?? "invalid_input", failure.GetProperty("message").GetString() ?? "The operation failed.");
            if (process.ExitCode != 0) throw new CliException("limit_exceeded", "The operation stopped before completion; no edit was saved.");
            return JsonSerializer.Deserialize<WorkerResult>(json, new JsonSerializerOptions(Program.Json) { PropertyNameCaseInsensitive = true }) ?? throw new CliException("invalid_input", "Invalid worker response.");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new CliException("limit_exceeded", "The operation exceeded the 15 second time limit."); }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync();
            }
        }
    }
}
