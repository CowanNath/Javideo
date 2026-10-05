using Dapper;
using Javideo.Worker.Db;
using Javideo.Worker.Magnet;
using Javideo.Worker.Models;
using Javideo.Worker.Services;

namespace Javideo.Worker.Endpoints;

public static class MovieEndpoints
{
    // Shared client for remote-image proxying — a per-request client exhausts
    // sockets when a library grid fetches dozens of covers at once.
    private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Whether a playable video exists: in the movie's folder, or —
    /// cloud-drive libraries (cache_local) — at the recorded source_path. The
    /// cloud path was verified at ingest, so presence is judged from the DB
    /// value alone: stat-ing a cloud mount once per card would stall the grid.
    /// Used by the list endpoints for the card badges.</summary>
    internal static bool HasVideoFile(string? folder, string number, string? sourcePath = null)
    {
        if (!string.IsNullOrWhiteSpace(sourcePath)) return true;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return false;
        try
        {
            return Directory.EnumerateFiles(folder).Any(f =>
                !f.Contains("-trailer", StringComparison.OrdinalIgnoreCase) &&
                MediaExtensions.Values.Contains(Path.GetExtension(f)));
        }
        catch { return false; }
    }

    /// <summary>Load one movie with everything the detail UI needs: local
    /// poster/thumb paths, cached previews, actors, tags, magnets, trailer
    /// presence. Shared by /{id} and /by-number/{number}.</summary>
    internal static async Task<Movie?> LoadMovieDetailAsync(long id, DbConnectionFactory db, PreviewImageService previews)
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
        if (movie == null) return null;
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
        // Point avatars at the local endpoint: serves the cached file and,
        // for manually added actors, lazily resolves + downloads one via
        // MetaTube on first display.
        foreach (var a in movie.Actors)
            if (a.Id > 0) a.AvatarUrl = $"/api/actors/{a.Id}/avatar";
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
        return movie;
    }

    public static void MapMovieEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/movies").WithTags("Movies");

        // Virtual "all" library: every ingested movie across all libraries.
        g.MapGet("/library/all", async (DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var movies = (await c.QueryAsync<Movie>(@"
                SELECT id Id, library_id LibraryId, number Number, title Title, summary Summary,
                       maker Maker, label Label, series Series, director Director,
                       release_date ReleaseDate, runtime_minutes RuntimeMinutes,
                       cover_url CoverUrl, thumb_url ThumbUrl, score Score, folder_path FolderPath,
                       source_path SourcePath
                FROM movies ORDER BY id DESC")).ToList();
            foreach (var m in movies)
            {
                if (!string.IsNullOrWhiteSpace(m.CoverUrl))
                    m.CoverUrl = $"/api/movies/{m.Id}/image/poster";
                if (!string.IsNullOrWhiteSpace(m.ThumbUrl))
                    m.ThumbUrl = $"/api/movies/{m.Id}/image/thumb";
                m.HasVideo = HasVideoFile(m.FolderPath, m.Number ?? "", m.SourcePath);
                m.HasTrailer = !string.IsNullOrWhiteSpace(m.FolderPath)
                    && File.Exists(Path.Combine(m.FolderPath, $"{m.Number}-trailer.mp4"));
            }
            return Results.Ok(movies);
        });

        // Movies in a library (with actors/tags/thumbnails).
        g.MapGet("/library/{libraryId:long}", async (long libraryId, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var movies = (await c.QueryAsync<Movie>(@"
                SELECT id Id, library_id LibraryId, number Number, title Title, summary Summary,
                       maker Maker, label Label, series Series, director Director,
                       release_date ReleaseDate, runtime_minutes RuntimeMinutes,
                       cover_url CoverUrl, thumb_url ThumbUrl, score Score, folder_path FolderPath,
                       source_path SourcePath
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
                // Card badges: video file present / trailer downloaded.
                m.HasVideo = HasVideoFile(m.FolderPath, m.Number ?? "", m.SourcePath);
                m.HasTrailer = !string.IsNullOrWhiteSpace(m.FolderPath)
                    && File.Exists(Path.Combine(m.FolderPath, $"{m.Number}-trailer.mp4"));
            }
            return Results.Ok(movies);
        });

        // One movie, fully hydrated. NOTE: use explicit column aliases — Dapper
        // does not map snake_case columns (cover_url) to PascalCase props (CoverUrl).
        g.MapGet("/{id:long}", async (long id, DbConnectionFactory db, PreviewImageService previews) =>
        {
            var movie = await LoadMovieDetailAsync(id, db, previews);
            return movie == null ? Results.NotFound() : Results.Ok(movie);
        });

        // Look up a movie by 番号 across all libraries. Used by the search page:
        // when the number is already ingested, the UI shows the stored record
        // (with its real id → "已入库" badge, local images, folder path)
        // instead of re-scraping MetaTube.
        g.MapGet("/by-number/{number}", async (string number, DbConnectionFactory db, PreviewImageService previews) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var id = await c.ExecuteScalarAsync<long?>(
                "SELECT id FROM movies WHERE number=@n ORDER BY id LIMIT 1", new { n = number });
            if (id == null) return Results.NotFound();
            var movie = await LoadMovieDetailAsync(id.Value, db, previews);
            return movie == null ? Results.NotFound() : Results.Ok(movie);
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

            // 1. Try local file first; if only the other image was downloaded,
            //    serve that rather than a 404.
            var suffix = type == "poster" ? "-poster.jpg" : "-thumb.jpg";
            var otherSuffix = type == "poster" ? "-thumb.jpg" : "-poster.jpg";
            if (row.Folder != null)
            {
                var path = Path.Combine(row.Folder, $"{row.Number}{suffix}");
                if (File.Exists(path)) return Results.File(path, "image/jpeg");
                var other = Path.Combine(row.Folder, $"{row.Number}{otherSuffix}");
                if (File.Exists(other)) return Results.File(other, "image/jpeg");
            }

            // 2. Fall back to proxying the remote URL via the worker (avoids dmm
            //    tracking-prevention in the webview). Try both cover and thumb URLs.
            var remoteUrl = type == "poster"
                ? (row.CoverUrl ?? row.ThumbUrl)
                : (row.ThumbUrl ?? row.CoverUrl);
            if (string.IsNullOrWhiteSpace(remoteUrl)) return Results.NotFound();
            try
            {
                // Try the URL as-is first, then without ?auto=false (some providers don't support it).
                foreach (var tryUrl in new[] { remoteUrl, remoteUrl.Split('?')[0] })
                {
                    try
                    {
                        var imgBytes = await ImageHttp.GetByteArrayAsync(tryUrl);
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
            if (req.Movie is null || string.IsNullOrWhiteSpace(req.Movie.Number))
                return Results.BadRequest(new { ok = false, detail = "缺少影片数据或番号" });
            req.Movie.LibraryId = req.LibraryId;
            var mags = req.Magnets ?? await magnets.SearchAsync(req.Movie.Number);
            var res = await ingest.IngestAsync(req.Movie, mags);
            return Results.Ok(res);
        });

        // Scrape by 番号 + ingest in one shot (used by the scan-to-ingest flow).
        // ponytail: if SourceFilePath is provided, move the source file into the
        // created folder after ingest (used for .strm files from scanning).
        g.MapPost("/ingest-by-number", async (IngestByNumberRequest req, HttpContext ctx, MetaTubeClient mt, MagnetService magnets, IngestService ingest, DbConnectionFactory db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Number))
                return Results.BadRequest(new { ok = false, detail = "缺少番号" });
            Movie? movie;
            try { movie = await mt.GetMovieAsync(req.Number); }
            catch (MetaTubeNotConfiguredException)
            {
                return Results.Json(
                    new { ok = false, needsConfig = true, detail = "未配置 MetaTube 服务地址,请到「设置 → MetaTube」填写" },
                    statusCode: StatusCodes.Status412PreconditionFailed);
            }
            if (movie == null)
                return Results.NotFound(new { ok = false, detail = "MetaTube 未找到该番号" });
            movie.LibraryId = req.LibraryId;
            // File-name status markers (-C/-U/-UC/4K) become tags on the movie.
            if (!string.IsNullOrWhiteSpace(req.SourceFilePath))
                movie.Tags.AddRange(MediaExtensions.MarkerTags(req.SourceFilePath));
            try
            {
                var ts = ctx.RequestServices.GetRequiredService<TranslationService>();
                await ts.TranslateAsync(movie);
            }
            catch { /* LLM not configured — skip silently */ }
            var mags = await magnets.SearchAsync(req.Number);
            var res = await ingest.IngestAsync(movie, mags);
            // Move source file into the created folder keeping its original
            // name. Multi-part siblings (same 番号) are collected as-is too.
            // Cloud-drive library (cache_local): the video stays on the remote
            // drive — record its location instead of moving it locally.
            var cacheLocal = await db.IsCacheLocalAsync(req.LibraryId);
            if (cacheLocal)
            {
                if (!string.IsNullOrWhiteSpace(req.SourceFilePath) && File.Exists(req.SourceFilePath))
                {
                    await using var c = db.Create();
                    await c.OpenAsync();
                    await c.ExecuteAsync("UPDATE movies SET source_path=@sp WHERE id=@id",
                        new { sp = req.SourceFilePath, id = res.MovieId });
                }
                return Results.Ok(res);
            }
            if (!string.IsNullOrWhiteSpace(req.SourceFilePath) && !string.IsNullOrWhiteSpace(res.FolderPath))
            {
                try { MediaExtensions.MoveSourceFile(req.SourceFilePath, res.FolderPath); }
                catch (Exception ex) { Serilog.Log.Warning(ex, "Failed to move source file {File}", req.SourceFilePath); }
                try
                {
                    var dir = Path.GetDirectoryName(req.SourceFilePath);
                    if (dir != null)
                    {
                        foreach (var f in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        {
                            if (string.Equals(f, req.SourceFilePath, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!MediaExtensions.Values.Contains(Path.GetExtension(f))) continue;
                            var siblingNum = Scanner.RecognizeNumber(Path.GetFileName(f));
                            if (siblingNum == null || !siblingNum.Equals(movie.Number, StringComparison.OrdinalIgnoreCase)) continue;
                            try { MediaExtensions.MoveSourceFile(f, res.FolderPath); }
                            catch (Exception ex) { Serilog.Log.Warning(ex, "Failed to move part file {File}", f); }
                        }
                    }
                }
                catch (Exception ex) { Serilog.Log.Warning(ex, "Failed to collect multi-part files for {File}", req.SourceFilePath); }
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
            var name = req.Name?.Trim();
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

        // Add an actor to a movie (find-or-create by name). manual=1 so the
        // link survives rescrape even when MetaTube returns no cast.
        g.MapPost("/{id:long}/actors", async (long id, AddActorRequest req, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var movie = await c.QueryFirstOrDefaultAsync("SELECT id FROM movies WHERE id=@id", new { id });
            if (movie == null) return Results.NotFound(new { ok = false, detail = "影片不存在" });
            var name = req.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { ok = false, detail = "演员名不能为空" });
            await c.ExecuteAsync(
                "INSERT INTO actors(name) VALUES(@name) ON CONFLICT(name) DO NOTHING", new { name });
            var actorId = await c.ExecuteScalarAsync<long>("SELECT id FROM actors WHERE name=@name", new { name });
            await c.ExecuteAsync(
                "INSERT INTO movie_actors(movie_id, actor_id, manual) VALUES(@m,@a,1) " +
                "ON CONFLICT(movie_id, actor_id) DO UPDATE SET manual=1",
                new { m = id, a = actorId });
            return Results.Ok(new { ok = true, detail = "演员已添加" });
        });

        // Remove an actor from a movie (the actor itself stays in the DB).
        g.MapDelete("/{id:long}/actors/{actorId:long}", async (long id, long actorId, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            await c.ExecuteAsync(
                "DELETE FROM movie_actors WHERE movie_id=@id AND actor_id=@aid",
                new { id, aid = actorId });
            return Results.Ok(new { ok = true, detail = "演员已移除" });
        });

        // Play a movie's local file in the configured player.
        g.MapPost("/{id:long}/play", async (long id, DbConnectionFactory db, PlayerService player) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var row = await c.QueryFirstOrDefaultAsync<(string? Folder, string? Source, long LibId, string Number)>(
                "SELECT folder_path Folder, source_path Source, library_id LibId, number Number FROM movies WHERE id=@id", new { id });
            // Folder first (local libraries), then the recorded cloud location.
            var file = MediaExtensions.FindFirstVideo(row.Folder);
            if (file == null && !string.IsNullOrWhiteSpace(row.Source) && File.Exists(row.Source))
                file = row.Source;
            // Self-heal records moved into a cloud library before source_path
            // was recorded: their video sits in {library dir}\{番号}\. Find it
            // once, remember it, and play — later plays skip this scan.
            if (file == null)
            {
                var dirs = (await c.QueryAsync<string>(
                    "SELECT path FROM library_directories WHERE library_id=@id", new { id = row.LibId })).ToList();
                foreach (var dir in dirs)
                {
                    var candidate = MediaExtensions.FindFirstVideo(
                        Path.Combine(dir, IngestService.SafeFolder(row.Number)));
                    if (candidate == null) continue;
                    file = candidate;
                    await c.ExecuteAsync("UPDATE movies SET source_path=@sp WHERE id=@id", new { sp = file, id });
                    break;
                }
            }
            if (file == null)
                return Results.Ok(new { ok = false, detail = "未找到可播放的媒体文件" });
            var res = await player.PlayAsync(file);
            return Results.Ok(res);
        });

        g.MapDelete("/{id:long}", async (long id, DbConnectionFactory db, HighlightService highlights, bool removeFiles = false) =>
        {
            await highlights.MutationGate.WaitAsync();
            try
            {
                await using var c = db.Create();
                await c.OpenAsync();
                await using var tx = c.BeginTransaction();
                var folder = await c.ExecuteScalarAsync<string?>(
                    "SELECT folder_path FROM movies WHERE id=@id", new { id }, tx);
                // Explicit cleanup also works for legacy databases with foreign keys disabled.
                await c.ExecuteAsync("DELETE FROM movie_highlight_assets WHERE movie_id=@id", new { id }, tx);
                await c.ExecuteAsync("DELETE FROM movie_highlights WHERE movie_id=@id", new { id }, tx);
                await c.ExecuteAsync("DELETE FROM movies WHERE id=@id", new { id }, tx);
                await c.ExecuteAsync(
                    "DELETE FROM favorites WHERE target_type='movie' AND target_id=@id", new { id }, tx);
                tx.Commit();
                highlights.RemoveMovieFiles(id);
                // Optionally wipe the generated folder (nfo/poster/thumb/magnet.txt).
                if (removeFiles && !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    try { Directory.Delete(folder, recursive: true); }
                    catch { /* best-effort */ }
                }
                return Results.NoContent();
            }
            finally { highlights.MutationGate.Release(); }
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

        // Move a movie to a different library (updates DB + moves the folder).
        g.MapPost("/{id:long}/move", async (long id, MoveRequest req,
            DbConnectionFactory db, LibraryService libs) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            // Reserve the SQLite writer while checking and committing the move.
            // This prevents a concurrent ingest from introducing a duplicate
            // after the conflict check but before the filesystem move.
            await using var tx = c.BeginTransaction();
            var movie = await c.QueryFirstOrDefaultAsync<(long LibId, string Number, string? Folder)>(
                "SELECT library_id LibId, number Number, folder_path Folder FROM movies WHERE id=@id", new { id }, tx);
            if (movie.Number == null)
                return Results.NotFound(new { ok = false, detail = "影片不存在" });

            if (movie.LibId == req.TargetLibraryId)
                return Results.Ok(new { ok = true, detail = "影片已在目标媒体库" });
            var targetExists = await c.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM libraries WHERE id=@id", new { id = req.TargetLibraryId }, tx);
            if (targetExists == 0)
                return Results.NotFound(new { ok = false, detail = "目标媒体库不存在" });
            var duplicate = await c.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM movies WHERE library_id=@lib AND number=@number AND id<>@id",
                new { lib = req.TargetLibraryId, number = movie.Number, id }, tx);
            if (duplicate > 0)
                return Results.Conflict(new { ok = false, detail = "目标媒体库已有相同番号的影片" });

            // Cloud-drive target (cache_local): videos belong in the cloud
            // directory and metadata in the local cache — same layout ingest
            // produces. Split the folder instead of dragging it in as one blob
            // (which used to leave source_path unset and broke playback).
            var cacheLocal = await c.ExecuteScalarAsync<long?>(
                "SELECT cache_local FROM libraries WHERE id=@id", new { id = req.TargetLibraryId }, tx) == 1;
            if (cacheLocal)
            {
                var targetDir = await libs.FirstAvailableDirectoryAsync(req.TargetLibraryId);
                if (string.IsNullOrWhiteSpace(targetDir))
                    return Results.BadRequest(new { ok = false, detail = "目标媒体库无可用目录" });

                var safe = IngestService.SafeFolder(movie.Number);
                var cacheFolder = Path.Combine(db.DataDir, "cache", safe);
                Directory.CreateDirectory(cacheFolder);
                var cloudFolder = Path.Combine(targetDir, safe);
                Directory.CreateDirectory(cloudFolder);

                string? source = null;
                var movedFiles = new List<(string Source, string Destination)>();
                try
                {
                    if (!string.IsNullOrWhiteSpace(movie.Folder) && Directory.Exists(movie.Folder))
                    {
                        foreach (var f in Directory.EnumerateFiles(movie.Folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        {
                            var isMedia = !f.Contains("-trailer", StringComparison.OrdinalIgnoreCase) &&
                                          MediaExtensions.Values.Contains(Path.GetExtension(f));
                            var destDir = isMedia ? cloudFolder : cacheFolder;
                            var dest = Path.Combine(destDir, Path.GetFileName(f));
                            if (!File.Exists(dest))
                            {
                                File.Move(f, dest);
                                movedFiles.Add((f, dest));
                            }
                            if (isMedia && source == null) source = dest;
                        }
                    }
                    source ??= MediaExtensions.FindFirstVideo(cloudFolder);

                    await c.ExecuteAsync(
                        "UPDATE movies SET library_id=@lib, folder_path=@fp, source_path=@sp WHERE id=@id",
                        new { lib = req.TargetLibraryId, fp = cacheFolder, sp = source, id }, tx);
                    tx.Commit();
                }
                catch
                {
                    // Roll back each completed file move on either I/O or DB
                    // failure. Keep the original directory until commit succeeds.
                    foreach (var moved in movedFiles.AsEnumerable().Reverse())
                    {
                        try { File.Move(moved.Destination, moved.Source); }
                        catch (Exception ex) { Serilog.Log.Error(ex, "Failed to restore moved file {File}", moved.Source); }
                    }
                    throw;
                }
                if (!string.IsNullOrWhiteSpace(movie.Folder) && Directory.Exists(movie.Folder))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(movie.Folder).Any())
                            Directory.Delete(movie.Folder);
                    }
                    catch { /* non-empty or locked — leave it */ }
                }
                return Results.Ok(new { ok = true, detail = "已移动" });
            }

            var localTargetDir = await libs.FirstAvailableDirectoryAsync(req.TargetLibraryId);
            if (string.IsNullOrWhiteSpace(localTargetDir))
                return Results.BadRequest(new { ok = false, detail = "目标媒体库无可用目录" });

            // Move the folder if it exists.
            string? newFolder = null;
            if (!string.IsNullOrWhiteSpace(movie.Folder) && Directory.Exists(movie.Folder))
            {
                var folderName = Path.GetFileName(movie.Folder);
                newFolder = Path.Combine(localTargetDir, folderName);
                if (newFolder != movie.Folder)
                {
                    // An existing target folder may hold the user's real video
                    // files (ingest moves sources into it) — NEVER delete it.
                    // Pick a unique name instead: "ABC (2)", "ABC (3)", ...
                    if (Directory.Exists(newFolder))
                    {
                        var unique = Path.Combine(localTargetDir, $"{folderName} (2)");
                        for (var i = 3; i < 100 && Directory.Exists(unique); i++)
                            unique = Path.Combine(localTargetDir, $"{folderName} ({i})");
                        if (Directory.Exists(unique))
                            return Results.Conflict(new { ok = false, detail = "目标目录已存在同名文件夹" });
                        newFolder = unique;
                    }
                    try
                    {
                        Directory.Move(movie.Folder, newFolder);
                    }
                    catch (Exception ex)
                    {
                        return Results.Json(new { ok = false, detail = $"移动文件夹失败: {ex.Message}" },
                            statusCode: StatusCodes.Status500InternalServerError);
                    }
                }
            }

            try
            {
                await c.ExecuteAsync(
                    "UPDATE movies SET library_id=@lib, folder_path=@fp WHERE id=@id",
                    new { lib = req.TargetLibraryId, fp = newFolder ?? movie.Folder, id }, tx);
                tx.Commit();
            }
            catch
            {
                // If the DB commit fails after a local move, restore the path
                // recorded by the unchanged database before reporting failure.
                if (!string.IsNullOrWhiteSpace(newFolder) && newFolder != movie.Folder &&
                    Directory.Exists(newFolder) && !Directory.Exists(movie.Folder))
                    Directory.Move(newFolder, movie.Folder!);
                throw;
            }
            return Results.Ok(new { ok = true, detail = "已移动" });
        });
    }
}

public record MoveRequest(long TargetLibraryId);

public record IngestRequest(long LibraryId, Movie Movie, List<MagnetResult>? Magnets);
public record IngestByNumberRequest(long LibraryId, string Number, string? SourceFilePath);
public record AddTagRequest(string Name);
public record AddActorRequest(string Name);
public record RescrapePickRequest(string Provider, string Id);

public static class MediaExtensions
{
    public static readonly HashSet<string> Values =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".strm", ".avi", ".wmv", ".mov", ".ts", ".m4v" };

    /// <summary>Move a source file into the movie folder keeping its original
    /// name — if the scrape turns out to be wrong, the original name is the
    /// only trace of what the file really is.</summary>
    public static void MoveSourceFile(string sourcePath, string folderPath)
    {
        var dest = Path.Combine(folderPath, Path.GetFileName(sourcePath));
        if (!File.Exists(dest))
            File.Move(sourcePath, dest);
    }

    /// <summary>First playable (non-trailer) media file directly inside the
    /// folder, or null. Shared by play / move / subtitle lookups.</summary>
    public static string? FindFirstVideo(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => !f.Contains("-trailer", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(f => Values.Contains(Path.GetExtension(f)));
        }
        catch { return null; }
    }

    /// <summary>Status markers commonly appended to file names → tags recorded
    /// on the movie: -C/-CH=中文, -U/-uncensored=无码修复, -UC=both, 4K
    /// anywhere=4K. A marker may also be appended directly after the digits
    /// (HUNT801C, START165C, ABP123CH).</summary>
    public static List<Tag> MarkerTags(string? fileName)
    {
        var tags = new List<Tag>();
        if (string.IsNullOrWhiteSpace(fileName)) return tags;
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

        void Add(string name)
        {
            if (tags.All(t => !string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                tags.Add(new Tag { Name = name, Category = "custom", IsStandard = false });
        }
        // Suffix like "-c", "_ch", or a bare "c"/"ch" directly after a digit.
        bool Has(string marker) =>
            stem.EndsWith($"-{marker}") || stem.EndsWith($"_{marker}") ||
            (stem.EndsWith(marker) && stem.Length > marker.Length && char.IsDigit(stem[stem.Length - marker.Length - 1]));

        if (Has("uc")) { Add("中文"); Add("无码修复"); }
        else
        {
            if (Has("c") || Has("ch")) Add("中文");
            if (Has("u") || stem.Contains("-uncensored") || stem.Contains("_uncensored")) Add("无码修复");
        }
        if (stem.Contains("4k")) Add("4K");
        return tags;
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
