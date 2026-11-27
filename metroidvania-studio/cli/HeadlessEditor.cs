using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MetroidvaniaStudio.Automation;
using MetroidvaniaStudio.Server;

namespace MetroidvaniaStudio.Cli;

internal sealed class HeadlessEditor(WorkspaceFiles files, EditorWorkspace editor, ScriptLibrary scripts)
{
    private const int MaximumCommandBytes = 32 * 1024 * 1024;
    private readonly string client = "headless-" + Guid.NewGuid().ToString("N");

    internal static async Task RunAsync(Options options, WorkspaceFiles files, CancellationToken cancellation)
    {
        options.Only("workspace", "commands", "read-only");
        string? source = options.Value("commands");
        JsonDocument? instructions = null;
        if (source != null)
        {
            string input = source == "-" ? await BoundedText.ReadAsync(Console.OpenStandardInput(), MaximumCommandBytes, cancellation)
                : files.Read(source, MaximumCommandBytes).Text;
            instructions = JsonDocument.Parse(input);
        }
        using var parsed = instructions;
        using var lease = files.BeginEdit(false);
        string studio = HeadlessGeneration.InstallationRoot();
        var workspace = EditorRuntime.Open(files.Root, studio);
        var session = new HeadlessEditor(files, workspace, new ScriptLibrary(files.Root, studio));
        try
        {
            if (parsed != null)
            {
                if (parsed.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var command in parsed.RootElement.EnumerateArray()) await session.Reply(command, cancellation);
                else await session.Reply(parsed.RootElement, cancellation);
                return;
            }
            using var input = new BoundedJsonLinesStream(Console.OpenStandardInput(), MaximumCommandBytes);
            using var reader = new StreamReader(input, new UTF8Encoding(false, true));
            while (await reader.ReadLineAsync(cancellation) is string line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var command = JsonDocument.Parse(line);
                await session.Reply(command.RootElement, cancellation);
            }
        }
        finally
        {
            try
            {
                lock (workspace.Gate) workspace.BeginShutdown();
                workspace.FlushRecovery();
                workspace.FlushAutoExports();
                lock (workspace.Gate) workspace.ValidateShutdown();
            }
            finally { workspace.StopAutoExports(); }
        }
    }

    private async Task Reply(JsonElement request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("action", out var name) || name.ValueKind != JsonValueKind.String)
            throw new CliException("invalid_input", "Provide a JSON object with an action name.");
        string action = name.GetString()!;
        lock (editor.Gate) editor.Tick();
        object result;
        switch (action)
        {
            case "state":
                lock (editor.Gate) result = editor.State(request.TryGetProperty("full", out var full) && full.GetBoolean(), false);
                break;
            case "catalog":
                lock (editor.Gate) result = editor.State(false, true);
                break;
            case "files": result = editor.Files.List(); break;
            case "inspect": result = AutomationEngine.Inspect(editor.Session.CurrentJson, cancellation); break;
            case "queryTiles": result = AutomationEngine.QueryTiles(editor.Session.CurrentJson, Text("roomId"), Text("layer"),
                Int("x",0), Int("y",0), Int("width",1), Int("height",1), cancellation); break;
            case "roomResolutionFit": result = editor.PreviewRoomResolutionFit(Text("id")); break;
            case "paletteTemplate":
                string png = Text("output");
                if (!png.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Template output must end in .png.");
                byte[] template = EditorRuntime.PaletteTemplate(Text("mode"), Text("color"));
                files.CreateOutput(png, stream => stream.Write(template));
                result = new { output = png };
                break;
            case "scripts": result = scripts.List(); break;
            case "readScript": result = scripts.Read(Text("id")); break;
            case "saveScript": result = scripts.Save(Text("name"), Text("source"), request.GetProperty("expectedRevision").GetString()); break;
            case "bundle":
                string output = Text("output");
                if (!output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Bundle output must end in .zip.");
                var bundle = editor.CaptureProjectBundle(editor.InstanceId, editor.DocumentRevision, editor.CatalogRevision);
                files.CreateOutput(output, stream => ProjectBundleExporter.Write(bundle, editor.Files, stream, cancellation));
                result = new { output };
                break;
            case "runLua":
            case "applyBatch":
                var snapshot = editor.CaptureAutomation(editor.InstanceId, editor.DocumentRevision);
                object job = action == "runLua" ? new { kind = "lua", documentJson = snapshot.DocumentJson, source = Text("source"), seed = Int("seed",1) }
                    : new { kind = "batch", documentJson = snapshot.DocumentJson, batch = request.GetProperty("batch") };
                var run = await WorkerClient.RunAsync(job, cancellation);
                bool dryRun = request.TryGetProperty("dryRun", out var dry) && dry.GetBoolean();
                if (!dryRun) editor.ApplyAutomation(snapshot, run.DocumentJson, cancellation);
                result = new { dryRun, run.Changed, run.OperationCount, run.Logs, run.CreatedIds };
                break;
            default:
                var cmd = JsonNode.Parse(request.GetRawText())!.AsObject();
                if (cmd.ContainsKey("clientId")) throw new ArgumentException("The editor session owns its clientId.");
                cmd["clientId"] = client;
                if (!cmd.ContainsKey("commandId")) cmd["commandId"] = Guid.NewGuid().ToString("N");
                lock (editor.Gate)
                {
                    if (cmd.ContainsKey("expectedInstanceId") != cmd.ContainsKey("expectedRevision"))
                        throw new ArgumentException("Supply both expectedInstanceId and expectedRevision from state.");
                    if (!cmd.ContainsKey("expectedRevision"))
                    {
                        cmd["expectedInstanceId"] = editor.InstanceId;
                        cmd["expectedRevision"] = editor.Revision;
                    }
                    if (action is "tileGesture" or "objectGesture" && !cmd.ContainsKey("baseRevision"))
                        cmd["baseRevision"] = cmd["expectedRevision"]!.DeepClone();
                    editor.Command(JsonSerializer.SerializeToElement(cmd));
                    var status = editor.State(false, false, false);
                    result = new { status.instanceId, status.revision, status.documentRevision, status.catalogRevision,
                        status.file, status.dirty, status.canUndo, status.canRedo, roomId = editor.Canvas.ActiveRoomId, roomCount = editor.Session.Document.rooms.Count, status.notice };
                }
                break;
        }
        Program.Write(new { action, result });
        string Text(string key) => request.GetProperty(key).GetString() ?? throw new ArgumentException("Expected a string for " + key);
        int Int(string key, int fallback) => request.TryGetProperty(key, out var n) ? n.GetInt32() : fallback;
    }
}
