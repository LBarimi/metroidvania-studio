using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetroidvaniaStudio.Automation;

namespace MetroidvaniaStudio.Cli;

internal sealed class LiveClient : IDisposable
{
    private readonly HttpClient http;
    private readonly bool readOnly;
    private readonly string clientId = "cli-" + Guid.NewGuid().ToString("N");
    private sealed record Snapshot(string InstanceId, long DocumentRevision, string DocumentJson, string Revision);

    public LiveClient(string url, bool readOnly)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host is not ("127.0.0.1" or "localhost")
            || uri.Port < 1024 || uri.Port > 65535 || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" || uri.AbsolutePath != "/")
            throw new CliException("invalid_input", "--url must be an HTTP localhost or 127.0.0.1 address with an explicit user port and no path.");
        http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = new UriBuilder(uri) { Host = "127.0.0.1" }.Uri, Timeout = TimeSpan.FromSeconds(10) };
        this.readOnly = readOnly;
    }
    public void Dispose() => http.Dispose();
    internal static void Active(string map)
    {
        if (map != "@active") throw new CliException("invalid_input", "Connected commands use --map @active to edit the document currently open in the web editor.");
    }
    private async Task<Snapshot> ReadAsync(CancellationToken cancellation)
    {
        var value = await RequestAsync(HttpMethod.Get, "api/v1/document", null, cancellation);
        string json = value.GetProperty("document").GetRawText();
        return new(value.GetProperty("instanceId").GetString()!, value.GetProperty("documentRevision").GetInt64(), json, WorkspaceFiles.Hash(Encoding.UTF8.GetBytes(json)));
    }
    internal async Task<object> InspectAsync(CancellationToken cancellation)
    {
        var snapshot = await ReadAsync(cancellation);
        return new { path = "@active", revision = snapshot.Revision, instanceId = snapshot.InstanceId, documentRevision = snapshot.DocumentRevision, document = AutomationEngine.Inspect(snapshot.DocumentJson, cancellation) };
    }
    internal async Task<object> QueryTilesAsync(string roomId, string layer, int x, int y, int width, int height, CancellationToken cancellation)
    {
        var snapshot = await ReadAsync(cancellation);
        return new { path = "@active", revision = snapshot.Revision, query = AutomationEngine.QueryTiles(snapshot.DocumentJson, roomId, layer, x, y, width, height, cancellation) };
    }
    internal async Task<object> ValidateAsync(CancellationToken cancellation)
    {
        var snapshot = await ReadAsync(cancellation);
        var map = MapDocumentStore.Deserialize(snapshot.DocumentJson);
        return new { path = "@active", revision = snapshot.Revision, valid = true, map.formatVersion, rooms = map.rooms.Count };
    }
    internal async Task<object> PreviewAsync(CancellationToken cancellation)
    {
        var snapshot = await ReadAsync(cancellation);
        var map = MapDocumentStore.Deserialize(snapshot.DocumentJson);
        return new { mimeType = "image/svg+xml", svg = MiniMapSvg.Render(map), roomCount = map.rooms.Count, revision = snapshot.Revision };
    }
    internal async Task<object> EditAsync(string kind, string? source, JsonElement? batch, int seed, string? expectedRevision, bool dryRun, CancellationToken cancellation)
    {
        if (readOnly && !dryRun) throw new CliException("read_only", "This session is read-only. Use dryRun for a preview.");
        var snapshot = await ReadAsync(cancellation);
        if (expectedRevision != null && snapshot.Revision != expectedRevision) throw new CliException("conflict", "The live map changed. Inspect @active before retrying.");
        cancellation.ThrowIfCancellationRequested();
        string commandId = Guid.NewGuid().ToString("N");
        object jobRequest = new { kind, source, batch, seed, dryRun, clientId, commandId,
            expectedInstanceId = snapshot.InstanceId, expectedDocumentRevision = snapshot.DocumentRevision };
        JsonElement job;
        try { job = await RequestAsync(HttpMethod.Post, "api/v1/jobs", jobRequest, CancellationToken.None); }
        catch (CliException error) when (error.Code == "io_error")
        {
            // Recover the original job after an uncertain response; never submit a new command ID.
            try { job = await RequestAsync(HttpMethod.Post, "api/v1/jobs", jobRequest, CancellationToken.None); }
            catch (CliException retry) when (retry.Code == "io_error")
            { throw new CliException("uncertain_result", "Could not confirm the live job result. Inspect the live document before attempting another edit."); }
        }
        string id = job.GetProperty("id").GetString()!;
        if (id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new CliException("invalid_input", "Invalid live job identifier.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            while (job.GetProperty("phase").GetString() == "running")
            {
                await Task.Delay(100, deadline.Token);
                job = await RequestAsync(HttpMethod.Get, "api/v1/jobs/" + id, null, deadline.Token);
            }
            string? phase = job.GetProperty("phase").GetString();
            if (phase != "completed")
            {
                string code = phase switch { "conflict" => "conflict", "cancelled" => "cancelled", _ => "invalid_input" };
                throw new CliException(code, job.TryGetProperty("error", out var error) ? error.GetString() ?? "The live job failed." : "The live job failed.");
            }
            return new { path = "@active", previousRevision = snapshot.Revision, jobId = id, dryRun,
                operationCount = job.GetProperty("operationCount").GetInt32(), changed = job.GetProperty("changed").GetBoolean(),
                logs = job.GetProperty("logs").Clone(), live = true };
        }
        catch (Exception error) when (error is OperationCanceledException || error is CliException { Code: "io_error" })
        {
            try
            {
                using var cancelDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await RequestAsync(HttpMethod.Post, "api/v1/jobs/" + id + "/cancel", new { clientId }, cancelDeadline.Token);
            }
            catch (Exception) { }
            if (cancellation.IsCancellationRequested) throw;
            if (error is CliException) throw new CliException("uncertain_result", "The live connection was interrupted. Cancellation was requested; inspect the document before retrying.");
            throw new CliException("limit_exceeded", "The live job exceeded the client time limit; cancellation was requested.");
        }
    }
    private async Task<JsonElement> RequestAsync(HttpMethod method, string path, object? body, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body, Program.Json), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is >= 300 and < 400) throw new CliException("invalid_input", "The local server returned a redirect. Redirects are not followed.");
            string text = await BoundedText.ReadAsync(await response.Content.ReadAsStreamAsync(cancellation), Program.MaximumInputBytes, cancellation);
            if (!response.IsSuccessStatusCode)
            {
                string code = response.StatusCode == HttpStatusCode.Conflict ? "conflict" : "invalid_input";
                throw new CliException(code, "The local editor rejected the request (HTTP " + (int)response.StatusCode + "). Inspect the live document before retrying.");
            }
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (HttpRequestException) { throw new CliException("io_error", "Could not connect to the local web editor."); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new CliException("io_error", "The local web editor did not respond in time."); }
    }
}
