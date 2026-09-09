using System.Net;
using System.Diagnostics;
using MetroidvaniaStudio.Storage;
using System.Text.Json;
using MetroidvaniaStudio.Server;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
const int MaximumRequestBodySize = 32 * 1024 * 1024;
const int KestrelRequestBodyAllowance = MaximumRequestBodySize + 64 * 1024;
string studioRoot = Path.GetFullPath(builder.Configuration["studio-root"] ?? Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string project = Path.GetFullPath(builder.Configuration["project"] ?? PortableWorkspace.Prepare(studioRoot, Path.Combine(studioRoot, ".local/workspace")));
int port = int.Parse(builder.Configuration["port"] ?? "18765");
if (port < 1024 || port > 65535) throw new ArgumentException("Choose a user port between 1024 and 65535.");
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port);
    // Let middleware return a deterministic JSON 413 for a request just over
    // the application limit instead of resetting the transport mid-upload.
    options.Limits.MaxRequestBodySize = KestrelRequestBodyAllowance;
});
builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
string sessionDirectory = Path.Combine(project, ".studio"), sessionFile = Path.Combine(sessionDirectory, "server.lock");
foreach (string candidate in new[] { sessionDirectory, sessionFile })
    if (File.Exists(candidate) || Directory.Exists(candidate))
        if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) throw new IOException("Workspace session files cannot be symbolic links.");
Directory.CreateDirectory(sessionDirectory);
using var workspaceLock = new FileStream(sessionFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
PortableWorkspace.SeedResources(project, studioRoot);
var files = new ProjectFiles(project, studioRoot: studioRoot);
TextureLibrary.Prepare(files, studioRoot);
BuiltInPalettes.Prepare(files, studioRoot);
BuiltInObjects.Prepare(files, studioRoot);
BundledTilesets.Prepare(files, studioRoot);
var workspace = new EditorWorkspace(files);
using var nativeMaps = new NativeMapFiles(files);
string webRoot = Path.GetFullPath(builder.Configuration["web-root"] ?? Path.Combine(studioRoot, "metroidvania-studio/dist"));
var app = builder.Build();
app.Use(async (context, next) =>
{
    // Reject DNS rebinding and cross-site writes. No CORS or external resource origin is enabled.
    if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None) ||
        context.Request.Host.Port != port || context.Request.Host.Host is not ("127.0.0.1" or "localhost"))
    { context.Response.StatusCode = 403; return; }
    string origin = context.Request.Headers.Origin.ToString();
    if (origin.Length > 0 && origin != $"http://127.0.0.1:{port}" && origin != $"http://localhost:{port}")
    { context.Response.StatusCode = 403; return; }
    if (context.Request.Method == "POST" && !context.Request.HasJsonContentType())
    { context.Response.StatusCode = 415; return; }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'none'";
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Method == "POST" && context.Request.ContentLength > MaximumRequestBodySize)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(new { error = "Request body cannot exceed 32 MiB." });
        return;
    }
    var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (context.Request.Method == "POST" && bodyLimit is { IsReadOnly: false })
        bodyLimit.MaxRequestBodySize = MaximumRequestBodySize;
    try { await next(context); }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or WorkspaceConflict or KeyNotFoundException or OverflowException or FormatException)
    {
        context.Response.StatusCode = error switch
        {
            WorkspaceConflict => 409,
            BadHttpRequestException badRequest => badRequest.StatusCode,
            _ => 400
        };
        await context.Response.WriteAsJsonAsync(new { error = error.Message });
    }
});
app.MapGet("/api/health", () => Results.Json(new { instanceId = workspace.InstanceId, projectPath = files.ProjectPath, workspacePath = files.ProjectPath, launchToken = builder.Configuration["launch-token"], processId = Environment.ProcessId, runtimePath = typeof(EditorWorkspace).Assembly.Location }));
app.MapPost("/api/shutdown", async (HttpContext context) =>
{
    if (context.Request.Headers["X-Metroidvania-Studio-Instance"].ToString() != workspace.InstanceId)
        return Results.Json(new { error = "The server instance does not match the shutdown request." }, statusCode: 409);
    lock (workspace.Gate) workspace.BeginShutdown();
    try
    {
        await Task.Run(() => { workspace.FlushRecovery(); workspace.FlushAutoExports(); });
        lock (workspace.Gate) workspace.ValidateShutdown();
    }
    catch
    {
        lock (workspace.Gate) workspace.AbortShutdown();
        throw;
    }
    context.Response.OnCompleted(() => { app.Lifetime.StopApplication(); return Task.CompletedTask; });
    return Results.Ok(new { stopping = true, instanceId = workspace.InstanceId });
});
app.MapGet("/api/state", (long? since, string? instanceId, long? documentRevision, long? catalogRevision, bool? full) =>
{
    lock (workspace.Gate)
    {
        bool sameInstance = instanceId == workspace.InstanceId;
        if (workspace.IsStateCurrent(since, instanceId, documentRevision, catalogRevision)) return Results.NoContent();
        bool includeDocument = full == true || !sameInstance || documentRevision != workspace.DocumentRevision;
        bool includeCatalog = full == true || !sameInstance || catalogRevision != workspace.CatalogRevision;
        return Results.Json(workspace.State(includeDocument, includeCatalog));
    }
});
app.MapGet("/api/room-resolution-fit", (string id) =>
{
    lock (workspace.Gate) return Results.Json(workspace.PreviewRoomResolutionFit(id));
});
app.MapPost("/api/command", async (HttpRequest request) =>
{
    using var command = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
    string requestFingerprint = EditorWorkspace.FingerprintCommand(command.RootElement);
    lock (workspace.Gate)
    {
        long before = workspace.DocumentRevision, catalogBefore = workspace.CatalogRevision;
        string? roomBefore = workspace.Canvas.ActiveRoomId;
        bool executed = workspace.Command(command.RootElement, requestFingerprint);
        bool compactDocument = executed && command.RootElement.TryGetProperty("action", out JsonElement action)
            && action.ValueKind == JsonValueKind.String && action.GetString() == "tileGesture"
            && command.RootElement.TryGetProperty("compactDocument", out JsonElement compact)
            && compact.ValueKind is JsonValueKind.True;
        bool includeSelection = !compactDocument || roomBefore != workspace.Canvas.ActiveRoomId;
        // A duplicate acknowledges a response that may have been lost before the
        // client received any revision tokens. Send a complete resync so catalog
        // changes between the first execution and retry cannot be hidden forever.
        return Results.Json(workspace.State(!compactDocument && (!executed || before != workspace.DocumentRevision), !executed || catalogBefore != workspace.CatalogRevision, includeSelection));
    }
});
app.MapAutomation(workspace, studioRoot);
app.MapScriptLibrary(new ScriptLibrary(files.ProjectPath, studioRoot));
app.MapDocumentation(webRoot);
app.MapGet("/api/palette-template", (string mode, string color) =>
{
    if (color.Length != 7 || color[0] != '#' || !color[1..].All(char.IsAsciiHexDigit)) throw new ArgumentException("@paletteInvalidColor");
    return Results.File(TilesetComposer.Template(mode, color), "image/png", "tileset-" + mode + ".png");
});
string CurrentExportDirectory()
{
    lock (workspace.Gate) return files.AutoExportPath(AutoRoomExporter.MapKey(workspace.Session.FilePath == null ? null : files.Relative(workspace.Session.FilePath)));
}
app.MapGet("/api/workspace-folders", () => Results.Json(new { project = files.ProjectPath, maps = files.MapsPath,
    textures = files.TexturesPath, catalog = files.CatalogWritePath, autoExport = files.AutoExportDirectory, currentExport = CurrentExportDirectory(), nativeMapDialogs = nativeMaps.Available }));
