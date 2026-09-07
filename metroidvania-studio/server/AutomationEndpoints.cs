using System.Text.Json;
using MetroidvaniaStudio.Automation;

namespace MetroidvaniaStudio.Server;

public static class AutomationEndpoints
{
    public static void MapAutomation(this WebApplication app, EditorWorkspace workspace, string studioRoot)
    {
        var host = new AutomationWorkerHost(studioRoot);
        var jobs = new AutomationJobs(workspace, host.RunAsync);
        app.Lifetime.ApplicationStopping.Register(jobs.Dispose);
        app.MapGet("/api/v1/capabilities", () => Results.Json(new
        {
            apiVersion = 1, mapFormatVersion = 2, api = AutomationEngine.Describe(),
            execution = new { asynchronous = true, atomic = true, requiresDocumentRevision = true,
                timeoutSeconds = AutomationWorkerHost.TimeoutSeconds, memoryMiB = AutomationWorkerHost.MaximumProcessBytes / 1024 / 1024,
                maximumActiveJobs = AutomationJobs.MaximumActiveJobs, luaSourceBytes = 65536, httpBatchBytes = 8 * 1024 * 1024 }
        }));
        app.MapGet("/api/v1/document", () =>
        {
            lock (workspace.Gate) return Results.Json(new { apiVersion = 1, instanceId = workspace.InstanceId,
                documentRevision = workspace.DocumentRevision, document = new ValidatedJson(workspace.Session.CurrentJson) });
        });
        app.MapPost("/api/v1/jobs", async (HttpRequest request) =>
        {
            using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            var job = jobs.Start(body.RootElement);
            return Results.Accepted("/api/v1/jobs/" + job.Id, job);
        });
        app.MapGet("/api/v1/jobs/lookup", (string clientId, string commandId) =>
            jobs.Find(clientId, commandId) is { } job ? Results.Json(job) : Results.NotFound());
        app.MapGet("/api/v1/jobs/{id}", (string id) => Results.Json(jobs.Get(id)));
        app.MapPost("/api/v1/jobs/{id}/cancel", async (string id, HttpRequest request) =>
        {
            using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            if (body.RootElement.ValueKind != JsonValueKind.Object || !body.RootElement.TryGetProperty("clientId", out var client) || client.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Cancellation requires the requesting clientId.");
            return Results.Json(jobs.Cancel(id, client.GetString()!));
        });
    }
}
