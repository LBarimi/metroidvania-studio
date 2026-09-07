using MetroidvaniaStudio;

namespace MetroidvaniaStudio.Server;

public sealed partial class EditorWorkspace
{
    private MapDocument LoadSampleWorld()
    {
        string path = Files.SampleWorldPath ?? throw new InvalidOperationException("@sampleUnavailable");
        MapDocument sample = ProjectFiles.LoadStable(path).Document;
        // Respect a workspace's custom catalog instead of replacing its palettes.
        if (sample.rooms.SelectMany(room => room.foreground.Concat(room.background)).Any(cell => !Catalog.Materials.ContainsKey(cell.material))
            || sample.rooms.SelectMany(room => room.objects).Any(item => !Catalog.Objects.ContainsKey(item.definition)))
            throw new InvalidOperationException("@sampleCatalogMismatch");
        return sample;
    }
}