app.MapPost("/api/workspace-folders/open", async (HttpRequest request) =>
{
    using var body = await JsonDocument.ParseAsync(request.Body);
    string folder = body.RootElement.GetProperty("folder").GetString() switch
    {
        "maps" => files.MapsPath, "textures" => files.TextureFolder(body.RootElement.TryGetProperty("asset", out var asset) ? asset.GetString() : null), "exports" => CurrentExportDirectory(),
        _ => throw new ArgumentException("Choose Maps or Textures.")
    };
    Directory.CreateDirectory(folder);
    var start = OperatingSystem.IsWindows() ? new ProcessStartInfo(folder) { UseShellExecute = true }
        : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
    if (!OperatingSystem.IsWindows()) start.ArgumentList.Add(folder);
    using var process = Process.Start(start);
    return Results.Ok();
});
app.MapPost("/api/native-map/pick", async (HttpRequest request) =>
{
    using var body = await JsonDocument.ParseAsync(request.Body);
    var value = body.RootElement;
    return Results.Json(await nativeMaps.Pick(value.GetProperty("mode").GetString()!, value.GetProperty("name").GetString()!, request.HttpContext.RequestAborted));
});
app.MapPost("/api/native-map/read", async (HttpRequest request) =>
{
    using var body = await JsonDocument.ParseAsync(request.Body);
    return Results.Bytes(nativeMaps.Read(body.RootElement.GetProperty("token").GetString()!), "application/json");
});
app.MapPost("/api/native-map/write", async (HttpRequest request) =>
{
    using var body = await JsonDocument.ParseAsync(request.Body);
    var value = body.RootElement;
    nativeMaps.Write(value.GetProperty("token").GetString()!, value.GetProperty("document").GetRawText(), value.GetProperty("hash").GetString()!);
    return Results.Ok();
});
app.MapGet("/api/files", () => Results.Json(files.List()));
app.MapGet("/api/locale", () => Results.File(Path.Combine(studioRoot, "metroidvania-studio/localization/MetroidvaniaStudioLocale.csv"), "text/csv; charset=utf-8"));
app.MapGet("/api/asset", (string path, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    string asset = files.Asset(path);
    string type = Path.GetExtension(asset).ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", _ => "image/webp" };
    return Results.File(asset, type);
});
app.MapGet("/{**path}", (string? path) =>
{
    path = string.IsNullOrWhiteSpace(path) ? "index.html" : path;
    if (path.Contains('/') || path.Contains('\\') || path.Contains("..") || path.Contains(':')) return Results.NotFound();
    string file = Path.Combine(webRoot, path);
    string? type = Path.GetExtension(file) switch { ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8", ".svg" => "image/svg+xml", ".json" when path == "build-info.json" => "application/json; charset=utf-8", _ => null };
    return type != null && File.Exists(file) ? Results.File(file, type) : Results.NotFound();
});
using var timer = new Timer(_ =>
{
    lock (workspace.Gate)
    {
        try { workspace.Tick(); }
        catch (Exception error) { app.Logger.LogWarning("Map refresh: {Message}", error.Message); }
    }
}, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
app.Lifetime.ApplicationStopping.Register(() =>
{
    lock (workspace.Gate) workspace.Cancel();
    workspace.FlushRecovery();
    workspace.FlushAutoExports();
    workspace.StopAutoExports();
});
Console.WriteLine($"Metroidvania Studio: http://127.0.0.1:{port} | Workspace: {project}");
app.Run();
