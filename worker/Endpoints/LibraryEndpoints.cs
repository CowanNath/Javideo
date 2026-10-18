using Javideo.Worker.Models;
using Javideo.Worker.Services;

namespace Javideo.Worker.Endpoints;

public static class LibraryEndpoints
{
    public static void MapLibraryEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/libraries").WithTags("Libraries");

        g.MapGet("/", async (LibraryService svc, LibraryCacheService cache) =>
        {
            var libraries = await svc.ListAsync();
            var statuses = await cache.StatusesAsync();
            foreach (var lib in libraries) lib.CacheStatus = statuses.GetValueOrDefault(lib.Id);
            return Results.Ok(libraries);
        });

        g.MapGet("/{id:long}", async (long id, LibraryService svc, LibraryCacheService cache) =>
        {
            var lib = await svc.GetAsync(id);
            if (lib == null) return Results.NotFound();
            lib.CacheStatus = (await cache.StatusesAsync()).GetValueOrDefault(id);
            return Results.Ok(lib);
        });

        g.MapPost("/", async (Library lib, LibraryService svc, LibraryCacheService cache) =>
        {
            var id = await svc.CreateAsync(lib);
            var saved = (await svc.GetAsync(id))!;
            saved.CacheStatus = await cache.EnqueueAsync(id);
            return Results.Created($"/api/libraries/{id}", saved);
        });

        g.MapPut("/{id:long}", async (long id, Library lib, LibraryService svc, LibraryCacheService cache) =>
        {
            await svc.UpdateAsync(id, lib);
            var saved = await svc.GetAsync(id);
            if (saved == null) return Results.NotFound();
            saved.CacheStatus = await cache.EnqueueAsync(id);
            return Results.Ok(saved);
        });

        g.MapDelete("/{id:long}", async (long id, LibraryService svc, LibraryCacheService cache) =>
        {
            await cache.CancelAsync(id);
            await svc.DeleteAsync(id);
            return Results.NoContent();
        });

        g.MapPost("/{id:long}/cache", async (long id, LibraryCacheService cache) =>
            await cache.EnqueueAsync(id) is { } status ? Results.Accepted(value: status)
                : Results.BadRequest(new { detail = "请先开启元数据本地缓存。" }));

        // Check whether a directory exists on disk (for real-time validation in
        // the library dialog). Path comes in the ?path= query.
        g.MapGet("/check-dir", async (string? path, CloudIoService cloud, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.Ok(new { exists = false });
            return Results.Ok(new { exists = await cloud.RunAsync(0, () => Task.Run(() => Directory.Exists(path.Trim())), ct, force: true) });
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
