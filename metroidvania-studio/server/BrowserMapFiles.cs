using System.Text.Json;
using MetroidvaniaStudio;

namespace MetroidvaniaStudio.Server;

public sealed partial class EditorWorkspace
{
    // A browser owns the OS file handle. The server receives only its display name
    // and a receipt for the exact revision written; filesystem access stays bounded.
    private sealed record BrowserFile(string Id, string Name, string SavedJson);
    private BrowserFile? browserFile;
    private bool HasUnsavedChanges => browserFile == null ? dirty : documentJson != browserFile.SavedJson;

    private static string BrowserFileName(JsonElement command)
    {
        string name = S(command, "fileName").Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Any(char.IsControl)
            || name.IndexOfAny(['/', '\\', ':']) >= 0 || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a JSON map file.");
        return name;
    }

    private void AcknowledgeBrowserSave(JsonElement command)
    {
        string name = BrowserFileName(command);
        string? previousId = command.TryGetProperty("browserFileId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        if (previousId != null && previousId != browserFile?.Id)
            throw new WorkspaceConflict("The open map changed while the file was being saved.");
        string receiptId = previousId ?? Guid.NewGuid().ToString("N");
        // Keep map Undo/Redo, and retain a recovery copy even after an external save.
        if (Session.FilePath != null) Session.DetachFile();
        browserFile = new BrowserFile(receiptId, name, documentJson);
        QueueRecovery();
    }
}
