using Dapper;
using Javideo.Worker.Db;
using Javideo.Worker.Services;

namespace Javideo.Worker.Endpoints;

public static class MetaTubeEndpoints
{
    public static void MapMetaTubeEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/metatube").WithTags("MetaTube");

        // Scrape metadata by 番号.
        // Returns 412 + needsConfig=true when no server is configured.
        // Also reports whether this number is already ingested into a library,
        // so the frontend can show an "已入库" badge.
        g.MapGet("/movie/{number}", async (string number, MetaTubeClient mt, DbConnectionFactory db) =>
        {
            try
            {
                var movie = await mt.GetMovieAsync(number);
                if (movie is null)
                    return Results.NotFound(new { ok = false, needsConfig = false, detail = $"MetaTube 未找到番号 {number}" });

                // Check ingest status across all libraries.
                await using var c = db.Create();
                await c.OpenAsync();
                var ingested = await c.QueryFirstOrDefaultAsync<(long Id, long? LibraryId, string? FolderPath)>(@"
                    SELECT id Id, library_id LibraryId, folder_path FolderPath
                    FROM movies WHERE number=@n ORDER BY id LIMIT 1", new { n = movie.Number });
                if (ingested.Id > 0)
                {
                    movie.Id = ingested.Id;
                    movie.LibraryId = ingested.LibraryId;
                    movie.FolderPath = ingested.FolderPath;
                }
                return Results.Ok(movie);
            }
            catch (MetaTubeNotConfiguredException)
            {
                return Results.Json(
                    new { ok = false, needsConfig = true, detail = "未配置 MetaTube 服务地址,请到「设置 → MetaTube」填写" },
                    statusCode: StatusCodes.Status412PreconditionFailed);
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { ok = false, needsConfig = false, detail = $"刮削失败: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Find a trailer URL for a 番号 (DMM free preview via TrailerClient).
        // Used by the search page to show a trailer without ingesting first.
        g.MapGet("/trailer/{number}", async (string number, TrailerClient tc, SettingsService settings) =>
        {
            var on = (await settings.GetAsync(SettingsService.KeyScrapeTrailer))?.Trim();
            if (!string.Equals(on, "true", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(new { ok = false, url = (string?)null });

            // Clean up old temp trailers from previous searches.
            TrailerClient.CleanupTemp();

            try
            {
                var url = await tc.FindTrailerUrlAsync(number);
                if (url == null) return Results.Ok(new { ok = false, url = (string?)null });

                // Download to temp dir (only once — ingest will move it, not re-download).
                var bytes = await tc.DownloadAsync(url);
                if (bytes == null || bytes.Length == 0) return Results.Ok(new { ok = false, url = (string?)null });

                Directory.CreateDirectory(Path.GetDirectoryName(TrailerClient.TempPathFor(number))!);
                await File.WriteAllBytesAsync(TrailerClient.TempPathFor(number), bytes);

                // Return the temp-playback URL (served by the endpoint below).
                return Results.Ok(new { ok = true, url = $"/api/metatube/trailer-temp/{number}" });
            }
            catch
            {
                return Results.Ok(new { ok = false, url = (string?)null });
            }
        });

        // Serve temp trailer for search-time playback (streamed from disk).
        g.MapGet("/trailer-temp/{number}", (string number) =>
        {
            var path = TrailerClient.TempPathFor(number);
            return File.Exists(path) ? Results.File(path, "video/mp4", enableRangeProcessing: true) : Results.NotFound();
        });

        // Search candidates — returns ALL matches so the user can pick the right
        // one when there are multiple results for a 番号.
        g.MapGet("/candidates/{number}", async (string number, MetaTubeClient mt) =>
        {
            try
            {
                var candidates = await mt.SearchCandidatesAsync(number);
                return Results.Ok(candidates);
            }
            catch (MetaTubeNotConfiguredException)
            {
                return Results.Json(
                    new { ok = false, needsConfig = true, detail = "未配置 MetaTube 服务地址" },
                    statusCode: StatusCodes.Status412PreconditionFailed);
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { ok = false, detail = $"搜索失败: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Scrape a specific provider/id (used after the user picks a candidate).
        g.MapGet("/movie/{number}/{provider}/{id}", async (string number, string provider, string id, MetaTubeClient mt) =>
        {
            try
            {
                var movie = await mt.GetMovieByProviderAsync(provider, id, number);
                if (movie is null)
                    return Results.NotFound(new { ok = false, detail = "MetaTube 未找到该番号" });
                return Results.Ok(movie);
            }
            catch (MetaTubeNotConfiguredException)
            {
                return Results.Json(
                    new { ok = false, needsConfig = true, detail = "未配置 MetaTube 服务地址" },
                    statusCode: StatusCodes.Status412PreconditionFailed);
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { ok = false, detail = $"刮削失败: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Connectivity test.
        g.MapGet("/test", async (MetaTubeClient mt) =>
        {
            var (ok, detail) = await mt.TestConnectionAsync();
            return Results.Ok(new { ok, detail });
        });
    }
}
