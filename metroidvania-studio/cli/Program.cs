using System.Reflection;
using System.Text;
using System.Text.Json;
using MetroidvaniaStudio.Automation;
using MetroidvaniaStudio.Scripting;

namespace MetroidvaniaStudio.Cli;

public static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 64 };
    internal const int MaximumInputBytes = 70 * 1024 * 1024;
    internal static string Version => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.1.0";

    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false, true);
        Console.OutputEncoding = new UTF8Encoding(false);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        bool protocolMode = Options.IsProtocol(args);
        try
        {
            var options = Options.Parse(args);
            protocolMode = options.Command is "mcp" or "serve";
            if (options.Command == "worker")
            {
                WorkerParentMonitor.Start();
                string input = await BoundedText.ReadAsync(Console.OpenStandardInput(), MaximumInputBytes, cancellation.Token);
                using var json = JsonDocument.Parse(input);
                JsonElement request = json.RootElement;
                string document = request.GetProperty("documentJson").GetString() ?? throw new CliException("invalid_input", "documentJson is required.");
                object result;
                if (request.GetProperty("kind").GetString() == "lua")
                {
                    var run = LuaScriptRunner.Run(document, request.GetProperty("source").GetString()!, request.TryGetProperty("seed", out var seed) ? seed.GetInt32() : 1, cancellation.Token);
                    result = new { run.DocumentJson, run.Logs, run.OperationCount, run.Changed };
                }
                else if (request.GetProperty("kind").GetString() == "batch")
                {
                    var run = AutomationEngine.Apply(document, request.GetProperty("batch"), cancellation.Token);
                    result = new { run.DocumentJson, logs = Array.Empty<string>(), run.OperationCount, run.Changed, run.CreatedIds };
                }
                else throw new CliException("invalid_input", "Worker kind must be lua or batch.");
                Write(result);
                return 0;
            }
            if (options.Command is "help" or "version" or "capabilities")
            {
                Write(options.Command switch
                {
                    "version" => new { version = Version, apiVersion = 1, formatVersion = MapDocument.CurrentFormatVersion },
                    "capabilities" => (object)AutomationEngine.Describe(),
                    _ => new { name = "Metroidvania Studio", version = Version, commands = new[] { "init", "inspect", "query-tiles", "validate", "apply", "run", "export-room", "preview", "mcp", "capabilities", "version" }, usage = "--workspace DIRECTORY COMMAND --map RELATIVE_FILE [--batch FILE | --script FILE] [--dry-run]", docs = "docs/cli/quick-start.md" }
                });
                return 0;
            }
            using var live = options.Value("url") is string url ? new LiveClient(url, options.Flag("read-only")) : null;
            using var workspace = options.Value("workspace") is string directory ? new WorkspaceFiles(directory, options.Flag("read-only")) : live == null ? throw new CliException("invalid_input", "Provide --workspace or --url.") : null;
            WorkspaceFiles RequireFiles() => workspace ?? throw new CliException("invalid_input", "Provide --workspace to read the batch or script file.");
            var service = new StudioService(workspace, live);
            if (options.Command is "mcp" or "serve")
            {
                await StudioMcp.RunAsync(service, cancellation.Token);
                return 0;
            }
            object response = options.Command switch
            {
                "init" or "create" => service.Create(options.Required("map"), options.Value("name") ?? "Untitled", options.Flag("dry-run")),
                "inspect" => service.Inspect(options.Required("map")),
                "query-tiles" => service.QueryTiles(options.Required("map"), options.Required("room"), options.Required("layer"), options.Int("x", 0), options.Int("y", 0), options.Int("width", 1), options.Int("height", 1)),
                "validate" => service.Validate(options.Required("map")),
                "apply" => await service.ApplyAsync(options.Required("map"), RequireFiles().Read(options.Required("batch")).Text, options.Value("expected-revision"), options.Flag("dry-run"), cancellation.Token),
                "run" => await service.RunAsync(options.Required("map"), RequireFiles().Read(options.Required("script"), 1024 * 1024).Text, options.Int("seed", 1), options.Value("expected-revision"), options.Flag("dry-run"), cancellation.Token),
                "export-room" => service.ExportRoom(options.Required("map"), options.Required("room"), options.Required("output"), options.Flag("force"), options.Flag("dry-run")),
                "preview" => service.Preview(options.Required("map"), options.Value("output"), options.Flag("force"), options.Flag("dry-run")),
                _ => throw new CliException("invalid_input", "Unknown command. Use help for available commands.")
            };
            Write(response);
            return 0;
        }
        catch (Exception error)
        {
            var failure = CliException.From(error);
            if (protocolMode) Console.Error.WriteLine(JsonSerializer.Serialize(new { error = new { failure.Code, failure.Message } }, Json));
            else Write(new { error = new { failure.Code, failure.Message } });
            return failure.ExitCode;
        }
    }

    internal static void Write(object value)
    {
        string json = JsonSerializer.Serialize(value, Json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumInputBytes) throw new CliException("limit_exceeded", "The result exceeds the output limit.");
        Console.WriteLine(json);
    }
}

