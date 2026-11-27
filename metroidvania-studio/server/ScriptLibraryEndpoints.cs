using System.Text.Json;

namespace MetroidvaniaStudio.Server;

public static class ScriptLibraryEndpoints
{
    public static void MapScriptLibrary(this WebApplication app, ScriptLibrary library)
    {
        app.MapGet("/api/v1/scripts", () => Results.Json(library.List()));
        app.MapGet("/api/v1/scripts/source", (string id) => Results.Json(library.Read(id)));
        app.MapPost("/api/v1/scripts", async (HttpRequest request) =>
        {
            using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            var value = body.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("A script object is required.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
                if (field.Name is not ("name" or "source" or "expectedRevision") || !seen.Add(field.Name)) throw new ArgumentException("Unknown or duplicate script field.");
            if (seen.Count != 3 || value.GetProperty("name").ValueKind != JsonValueKind.String || value.GetProperty("source").ValueKind != JsonValueKind.String
                || value.GetProperty("expectedRevision").ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new ArgumentException("Provide name, source, and expectedRevision.");
            return Results.Json(library.Save(value.GetProperty("name").GetString()!, value.GetProperty("source").GetString()!, value.GetProperty("expectedRevision").GetString()));
        });
    }
}
