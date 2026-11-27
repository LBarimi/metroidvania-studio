using MetroidvaniaStudio.Storage;

namespace MetroidvaniaStudio.Server;

public static class EditorRuntime
{
    public static byte[] PaletteTemplate(string mode, string color)
    {
        if (color.Length != 7 || color[0] != '#' || !color[1..].All(char.IsAsciiHexDigit)) throw new ArgumentException("@paletteInvalidColor");
        return TilesetComposer.Template(mode, color);
    }

    // The caller owns the project writer lock for the lifetime of the editor.
    public static EditorWorkspace Open(string project, string studioRoot)
    {
        PortableWorkspace.SeedResources(project, studioRoot);
        var files = new ProjectFiles(project, studioRoot: studioRoot);
        TextureLibrary.Prepare(files, studioRoot);
        BuiltInPalettes.Prepare(files, studioRoot);
        BuiltInObjects.Prepare(files, studioRoot);
        BundledTilesets.Prepare(files, studioRoot);
        return new EditorWorkspace(files);
    }
}
