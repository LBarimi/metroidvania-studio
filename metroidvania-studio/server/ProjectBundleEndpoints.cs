namespace MetroidvaniaStudio.Server;

public static class ProjectBundleEndpoints
{
    public sealed record Request(string InstanceId, long DocumentRevision, long CatalogRevision);

    public static void MapProjectBundles(this WebApplication app, EditorWorkspace workspace, ProjectFiles files)
    {
        var gate = new SemaphoreSlim(1, 1);
        app.MapPost("/api/project-bundle", async (Request request, HttpContext context) =>
        {
            if (!await gate.WaitAsync(0, context.RequestAborted)) throw new WorkspaceConflict("@bundleBusy");
            FileStream? output = null;
            try
            {
                var snapshot = workspace.CaptureProjectBundle(request.InstanceId, request.DocumentRevision, request.CatalogRevision);
                output = await Task.Run(() =>
                {
                    var stream = new FileStream(Path.Combine(Path.GetTempPath(), "studio-bundle-" + Guid.NewGuid().ToString("N") + ".zip"),
                        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
                    try { ProjectBundleExporter.Write(snapshot, files, stream, context.RequestAborted); stream.Position = 0; return stream; }
                    catch { stream.Dispose(); throw; }
                }, context.RequestAborted);
                lock (workspace.Gate)
                    if (snapshot.CatalogRevision != workspace.CatalogRevision) throw new WorkspaceConflict("@bundleChanged");
                context.RequestAborted.ThrowIfCancellationRequested();
                context.Response.RegisterForDispose(output);
                return Results.File(output, "application/zip", "project.zip");
            }
            catch { output?.Dispose(); throw; }
            finally { gate.Release(); }
        });
    }
}
