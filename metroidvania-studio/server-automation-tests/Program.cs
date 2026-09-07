using System.Text.Json;
using MetroidvaniaStudio;
using MetroidvaniaStudio.Server;

int passed = 0;
async Task Check(string name, Func<Task> test)
{
    await test(); passed++; Console.WriteLine("PASS " + name);
}
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
JsonElement Request(EditorWorkspace w, bool dry = false, string command = "command", string client = "client") =>
    JsonSerializer.SerializeToElement(new { kind = "lua", source = "print(1)", seed = 1, dryRun = dry,
        clientId = client, commandId = command, expectedInstanceId = w.InstanceId, expectedDocumentRevision = w.DocumentRevision });
AutomationExecution Result(string json)
{
    var document = MapDocumentStore.Deserialize(json); document.name = "automated";
    return new(MapDocumentStore.Serialize(document), ["done"], 1, true);
}
async Task<AutomationJobStatus> Completed(AutomationJobs jobs, string id)
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (jobs.Get(id).Phase == "running") await Task.Delay(10, limit.Token);
    return jobs.Get(id);
}
async Task Fixture(Func<EditorWorkspace, Task> action)
{
    string parent = Path.GetFullPath("../../../.test-output", AppContext.BaseDirectory);
    string target = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(target);
    var workspace = new EditorWorkspace(new ProjectFiles(target));
    try { await action(workspace); }
    finally
    {
        workspace.Cancel(); workspace.FlushRecovery(); workspace.StopAutoExports(); workspace.Canvas.Dispose();
        if (!Path.GetFullPath(target).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new Exception("Unsafe test cleanup.");
        Directory.Delete(target, recursive: true);
    }
}
await Check("job applies once, preserves file identity and supports one Undo/Redo", () => Fixture(async w =>
{
    string before = w.Session.CurrentJson; string? file = w.Session.FilePath;
    using var jobs = new AutomationJobs(w, (input, token) => Task.FromResult(Result(input.DocumentJson)));
    long serializations = w.Session.SnapshotSerializationCount;
    var request = Request(w); var job = jobs.Start(request);
    var completed = await Completed(jobs, job.Id);
    Require(completed.Phase == "completed" && completed.Changed && completed.OperationCount == 1, completed.Error ?? "No result");
    Require(w.Session.SnapshotSerializationCount == serializations, "Automation commit serialized the live document under its editing lock.");
    Require(jobs.Find("client", "command")?.Id == job.Id, "Lost submission could not be recovered.");
    Require(jobs.Find("client", "missing") == null, "Missing submission unexpectedly created a job.");
    string after = w.Session.CurrentJson;
    Require(w.Session.Document.name == "automated" && w.Session.FilePath == file && w.Session.CanUndo, "Content or identity incorrect.");
    Require(jobs.Start(request).Id == job.Id, "A retry executed twice.");
    w.Session.Undo(); Require(w.Session.CurrentJson == before && !w.Session.CanUndo, "Not one undo unit.");
    w.Session.Redo(); Require(w.Session.CurrentJson == after, "Redo did not restore the batch.");
}));
await Check("dry run leaves map, history and document revision unchanged", () => Fixture(async w =>
{
    string before = w.Session.CurrentJson; long revision = w.DocumentRevision;
    using var jobs = new AutomationJobs(w, (input, token) => Task.FromResult(Result(input.DocumentJson)));
    var result = await Completed(jobs, jobs.Start(Request(w, true)).Id);
    Require(result.Phase == "completed" && result.DryRun && result.Changed, "Dry-run result missing.");
    Require(w.Session.CurrentJson == before && w.DocumentRevision == revision && !w.Session.CanUndo, "Dry run mutated the map.");
}));
await Check("worker runs outside editor gate and stale results preserve intervening edits", () => Fixture(async w =>
{
    var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
    using var jobs = new AutomationJobs(w, async (input, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return Result(input.DocumentJson); });
    var job = jobs.Start(Request(w)); await entered.Task;
    await Task.Run(() => { lock (w.Gate) w.Session.Execute("Manual edit", document => document.name = "manual"); }).WaitAsync(TimeSpan.FromSeconds(1));
    string manual = w.Session.CurrentJson; release.SetResult(); var result = await Completed(jobs, job.Id);
    Require(result.Phase == "conflict" && w.Session.CurrentJson == manual, "Stale result overwrote a manual edit.");
}));
await Check("cancel is owner-scoped and never publishes a partial result", () => Fixture(async w =>
{
    string before = w.Session.CurrentJson;
    using var jobs = new AutomationJobs(w, async (input, token) => { await Task.Delay(5000, token); return Result(input.DocumentJson); });
    var job = jobs.Start(Request(w));
    try { jobs.Cancel(job.Id, "other"); throw new Exception("Foreign cancellation accepted."); } catch (WorkspaceConflict) { }
    jobs.Cancel(job.Id, "client"); var result = await Completed(jobs, job.Id);
    Require(result.Phase == "cancelled" && w.Session.CurrentJson == before && !w.Session.CanUndo, "Cancelled job mutated map.");
}));
await Check("invalid worker output and queue overflow preserve editor state", () => Fixture(async w =>
{
    string before = w.Session.CurrentJson;
    using var bad = new AutomationJobs(w, (input, token) => Task.FromResult(new AutomationExecution("{}", [], 1, true)));
    // Empty fields can use model defaults; a future schema version is always invalid.
    using var invalid = new AutomationJobs(w, (input, token) => Task.FromResult(new AutomationExecution("{\"formatVersion\":999}", [], 1, true)));
    var result = await Completed(invalid, invalid.Start(Request(w)).Id);
    Require(result.Phase == "failed" && w.Session.CurrentJson == before && !w.Session.CanUndo, "Invalid worker output mutated map.");
    using var busy = new AutomationJobs(w, async (input, token) => { await Task.Delay(5000, token); return Result(input.DocumentJson); });
    var first = busy.Start(Request(w, command: "one")); var second = busy.Start(Request(w, command: "two"));
    try { busy.Start(Request(w, command: "three")); throw new Exception("Queue limit ignored."); } catch (WorkspaceConflict) { }
    busy.Cancel(first.Id, "client"); busy.Cancel(second.Id, "client");
    await Completed(busy, first.Id); await Completed(busy, second.Id);
}));
await Check("active gesture and changed document epoch block automation", () => Fixture(async w =>
{
    var snapshot = w.CaptureAutomation(w.InstanceId, w.DocumentRevision);
    w.Session.BeginEdit("Gesture");
    try { w.CaptureAutomation(w.InstanceId, w.DocumentRevision); throw new Exception("Active gesture accepted."); } catch (WorkspaceConflict) { }
    w.Session.CancelEdit();
    w.Session.New(MapDocumentStore.Deserialize(snapshot.DocumentJson));
    try { w.ApplyAutomation(snapshot, Result(snapshot.DocumentJson).DocumentJson); throw new Exception("Changed document epoch accepted."); } catch (WorkspaceConflict) { }
    await Task.CompletedTask;
}));
await Check("misspelled dry run and duplicate job fields never execute", () => Fixture(async w =>
{
    bool executed = false;
    using var jobs = new AutomationJobs(w, (input, token) => { executed = true; return Task.FromResult(Result(input.DocumentJson)); });
    foreach (string json in new[] { Request(w).GetRawText().Replace("dryRun", "dryrun"), Request(w).GetRawText().Replace("{", "{\"dryRun\":true,", StringComparison.Ordinal) })
    {
        using var parsed = JsonDocument.Parse(json);
        try { jobs.Start(parsed.RootElement); throw new Exception("Invalid envelope accepted."); } catch (ArgumentException) { }
    }
    Require(!executed && !w.Session.CanUndo, "Malformed dry run ran a worker.");
    await Task.CompletedTask;
}));
await Check("prepared snapshot is single-use and no-op preserves history", () => Fixture(async w =>
{
    string before = w.Session.CurrentJson;
    var same = MapEditSession.PrepareSnapshot(before);
    w.Session.ApplySnapshot("No change", same);
    Require(!w.Session.CanUndo, "No-op created history.");
    try { w.Session.ApplySnapshot("Reuse", same); throw new Exception("Prepared snapshot reused."); } catch (InvalidOperationException) { }
    Require(w.Session.CurrentJson == before, "Reuse changed map.");
    await Task.CompletedTask;
}));
Console.WriteLine($"Server automation: {passed}/{passed} passed.");
