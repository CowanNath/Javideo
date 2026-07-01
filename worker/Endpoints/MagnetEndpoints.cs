using Javideo.Worker.Magnet;

namespace Javideo.Worker.Endpoints;

public static class MagnetEndpoints
{
    public static void MapMagnetEndpoints(this WebApplication app)
    {
        // Flat (de-duplicated) list across all sources.
        app.MapGet("/api/magnet/search", async (string q, MagnetService svc, CancellationToken ct) =>
            Results.Ok(await svc.SearchAsync(q, ct)))
           .WithTags("Magnet");

        // Grouped by source — one entry per source with its count + results,
        // so the frontend can render a tab per source (incl. empty ones).
        app.MapGet("/api/magnet/search-grouped", async (string q, MagnetService svc, CancellationToken ct) =>
            Results.Ok(await svc.SearchGroupedAsync(q, ct)))
           .WithTags("Magnet");
    }
}
