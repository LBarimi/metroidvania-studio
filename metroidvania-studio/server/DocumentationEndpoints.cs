namespace MetroidvaniaStudio.Server;

public static class DocumentationEndpoints
{
    public static void MapDocumentation(this WebApplication app, string webRoot)
    {
        // Routing treats /docs and /docs/ as the same path. Redirect to a file to avoid a self-redirect.
        app.MapGet("/docs", () => Results.Redirect("/docs/index.html"));
        app.MapGet("/docs/{**page}", (string? page) =>
        {
            page = string.IsNullOrEmpty(page) ? "index.html" : page;
            if (page.Length > 160 || page.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) || page.Contains("..")) return Results.NotFound();
            string folder = Path.Combine(webRoot, "docs"), file = Path.Combine(folder, page);
            if (!File.Exists(file) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return Results.NotFound();
            string? type = Path.GetExtension(page) switch
            {
                ".html" => "text/html; charset=utf-8", ".css" => "text/css; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
                ".svg" => "image/svg+xml", ".json" => "application/json; charset=utf-8", ".lua" => "text/plain; charset=utf-8", _ => null
            };
            return type == null ? Results.NotFound() : Results.File(file, type);
        });
    }
}
