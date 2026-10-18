using Javideo.Worker.Db;
using Dapper;

namespace Javideo.Worker.Services;

/// <summary>One shared limit for reads from cloud-drive libraries.</summary>
public sealed class CloudIoService(DbConnectionFactory db) : IDisposable
{
    // ponytail: adjust this capacity if the mounted drive's limit changes.
    private readonly SemaphoreSlim _gate = new(2, 2);

    public async Task<T> RunAsync<T>(long libraryId, Func<Task<T>> action, CancellationToken ct = default, bool force = false)
    {
        if (!force && !await db.IsCacheLocalAsync(libraryId)) return await action();
        await _gate.WaitAsync(ct);
        try { return await action(); }
        finally { _gate.Release(); }
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path.Value ?? "";
        var values = context.Request.RouteValues;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var metadataOnly = HttpMethods.IsGet(context.Request.Method)
            && ((segments.Length == 3 && long.TryParse(segments[2], out _)) || values.ContainsKey("number"));
        long libraryId = 0;
        // Preview images and highlight attachments are already local. Only the
        // highlight video picker needs to enumerate the original media folder.
        if (path.StartsWith("/api/movies/", StringComparison.OrdinalIgnoreCase)
            && !metadataOnly
            && !path.Contains("/preview/", StringComparison.OrdinalIgnoreCase)
            && (!path.Contains("/highlights", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/video-files", StringComparison.OrdinalIgnoreCase)))
        {
            await using var c = db.Create();
            await c.OpenAsync(context.RequestAborted);
            var id = values.GetValueOrDefault("id") ?? values.GetValueOrDefault("movieId");
            if (long.TryParse(id?.ToString(), out var movieId))
            {
                libraryId = await c.ExecuteScalarAsync<long>("SELECT library_id FROM movies WHERE id=@movieId", new { movieId });
                if (path.Contains("/image/", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("/trailer", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/trailer-status", StringComparison.OrdinalIgnoreCase))
                {
                    var folder = await c.ExecuteScalarAsync<string?>("SELECT folder_path FROM movies WHERE id=@movieId", new { movieId });
                    var localRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(db.DataDir)) + Path.DirectorySeparatorChar;
                    if (!string.IsNullOrWhiteSpace(folder) && Path.GetFullPath(folder).StartsWith(localRoot, StringComparison.OrdinalIgnoreCase))
                        libraryId = 0;
                }
            }
        }
        else if (path.StartsWith("/api/libraries/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/scan", StringComparison.OrdinalIgnoreCase))
            long.TryParse(values.GetValueOrDefault("id")?.ToString(), out libraryId);

        // The slot covers response execution too, so streamed trailers and
        // range responses cannot release it while still reading the mount.
        await RunAsync(libraryId, async () => { await next(context); return true; }, context.RequestAborted);
    }

    public void Dispose() => _gate.Dispose();
}
