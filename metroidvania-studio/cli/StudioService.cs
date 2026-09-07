using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using MetroidvaniaStudio.Automation;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio.Cli;

internal sealed class StudioService(WorkspaceFiles? files, LiveClient? live = null)
{
    private WorkspaceFiles Files => files ?? throw new CliException("invalid_input", "This command requires --workspace for local files.");
    internal object Create(string path, string name, bool dryRun)
    {
        if (live != null) throw new CliException("invalid_input", "Create a map in offline mode; live edits target @active.");
        using var edit = Files.BeginEdit(dryRun);
        if (Files.Exists(path)) throw new CliException("conflict", "The map file already exists.");
        if (name.Length > 4096) throw new CliException("limit_exceeded", "The map name is too long.");
        var document = new MapDocument { name = name };
        string json = MapJson.ToJson(document, true);
        string revision = dryRun ? WorkspaceFiles.Hash(Encoding.UTF8.GetBytes(json)) : Files.Write(path, json, null, true, CancellationToken.None);
        return new { path, revision, dryRun, changed = true, roomCount = 0 };
    }
    internal object Inspect(string path)
    {
        if (live != null) { LiveClient.Active(path); return live.InspectAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        FileSnapshot file = Files.Read(path);
        return new { path, revision = file.Revision, document = AutomationEngine.Inspect(file.Text) };
    }
    internal object QueryTiles(string path, string roomId, string layer, int x, int y, int width, int height)
    {
        if (live != null) { LiveClient.Active(path); return live.QueryTilesAsync(roomId, layer, x, y, width, height, CancellationToken.None).GetAwaiter().GetResult(); }
        var file = Files.Read(path);
        return new { path, revision = file.Revision, query = AutomationEngine.QueryTiles(file.Text, roomId, layer, x, y, width, height) };
    }
    internal object Validate(string path)
    {
        if (live != null) { LiveClient.Active(path); return live.ValidateAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        FileSnapshot file = Files.Read(path);
        var map = Parse(file.Text);
        return new { path, revision = file.Revision, valid = true, map.formatVersion, rooms = map.rooms.Count };
    }
    internal Task<object> ApplyAsync(string path, string batchJson, string? expected, bool dryRun, CancellationToken cancellation)
    {
        using var batch = JsonDocument.Parse(batchJson);
        if (live != null) { LiveClient.Active(path); return live.EditAsync("batch", null, batch.RootElement.Clone(), 1, expected, dryRun, cancellation); }
        return EditAsync(path, expected, dryRun, document => new { kind = "batch", documentJson = document, batch = batch.RootElement.Clone() }, cancellation);
    }
    internal Task<object> RunAsync(string path, string source, int seed, string? expected, bool dryRun, CancellationToken cancellation)
    {
        if (Encoding.UTF8.GetByteCount(source) > MetroidvaniaStudio.Scripting.LuaScriptRunner.MaximumSourceBytes) throw new CliException("limit_exceeded", "Lua scripts cannot exceed 64 KiB.");
        if (live != null) { LiveClient.Active(path); return live.EditAsync("lua", source, null, seed, expected, dryRun, cancellation); }
        return EditAsync(path, expected, dryRun, document => new { kind = "lua", documentJson = document, source, seed }, cancellation);
    }
    private async Task<object> EditAsync(string path, string? expected, bool dryRun, Func<string, object> request, CancellationToken cancellation)
    {
        using var edit = await Files.BeginEditAsync(dryRun, cancellation);
        FileSnapshot original = Files.Read(path);
        if (expected != null && original.Revision != expected) throw new CliException("conflict", "The map revision does not match. Inspect the map before retrying.");
        Parse(original.Text);
        var result = await WorkerClient.RunAsync(request(original.Text), cancellation);
        Parse(result.DocumentJson);
        cancellation.ThrowIfCancellationRequested();
        string revision = original.Revision;
        if (result.Changed && !dryRun) revision = Files.Write(path, result.DocumentJson, original.Revision, false, cancellation);
        return new { path, previousRevision = original.Revision, revision, dryRun, result.Changed, result.OperationCount, createdIds = result.CreatedIds ?? [], result.Logs, preview = dryRun ? AutomationEngine.Inspect(result.DocumentJson) : (JsonElement?)null };
    }
    internal object ExportRoom(string path, string roomId, string output, bool overwrite, bool dryRun)
    {
        if (live != null) throw new CliException("invalid_input", "Export saved JSON in offline mode, or use the web editor export menu.");
        using var edit = Files.BeginEdit(dryRun);
        var file = Files.Read(path);
        var map = Parse(file.Text);
        if (!map.rooms.Any(r => r.id == roomId)) throw new CliException("invalid_input", "The requested room ID does not exist.");
        if (Files.Resolve(path).Equals(Files.Resolve(output), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new CliException("invalid_input", "Export output must differ from the source map.");
        string json = MapRoomJsonExporter.Plan(map, [roomId]).Single().Json;
        string? revision = SaveOutput(output, json, overwrite, dryRun);
        return new { output, revision, roomId, dryRun };
    }
    internal object Preview(string path, string? output, bool overwrite, bool dryRun)
    {
        if (live != null)
        {
            LiveClient.Active(path);
            if (output != null) throw new CliException("invalid_input", "Connected previews return SVG in JSON. Omit --output.");
            return live.PreviewAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        using var edit = Files.BeginEdit(output == null || dryRun);
        var map = Parse(Files.Read(path).Text);
        string svg = MiniMapSvg.Render(map);
        if (output != null && Files.Resolve(path).Equals(Files.Resolve(output), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new CliException("invalid_input", "Preview output must differ from the source map.");
        string? revision = output == null ? null : SaveOutput(output, svg, overwrite, dryRun);
        return new { output, revision, mimeType = "image/svg+xml", svg = output == null || dryRun ? svg : null, roomCount = map.rooms.Count, dryRun };
    }
    private string? SaveOutput(string output, string text, bool overwrite, bool dryRun)
    {
        bool exists = Files.Exists(output);
        if (exists && !overwrite) throw new CliException("conflict", "The output file already exists. Choose another path or set force explicitly.");
        string? expected = exists ? Files.Read(output).Revision : null;
        return dryRun ? null : Files.Write(output, text, expected, !exists, CancellationToken.None);
    }
    private static MapDocument Parse(string text)
    {
        var map = MapJson.FromJson<MapDocument>(text) ?? throw new CliException("invalid_input", "A map document is required.");
        map.Validate();
        return map;
    }
}

internal static class MiniMapSvg
{
    public static string Render(MapDocument map)
    {
        var bounds = MiniMapGeometry.GetBounds(map);
        double width = bounds.IsEmpty ? 64 : Math.Max(1, bounds.Width), height = bounds.IsEmpty ? 64 : Math.Max(1, bounds.Height);
        double stroke = Math.Max(.15, Math.Min(width, height) / 300), margin = stroke * 6;
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{-margin} {-margin} {width + margin * 2} {height + margin * 2}\" width=\"960\" height=\"640\" role=\"img\" aria-label=\"Room layout\"><rect x=\"{-margin}\" y=\"{-margin}\" width=\"{width + margin * 2}\" height=\"{height + margin * 2}\" fill=\"#15171b\"/>");
        foreach (var room in map.rooms)
        {
            string color = MapRoomTheme.TryGetColor(room, out Color theme) ? "#" + ColorText.ToHtmlStringRGB(theme) : "#3879ad";
            svg.Append(CultureInfo.InvariantCulture, $"<g><title>{SecurityElement.Escape(room.name)}</title><rect x=\"{(double)room.x - bounds.XMin}\" y=\"{bounds.YMax - room.y - room.height}\" width=\"{room.width}\" height=\"{room.height}\" fill=\"{color}\" stroke=\"#ffffff\" stroke-width=\"{stroke}\"/></g>");
        }
        svg.Append("</svg>");
        return svg.ToString();
    }
}
