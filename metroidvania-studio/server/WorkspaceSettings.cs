using System.Text.Json;

namespace MetroidvaniaStudio.Server;

/// <summary>Optional workspace-local settings. All paths stay relative to that workspace.</summary>
public sealed class WorkspaceSettings
{
    public string? MapsRoot { get; set; }
    public string? CatalogPath { get; set; }
    public string? TexturesRoot { get; set; }

    public static WorkspaceSettings Load(string workspace)
    {
        string directory = Path.Combine(workspace, ".studio"), path = Path.Combine(directory, "workspace.json");
        if (!File.Exists(path)) return new WorkspaceSettings();
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Linked workspace settings are not supported.");
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Workspace settings exceed 64 KiB.");
        WorkspaceSettings settings = JsonSerializer.Deserialize<WorkspaceSettings>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Workspace settings must be an object.");
        return settings;
    }
}
