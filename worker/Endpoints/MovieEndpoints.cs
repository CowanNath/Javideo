using Dapper;
using Javideo.Worker.Db;
using Javideo.Worker.Magnet;
using Javideo.Worker.Models;
using Javideo.Worker.Services;

namespace Javideo.Worker.Endpoints;

public static class MovieEndpoints
{
    public static void MapMovieEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/movies").WithTags("Movies");

        // Movies in a library (with actors/tags/thumbnails).
        g.MapGet("/library/{libraryId:long}", async (long libraryId, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var movies = (await c.QueryAsync<Movie>(@"
                SELECT id Id, library_id LibraryId, number Number, title Title, summary Summary,
                       maker Maker, label Label, series Series, director Director,
                       release_date ReleaseDate, runtime_minutes RuntimeMinutes,
                       cover_url CoverUrl, thumb_url ThumbUrl, score Score, folder_path FolderPath
                FROM movies WHERE library_id=@id ORDER BY id DESC", new { id = libraryId })).ToList();
            // Override cover/thumb to local file paths when they exist, so the
            // webview doesn't hit external dmm URLs (which get blocked by
            // tracking prevention).
            foreach (var m in movies)
            {
                // Always route through the local image endpoint — it serves from
                // disk if available, or proxies the remote URL otherwise. This
                // prevents the webview from hitting dmm directly (Tracking Prevention).
                if (!string.IsNullOrWhiteSpace(m.CoverUrl))
                    m.CoverUrl = $"/api/movies/{m.Id}/image/poster";
                if (!string.IsNullOrWhiteSpace(m.ThumbUrl))
                    m.ThumbUrl = $"/api/movies/{m.Id}/image/thumb";
            }
            return Results.Ok(movies);
        });

        // One movie, fully hydrated. NOTE: use explicit column aliases — Dapper
        // does not map snake_case columns (cover_url) to PascalCase props (CoverUrl).
        g.MapGet("/{id:long}", async (long id, DbConnectionFactory db, PreviewImageService previews) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var movie = await c.QueryFirstOrDefaultAsync<Movie>(@"
                SELECT id Id, library_id LibraryId, number Number, title Title,
                       summary Summary, maker Maker, label Label, series Series,
                       director Director, release_date ReleaseDate,
                       runtime_minutes RuntimeMinutes, cover_url CoverUrl,
                       thumb_url ThumbUrl, score Score, provider Provider,
                       homepage_url HomepageUrl, folder_path FolderPath
                FROM movies WHERE id=@id", new { id });
            if (movie == null) return Results.NotFound();
            // Override cover/thumb to read from the local movie folder
            // ({番号}-poster.jpg / {番号}-thumb.jpg) instead of the stored
            // MetaTube remote URL, so the detail page works offline.
            if (!string.IsNullOrWhiteSpace(movie.FolderPath))
            {
                var poster = Path.Combine(movie.FolderPath, $"{movie.Number}-poster.jpg");
                var thumb = Path.Combine(movie.FolderPath, $"{movie.Number}-thumb.jpg");
                if (File.Exists(poster)) movie.CoverUrl = $"/api/movies/{id}/image/poster";
                if (File.Exists(thumb)) movie.ThumbUrl = $"/api/movies/{id}/image/thumb";
            }
            // Serve preview images from the local cache (one entry per cached file),
            // pointing at the static preview endpoint so they work offline.
            var count = previews.Count(id);
            movie.PreviewImages = Enumerable.Range(0, count)
                .Select(n => $"/api/movies/{id}/preview/{n}").ToList();
            movie.Actors = (await c.QueryAsync<Actor>(@"
                SELECT a.id Id, a.name Name, a.avatar_url AvatarUrl
                FROM actors a JOIN movie_actors ma ON ma.actor_id=a.id
                WHERE ma.movie_id=@id", new { id })).ToList();
            movie.Tags = (await c.QueryAsync<Tag>(@"
                SELECT t.id Id, t.name Name, t.category Category, t.is_standard IsStandard
                FROM tags t JOIN movie_tags mt ON mt.tag_id=t.id
                WHERE mt.movie_id=@id", new { id })).ToList();
            movie.Magnets = (await c.QueryAsync<MagnetResult>(@"
                SELECT title Title, size Size, magnet_uri MagnetUri, source Source
                FROM magnets WHERE movie_id=@id", new { id })).ToList();
            // Report whether a {番号}-trailer.mp4 exists in the movie folder.
            movie.HasTrailer = !string.IsNullOrWhiteSpace(movie.FolderPath)
                && File.Exists(Path.Combine(movie.FolderPath, $"{movie.Number}-trailer.mp4"));
            return Results.Ok(movie);
        });

        // Serve the trailer file (streaming) for inline playback.
        g.MapGet("/{id:long}/trailer", async (long id, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var row = await c.QueryFirstOrDefaultAsync<(string? Folder, string Number)>(
                "SELECT folder_path Folder, number Number FROM movies WHERE id=@id", new { id });
            if (row.Folder == null) return Results.NotFound();
            var path = Path.Combine(row.Folder, $"{row.Number}-trailer.mp4");
            return File.Exists(path) ? Results.File(path, "video/mp4", enableRangeProcessing: true) : Results.NotFound();
        });

        // Serve the local poster/thumb image from the movie folder.
        // Falls back to proxying the remote URL (from DB) so the webview never
        // hits dmm directly (avoids Tracking Prevention blocking).
        g.MapGet("/{id:long}/image/{type}", async (long id, string type, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var row = await c.QueryFirstOrDefaultAsync<(string? Folder, string Number, string? CoverUrl, string? ThumbUrl)>(
                "SELECT folder_path Folder, number Number, cover_url CoverUrl, thumb_url ThumbUrl FROM movies WHERE id=@id", new { id });
            if (row.Number == null) return Results.NotFound();

            // 1. Try local file first.
            var suffix = type == "poster" ? "-poster.jpg" : "-thumb.jpg";
            if (row.Folder != null)
            {
                var path = Path.Combine(row.Folder, $"{row.Number}{suffix}");
                if (File.Exists(path)) return Results.File(path, "image/jpeg");
            }

            // 2. Fall back to proxying the remote URL via the worker (avoids dmm
            //    tracking-prevention in the webview). Try both cover and thumb URLs.
            var remoteUrl = type == "poster"
                ? (row.CoverUrl ?? row.ThumbUrl)
                : (row.ThumbUrl ?? row.CoverUrl);
            if (string.IsNullOrWhiteSpace(remoteUrl)) return Results.NotFound();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                // Try the URL as-is first, then without ?auto=false (some providers don't support it).
                foreach (var tryUrl in new[] { remoteUrl, remoteUrl.Split('?')[0] })
                {
                    try
                    {
                        var imgBytes = await http.GetByteArrayAsync(tryUrl);
                        if (imgBytes.Length > 0) return Results.File(imgBytes, "image/jpeg");
                    }
                    catch { /* try next variant */ }
                }
                return Results.NotFound();
            }
            catch { return Results.NotFound(); }
        });

        // Serve a cached preview image for a movie.
        g.MapGet("/{id:long}/preview/{n:int}", (long id, int n, PreviewImageService previews) =>
        {
            var path = previews.PathFor(id, n);
            return File.Exists(path) ? Results.File(path, "image/jpeg") : Results.NotFound();
        });

        // Ingest a scraped movie into a library (writes nfo/poster/thumb/magnet.txt).
        g.MapPost("/ingest", async (IngestRequest req, IngestService ingest, MagnetService magnets) =>
        {
            req.Movie.LibraryId = req.LibraryId;
            var mags = req.Magnets ?? await magnets.SearchAsync(req.Movie.Number);
            var res = await ingest.IngestAsync(req.Movie, mags);
            return Results.Ok(res);
        });

        // Scrape by 番号 + ingest in one shot (used by the scan-to-ingest flow).
        // ponytail: if SourceFilePath is provided, move the source file into the
        // created folder after ingest (used for .strm files from scanning).
        g.MapPost("/ingest-by-number", async (IngestByNumberRequest req, HttpContext ctx, MetaTubeClient mt, MagnetService magnets, IngestService ingest) =>
        {
            var movie = await mt.GetMovieAsync(req.Number);
            if (movie == null)
                return Results.NotFound(new { ok = false, detail = "MetaTube 未找到该番号" });
            movie.LibraryId = req.LibraryId;
            try
            {
                var ts = ctx.RequestServices.GetRequiredService<TranslationService>();
                await ts.TranslateAsync(movie);
            }
            catch { /* LLM not configured — skip silently */ }
            var mags = await magnets.SearchAsync(req.Number);
            var res = await ingest.IngestAsync(movie, mags);
            // Move source file into the created folder (e.g. .strm files).
            if (!string.IsNullOrWhiteSpace(req.SourceFilePath) && !string.IsNullOrWhiteSpace(res.FolderPath))
            {
                try { MediaExtensions.MoveSourceFile(req.SourceFilePath, res.FolderPath); }
                catch (Exception ex) { Serilog.Log.Warning(ex, "Failed to move source file {File}", req.SourceFilePath); }
            }
            return Results.Ok(res);
        });

        // Add a custom tag to a movie (find-or-create by name).
        g.MapPost("/{id:long}/tags", async (long id, AddTagRequest req, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var movie = await c.QueryFirstOrDefaultAsync("SELECT id FROM movies WHERE id=@id", new { id });
            if (movie == null) return Results.NotFound(new { ok = false, detail = "影片不存在" });
            var name = req.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { ok = false, detail = "标签名不能为空" });
            // Upsert the tag (find existing or create as custom).
            var tagId = await c.QueryFirstOrDefaultAsync<long>(
                "SELECT id FROM tags WHERE name=@name", new { name });
            if (tagId == 0)
            {
                tagId = await c.QueryFirstAsync<long>(@"
                    INSERT INTO tags(name, category, is_standard)
                    VALUES(@name,'custom',0);
                    SELECT last_insert_rowid()", new { name });
            }
            await c.ExecuteAsync(
                "INSERT OR IGNORE INTO movie_tags(movie_id, tag_id) VALUES(@movieId,@tagId)",
                new { movieId = id, tagId });
            return Results.Ok(new { ok = true, detail = "标签已添加" });
        });

        // Play a movie's local file in the configured player.
        g.MapPost("/{id:long}/play", async (long id, DbConnectionFactory db, PlayerService player) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var folder = await c.ExecuteScalarAsync<string?>(
                "SELECT folder_path FROM movies WHERE id=@id", new { id });
            // Find the first media file inside the movie folder (skip trailer).
            string? file = null;
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                file = Directory.EnumerateFiles(folder)
                    .Where(f => !f.Contains("-trailer", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(f => MediaExtensions.Values.Contains(Path.GetExtension(f)));
            }
            if (file == null)
                return Results.Ok(new { ok = false, detail = "未找到可播放的媒体文件" });
            var res = await player.PlayAsync(file);
            return Results.Ok(res);
        });

        g.MapDelete("/{id:long}", async (long id, DbConnectionFactory db, bool removeFiles = false) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var folder = await c.ExecuteScalarAsync<string?>(
                "SELECT folder_path FROM movies WHERE id=@id", new { id });
            // Cascade: remove the movie row and any favorite pointing at it,
            // so the favorites list never shows a deleted movie.
            await c.ExecuteAsync("DELETE FROM movies WHERE id=@id", new { id });
            await c.ExecuteAsync(
                "DELETE FROM favorites WHERE target_type='movie' AND target_id=@id", new { id });
            // Optionally wipe the generated folder (nfo/poster/thumb/magnet.txt).
            if (removeFiles && !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                try { Directory.Delete(folder, recursive: true); }
                catch { /* best-effort */ }
            }
            return Results.NoContent();
        });

        // Re-scrape an existing movie: re-fetch metadata from MetaTube, translate,
        // update the DB row + actors/tags, and rewrite nfo/poster/thumb.
        // ponytail: trailer download decoupled from MetaTube — if scrape times out
        // we still attempt trailer using the existing folder path.
        g.MapPost("/{id:long}/rescrape", async (long id, HttpContext ctx,
            DbConnectionFactory db, MetaTubeClient mt, MagnetService magnets, IngestService ingest) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var existing = await c.QueryFirstOrDefaultAsync<(long LibraryId, string Number, string? Folder)>(
                "SELECT library_id LibraryId, number Number, folder_path Folder FROM movies WHERE id=@id", new { id });
            if (existing.Number == null)
                return Results.NotFound(new { ok = false, detail = "影片不存在" });

            Movie? movie = null;
            try { movie = await mt.GetMovieAsync(existing.Number); }
            catch { /* MetaTube unavailable — will try trailer independently */ }

            if (movie != null)
            {
                movie.Id = id;
                movie.LibraryId = existing.LibraryId;
                try
                {
                    var ts = ctx.RequestServices.GetRequiredService<TranslationService>();
                    await ts.TranslateAsync(movie);
                }
                catch { /* LLM not configured — skip silently */ }
                var mags = await magnets.SearchAsync(existing.Number);
                await ingest.IngestAsync(movie, mags);
                return Results.Ok(new { ok = true, detail = "已重新刮削" });
            }

            // MetaTube failed — attempt trailer-only update using existing folder.
            if (!string.IsNullOrWhiteSpace(existing.Folder))
                await TrailerHelper.TryDownloadTrailerAsync(ctx, existing.Number, existing.Folder);
            return Results.Ok(new { ok = true, detail = "MetaTube 超时,已尝试更新预告片" });
        });

        // Re-scrape with a specific provider/id (when user picks a candidate).
        g.MapPost("/{id:long}/rescrape-pick", async (long id, HttpContext ctx,
            DbConnectionFactory db, MetaTubeClient mt, MagnetService magnets, IngestService ingest) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<RescrapePickRequest>(ctx.RequestAborted);
            if (body == null || string.IsNullOrEmpty(body.Provider) || string.IsNullOrEmpty(body.Id))
                return Results.BadRequest(new { ok = false, detail = "缺少 provider/id" });

            await using var c = db.Create();
            await c.OpenAsync();
            var existing = await c.QueryFirstOrDefaultAsync<(long LibraryId, string Number)>(
                "SELECT library_id LibraryId, number Number FROM movies WHERE id=@id", new { id });
            if (existing.Number == null)
                return Results.NotFound(new { ok = false, detail = "影片不存在" });

            Movie? movie = null;
            try { movie = await mt.GetMovieByProviderAsync(body.Provider, body.Id, existing.Number); }
            catch { }

            if (movie != null)
            {
                movie.Id = id;
                movie.LibraryId = existing.LibraryId;
                try
                {
                    var ts = ctx.RequestServices.GetRequiredService<TranslationService>();
                    await ts.TranslateAsync(movie);
                }
                catch { }
                var mags = await magnets.SearchAsync(existing.Number);
                await ingest.IngestAsync(movie, mags);
                return Results.Ok(new { ok = true, detail = "已重新刮削" });
            }
            return Results.Ok(new { ok = false, detail = "MetaTube 未找到该候选" });
        });
    }
}

public record IngestRequest(long LibraryId, Movie Movie, List<MagnetResult>? Magnets);
public record IngestByNumberRequest(long LibraryId, string Number, string? SourceFilePath);
public record AddTagRequest(string Name);
public record RescrapePickRequest(string Provider, string Id);

public static class MediaExtensions
{
    public static readonly HashSet<string> Values =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".strm", ".avi", ".wmv", ".mov", ".ts", ".m4v" };

    /// <summary>Move a source file into the movie folder, renaming to {番号}{ext}.</summary>
    public static void MoveSourceFile(string sourcePath, string folderPath)
    {
        var ext = Path.GetExtension(sourcePath);
        var dest = Path.Combine(folderPath, $"{Path.GetFileNameWithoutExtension(Path.GetFileName(folderPath))}{ext}");
        if (!File.Exists(dest))
            File.Move(sourcePath, dest);
    }
}

/// <summary>Decoupled trailer-only download for when MetaTube is unavailable.
/// Mirrors the trailer logic inside IngestService.IngestAsync.</summary>
file static class TrailerHelper
{
    public static async Task TryDownloadTrailerAsync(HttpContext ctx, string number, string folder)
    {
        try
        {
            var settings = ctx.RequestServices.GetRequiredService<SettingsService>();
            var on = (await settings.GetAsync(SettingsService.KeyScrapeTrailer))?.Trim();
            if (!string.Equals(on, "true", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(on, "1", StringComparison.OrdinalIgnoreCase))
                return;
            var tc = ctx.RequestServices.GetRequiredService<TrailerClient>();
            var url = await tc.FindTrailerUrlAsync(number);
            if (string.IsNullOrWhiteSpace(url)) return;
            var bytes = await tc.DownloadAsync(url);
            if (bytes != null)
                await File.WriteAllBytesAsync(Path.Combine(folder, $"{number}-trailer.mp4"), bytes);
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Trailer-only download failed for {Number}", number); }
    }
}
