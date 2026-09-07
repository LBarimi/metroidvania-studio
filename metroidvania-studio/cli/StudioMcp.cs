using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MetroidvaniaStudio.Automation;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MetroidvaniaStudio.Cli;

internal sealed class StudioMcp(StudioService service)
{
    public static async Task RunAsync(StudioService service, CancellationToken cancellation)
    {
        var methods = new StudioMcp(service);
        using var input = new BoundedJsonLinesStream(Console.OpenStandardInput(), 4 * 1024 * 1024);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "metroidvania-studio", Title = "Metroidvania Studio", Version = Program.Version },
            ServerInstructions = "Edit maps in the explicitly configured local workspace. Inspect a map before editing and pass its revision. Use capabilities for operation schemas, and dryRun to preview. No network or filesystem access is provided to Lua scripts. With --url, use map @active for the live web document. When a workspace is open in the web editor, use connected mode instead of offline writes.",
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>()
        };
        foreach (var method in typeof(StudioMcp).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (method.IsDefined(typeof(McpServerToolAttribute))) options.ToolCollection.Add(McpServerTool.Create(method, methods));
        await using var server = McpServer.Create(new StreamServerTransport(input, Console.OpenStandardOutput()), options);
        await server.RunAsync(cancellation);
    }

    [McpServerTool(Name = "studio_capabilities", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Get the versioned editing API, complete operation schemas, and limits. Call before authoring batch commands or Lua scripts.")]
    public CallToolResult Capabilities() => Result(() => AutomationEngine.Describe());

    [McpServerTool(Name = "studio_create", Destructive = false, OpenWorld = false)]
    [Description("Create an empty map JSON at a new relative workspace path. Existing files are never overwritten. Add connected rooms with studio_apply.")]
    public CallToolResult Create([Description("New map JSON path relative to the workspace.")] string map,
        [Description("Map display name.")] string name = "Untitled", [Description("Validate and preview without writing files.")] bool dryRun = false)
        => Result(() => service.Create(map, name, dryRun));

    [McpServerTool(Name = "studio_inspect", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Read map metadata, room IDs and bounds, camera settings, and the current SHA-256 revision needed for editing.")]
    public CallToolResult Inspect([Description("Existing map JSON path relative to the workspace, or @active in connected mode.")] string map)
        => Result(() => service.Inspect(map));

    [McpServerTool(Name = "studio_query_tiles", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect occupied tiles in a room-local rectangle of at most 4096 cells. Empty coordinates are omitted. Returns shapes, resource material IDs, groups, and the current map revision without editing.")]
    public CallToolResult QueryTiles([Description("Existing map path, or @active in connected mode.")] string map,
        [Description("Stable room ID from studio_inspect.")] string roomId,
        [Description("Tile layer: foreground or background.")] string layer,
        [Description("Room-local left coordinate.")] int x = 0,
        [Description("Room-local bottom coordinate.")] int y = 0,
        [Description("Width in tiles; the query must fit inside the room.")] int width = 1,
        [Description("Height in tiles; width times height cannot exceed 4096.")] int height = 1)
        => Result(() => service.QueryTiles(map, roomId, layer, x, y, width, height));

    [McpServerTool(Name = "studio_validate", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Validate a map JSON document against the supported map data contract without changing files.")]
    public CallToolResult Validate([Description("Existing map JSON path relative to the workspace, or @active in connected mode.")] string map)
        => Result(() => service.Validate(map));

    [McpServerTool(Name = "studio_apply", Destructive = true, OpenWorld = false)]
    [Description("Apply a versioned operation batch atomically. Pass {apiVersion:1,operations:[...]} using schemas from studio_capabilities. A failed or cancelled batch saves nothing.")]
    public Task<CallToolResult> Apply([Description("Existing map JSON path relative to the workspace, or @active in connected mode.")] string map,
        [Description("The complete API operation envelope.")] JsonElement batch,
        [Description("SHA-256 revision returned by studio_inspect. A mismatch refuses the edit.")] string expectedRevision,
        [Description("Return proposed changes without writing files.")] bool dryRun = false, CancellationToken cancellationToken = default)
        => ResultAsync(() => service.ApplyAsync(map, batch.GetRawText(), RequireRevision(expectedRevision), dryRun, cancellationToken));

    [McpServerTool(Name = "studio_run_lua", Destructive = true, OpenWorld = false)]
    [Description("Run bounded Lua source with the studio editing API in an isolated worker. No OS, filesystem, network, package, or process APIs. The whole edit is atomic; timeout, error, or cancellation saves nothing.")]
    public Task<CallToolResult> RunLua([Description("Existing map JSON path relative to the workspace, or @active in connected mode.")] string map,
        [Description("Lua source text, at most 64 KiB. See docs/scripting.")] string source,
        [Description("SHA-256 revision returned by studio_inspect.")] string expectedRevision,
        [Description("Random seed for reproducible scripts.")] int seed = 1,
        [Description("Return proposed changes without writing files.")] bool dryRun = false, CancellationToken cancellationToken = default)
        => ResultAsync(() => service.RunAsync(map, source, seed, RequireRevision(expectedRevision), dryRun, cancellationToken));

    [McpServerTool(Name = "studio_export_room", Destructive = true, OpenWorld = false)]
    [Description("Export one room as an independent engine-compatible JSON document, preserving shared settings and resource identifiers.")]
    public CallToolResult ExportRoom([Description("Source map JSON path relative to the workspace.")] string map,
        [Description("Stable room ID from studio_inspect.")] string roomId,
        [Description("Output JSON path relative to the workspace, different from the source.")] string output,
        [Description("Explicitly allow replacing an existing output file.")] bool force = false,
        [Description("Validate without writing files.")] bool dryRun = false)
        => Result(() => service.ExportRoom(map, roomId, output, force, dryRun));

    [McpServerTool(Name = "studio_preview", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Return a headless SVG room-layout preview with region colors and white room outlines. No browser or graphics engine is required. This overview shows room rectangles, not rendered tile artwork.")]
    public CallToolResult Preview([Description("Existing map JSON path relative to the workspace, or @active in connected mode.")] string map)
        => Result(() => service.Preview(map, null, false, true));

    private static string RequireRevision(string value)
    {
        if (value == null || value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c)))
            throw new CliException("invalid_input", "expectedRevision must be the SHA-256 revision from studio_inspect.");
        return value.ToLowerInvariant();
    }
    private static CallToolResult Result(Func<object> action)
    {
        try { return Content(action(), false); }
        catch (Exception error) { return Failure(error); }
    }
    private static async Task<CallToolResult> ResultAsync(Func<Task<object>> action)
    {
        try { return Content(await action(), false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { return Failure(error); }
    }
    private static CallToolResult Failure(Exception error)
    {
        var failure = CliException.From(error);
        return Content(new { error = new { failure.Code, failure.Message } }, true);
    }
    private static CallToolResult Content(object value, bool isError)
    {
        string json = JsonSerializer.Serialize(value, Program.Json);
        return new() { Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.SerializeToElement(value, Program.Json), IsError = isError };
    }
}

internal sealed class BoundedJsonLinesStream(Stream input, int maximumLineBytes) : Stream
{
    private int pending;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    private void Check(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value == 10) pending = 0;
            else if (++pending > maximumLineBytes) throw new IOException("The MCP message exceeds the 4 MiB limit.");
        }
    }
    public override int Read(byte[] buffer, int offset, int count) { int n = input.Read(buffer, offset, count); Check(buffer.AsSpan(offset, n)); return n; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { int n = await input.ReadAsync(buffer, cancellationToken); Check(buffer.Span[..n]); return n; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
}