internal sealed class Options
{
    public string Command { get; private set; } = "help";
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "dry-run", "read-only", "force" };
    private static readonly HashSet<string> Values = new(StringComparer.Ordinal) { "workspace", "url", "layer", "x", "y", "width", "height", "map", "name", "batch", "script", "room", "output", "seed", "expected-revision" };
    public string? Value(string name) => values.GetValueOrDefault(name);
    public bool Flag(string name) => values.ContainsKey(name);
    public string Required(string name) => Value(name) ?? throw new CliException("invalid_input", "Missing --" + name + ".");
    public int Int(string name, int fallback) => Value(name) is not string value ? fallback : int.TryParse(value, out int n) ? n : throw new CliException("invalid_input", "--" + name + " must be an integer.");
    public static bool IsProtocol(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal)) { if (Values.Contains(args[i][2..])) i++; continue; }
            if (args[i].StartsWith("-", StringComparison.Ordinal)) continue;
            return args[i] is "mcp" or "serve";
        }
        return false;
    }
    public static Options Parse(string[] args)
    {
        var result = new Options();
        bool commandSet = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "--help" or "-h") { result.Command = "help"; commandSet = true; continue; }
            if (arg is "--version" or "-v") { result.Command = "version"; commandSet = true; continue; }
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (commandSet) throw new CliException("invalid_input", "Only one command is allowed.");
                result.Command = arg; commandSet = true; continue;
            }
            string name = arg[2..];
            if (!Flags.Contains(name) && !Values.Contains(name)) throw new CliException("invalid_input", "Unknown option --" + name + ".");
            string value = Flags.Contains(name) ? "true" : ++i < args.Length ? args[i] : throw new CliException("invalid_input", "Missing value for --" + name + ".");
            if (!result.values.TryAdd(name, value)) throw new CliException("invalid_input", "Duplicate --" + name + ".");
        }
        return result;
    }
}

internal sealed class CliException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public int ExitCode => Code switch { "invalid_input" => 2, "conflict" => 3, "workspace_busy" => 4, "limit_exceeded" => 5, "read_only" => 6, "cancelled" => 130, _ => 1 };
    public static CliException From(Exception error) => error switch
    {
        CliException known => known,
        OperationCanceledException => new("cancelled", "The operation was cancelled; no pending edit was saved."),
        LuaScriptException => new("invalid_input", error.Message),
        InvalidDataException => new("invalid_input", error.Message),
        JsonException => new("invalid_input", "Invalid JSON input."),
        ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException => new("invalid_input", error.Message),
        UnauthorizedAccessException => new("io_error", "The requested file is not accessible."),
        IOException => new("io_error", "The workspace file could not be read or saved."),
        _ => new("internal_error", "The operation failed. No pending edit was saved.")
    };
}

internal static class BoundedText
{
    public static async Task<string> ReadAsync(Stream input, int maximumBytes, CancellationToken cancellation)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[32768];
        while (true)
        {
            int count = await input.ReadAsync(chunk, cancellation);
            if (count == 0) break;
            if (buffer.Length + count > maximumBytes) throw new CliException("limit_exceeded", "Input exceeds the supported size.");
            buffer.Write(chunk, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)).TrimStart('\uFEFF');
    }
}
