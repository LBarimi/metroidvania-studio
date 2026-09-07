using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetroidvaniaStudio.Server;

public sealed record AutomationExecution(string DocumentJson, string[] Logs, int OperationCount, bool Changed);
public sealed record AutomationInput(string Kind, string DocumentJson, string? Source, JsonElement? Batch, int Seed);
public sealed record AutomationJobStatus(string Id, string Phase, bool DryRun, int OperationCount, bool Changed,
    string[] Logs, string? Error, string? ErrorCode, long BaseDocumentRevision);

/// <summary>Runs bounded jobs outside the editor gate and atomically publishes a current result.</summary>
public sealed class AutomationJobs : IDisposable
{
    public const int MaximumActiveJobs = 2;
    public const int MaximumRetainedJobs = 32;
    private readonly object gate = new();
    private readonly EditorWorkspace workspace;
    private readonly Func<AutomationInput, CancellationToken, Task<AutomationExecution>> execute;
    private readonly Dictionary<string, Job> jobs = new();
    private bool disposed;
    private sealed class Job
    {
        public required string Id, ClientId, CommandId, Fingerprint;
        public required AutomationJobStatus Status;
        public readonly CancellationTokenSource Cancellation = new();
        public bool Active = true;
    }

    public AutomationJobs(EditorWorkspace workspace, Func<AutomationInput, CancellationToken, Task<AutomationExecution>> execute)
    { this.workspace = workspace; this.execute = execute; }

    public AutomationJobStatus Start(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected an automation job object.");
        string[] fields = ["kind", "clientId", "commandId", "expectedInstanceId", "expectedDocumentRevision", "source", "batch", "seed", "dryRun"];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in request.EnumerateObject())
            if (!fields.Contains(field.Name) || !seen.Add(field.Name))
                throw new ArgumentException("Unknown or repeated automation field: " + field.Name);
        string Required(string key, int maximum = 128)
        {
            if (!request.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > maximum)
                throw new ArgumentException("Invalid automation field: " + key);
            return value.GetString()!;
        }
        string client = Required("clientId"), command = Required("commandId"), instance = Required("expectedInstanceId");
        string kind = Required("kind");
        if (kind is not ("lua" or "batch")) throw new ArgumentException("Automation kind must be lua or batch.");
        if (!request.TryGetProperty("expectedDocumentRevision", out var revisionValue) || revisionValue.ValueKind != JsonValueKind.Number || !revisionValue.TryGetInt64(out long revision))
            throw new ArgumentException("expectedDocumentRevision must be an integer.");
        string? source = kind == "lua" ? Required("source", 65536) : null;
        if (source != null && Encoding.UTF8.GetByteCount(source) > 65536) throw new ArgumentException("Lua source exceeds 64 KiB.");
        JsonElement? batch = null;
        if (kind == "batch")
        {
            if (!request.TryGetProperty("batch", out var value) || value.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("A batch job requires an API request object.");
            if (Encoding.UTF8.GetByteCount(value.GetRawText()) > 8 * 1024 * 1024) throw new ArgumentException("HTTP automation batch exceeds 8 MiB.");
            batch = value.Clone();
        }
        int seed = 1;
        if (request.TryGetProperty("seed", out var seedValue) && (seedValue.ValueKind != JsonValueKind.Number || !seedValue.TryGetInt32(out seed))) throw new ArgumentException("seed must be an integer.");
        bool dryRun = false;
        if (request.TryGetProperty("dryRun", out var dryValue))
        {
            if (dryValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("dryRun must be a boolean.");
            dryRun = dryValue.GetBoolean();
        }
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.GetRawText())));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var previous = jobs.Values.FirstOrDefault(job => job.ClientId == client && job.CommandId == command);
            if (previous != null)
            {
                if (previous.Fingerprint != fingerprint) throw new WorkspaceConflict("commandId already identifies a different automation request.");
                return previous.Status;
            }
            if (jobs.Values.Count(job => job.Active) >= MaximumActiveJobs) throw new WorkspaceConflict("The automation worker queue is full.");
            var snapshot = workspace.CaptureAutomation(instance, revision);
            while (jobs.Count >= MaximumRetainedJobs)
            {
                var oldest = jobs.Values.First(job => !job.Active);
                jobs.Remove(oldest.Id); oldest.Cancellation.Dispose();
            }
            string id = Guid.NewGuid().ToString("N");
            var job = new Job { Id = id, ClientId = client, CommandId = command, Fingerprint = fingerprint,
                Status = new(id, "running", dryRun, 0, false, [], null, null, revision) };
            jobs.Add(id, job);
            _ = Task.Run(() => Run(job, snapshot, new(kind, snapshot.DocumentJson, source, batch, seed)));
            return job.Status;
        }
    }

    public AutomationJobStatus Get(string id)
    { lock (gate) return jobs.TryGetValue(id, out var job) ? job.Status : throw new KeyNotFoundException("Automation job not found."); }

    public AutomationJobStatus? Find(string clientId, string commandId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 128 || string.IsNullOrWhiteSpace(commandId) || commandId.Length > 128)
            throw new ArgumentException("Invalid automation request identity.");
        lock (gate) return jobs.Values.FirstOrDefault(job => job.ClientId == clientId && job.CommandId == commandId)?.Status;
    }

    public AutomationJobStatus Cancel(string id, string clientId)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job)) throw new KeyNotFoundException("Automation job not found.");
            if (job.ClientId != clientId) throw new WorkspaceConflict("Only the requesting client can cancel this job.");
            if (job.Active) job.Cancellation.Cancel();
            return job.Status;
        }
    }

    private async Task Run(Job job, AutomationSnapshot snapshot, AutomationInput input)
    {
        try
        {
            var result = await execute(input, job.Cancellation.Token);
            // Validate worker output outside both locks, including dry runs.
            var prepared = MapEditSession.PrepareSnapshot(result.DocumentJson);
            lock (gate)
            {
                job.Cancellation.Token.ThrowIfCancellationRequested();
                bool changed = prepared.Json != snapshot.DocumentJson;
                if (!job.Status.DryRun) changed = workspace.ApplyAutomation(snapshot, prepared, job.Cancellation.Token);
                job.Status = job.Status with { Phase = "completed", OperationCount = result.OperationCount, Changed = changed, Logs = result.Logs };
                job.Active = false;
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                bool cancelled = job.Cancellation.IsCancellationRequested || error is OperationCanceledException;
                string message = cancelled ? "Automation cancelled. No result was applied." : error.Message;
                job.Status = job.Status with { Phase = cancelled ? "cancelled" : error is WorkspaceConflict ? "conflict" : "failed",
                    Error = message.Length <= 4096 ? message : message[..4096], ErrorCode = cancelled ? "cancelled" : error is WorkspaceConflict ? "conflict" : "execution_failed" };
                job.Active = false;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            foreach (var job in jobs.Values) if (job.Active) job.Cancellation.Cancel();
        }
    }
}
