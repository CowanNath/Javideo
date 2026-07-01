using Javideo.Worker.Models;
using Javideo.Worker.Services;

namespace Javideo.Worker.Endpoints;

public static class LibraryEndpoints
{
    public static void MapLibraryEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/libraries").WithTags("Libraries");

        g.MapGet("/", async (LibraryService svc) => Results.Ok(await svc.ListAsync()));

        g.MapGet("/{id:long}", async (long id, LibraryService svc) =>
            await svc.GetAsync(id) is { } lib ? Results.Ok(lib) : Results.NotFound());

        g.MapPost("/", async (Library lib, LibraryService svc) =>
        {
            var id = await svc.CreateAsync(lib);
            return Results.Created($"/api/libraries/{id}", await svc.GetAsync(id));
        });

        g.MapPut("/{id:long}", async (long id, Library lib, LibraryService svc) =>
        {
            await svc.UpdateAsync(id, lib);
            return Results.Ok(await svc.GetAsync(id));
        });

        g.MapDelete("/{id:long}", async (long id, LibraryService svc) =>
        {
            await svc.DeleteAsync(id);
            return Results.NoContent();
        });

        // Check whether a directory exists on disk (for real-time validation in
        // the library dialog). Path comes in the ?path= query.
        g.MapGet("/check-dir", (string? path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.Ok(new { exists = false });
            return Results.Ok(new { exists = Directory.Exists(path.Trim()) });
        });

        // Check whether a library name is already taken (optionally excluding
        // the library being edited).
        g.MapGet("/check-name", async (string? name, long? exclude, LibraryService svc) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Results.Ok(new { taken = false });
            var libs = await svc.ListAsync();
            var taken = libs.Any(l => l.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)
                                   && l.Id != exclude);
            return Results.Ok(new { taken });
        });
    }
}
