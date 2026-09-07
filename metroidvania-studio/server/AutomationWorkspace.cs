namespace MetroidvaniaStudio.Server;

public sealed record AutomationSnapshot(string DocumentJson, long DocumentRevision, int DocumentEpoch, string InstanceId, string? FilePath);

public sealed partial class EditorWorkspace
{
    public AutomationSnapshot CaptureAutomation(string instanceId, long documentRevision)
    {
        lock (Gate)
        {
            RequireAutomationReady();
            if (instanceId != InstanceId || documentRevision != DocumentRevision)
                throw new WorkspaceConflict("The map changed. Read its current document before running automation.");
            return new(Session.CurrentJson, DocumentRevision, Session.DocumentEpoch, InstanceId, Session.FilePath);
        }
    }

    public bool ApplyAutomation(AutomationSnapshot snapshot, string json, CancellationToken cancellationToken = default)
        => ApplyAutomation(snapshot, MapEditSession.PrepareSnapshot(json), cancellationToken);

    public bool ApplyAutomation(AutomationSnapshot snapshot, MapEditSession.PreparedSnapshot prepared, CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireAutomationReady();
            if (snapshot.InstanceId != InstanceId || snapshot.DocumentRevision != DocumentRevision
                || snapshot.DocumentEpoch != Session.DocumentEpoch || snapshot.FilePath != Session.FilePath)
                throw new WorkspaceConflict("The map changed while automation was running. Its result was not applied.");
            bool changed = prepared.Json != Session.CurrentJson;
            if (changed)
            {
                Session.ApplySnapshot("Automation", prepared);
                Canvas.Deselect();
            }
            return changed;
        }
    }

    private void RequireAutomationReady()
    {
        if (shuttingDown) throw new InvalidOperationException("The studio is saving and shutting down.");
        if (Session.IsEditing || gestureOwner != null)
            throw new WorkspaceConflict("Finish the current gesture before applying automation.");
    }
}
