using Dapper;
using Javideo.Worker.Db;
using Javideo.Worker.Models;

namespace Javideo.Worker.Services;

/// <summary>
/// The ingestion pipeline: persists a scraped Movie (+ actors/tags/magnets)
/// to the DB and writes nfo / poster / thumb / 磁力链接.txt into the library folder.
/// </summary>
public sealed class IngestService
{
    private readonly DbConnectionFactory _db;
    private readonly LibraryService _libs;
    private readonly NfoWriter _nfo;
    private readonly ImageWriter _images;
    private readonly MetaTubeClient _metaTube;
    private readonly IServiceProvider _sp;

    public IngestService(DbConnectionFactory db, LibraryService libs, NfoWriter nfo, ImageWriter images, MetaTubeClient metaTube, IServiceProvider sp)
    {
        _db = db; _libs = libs; _nfo = nfo; _images = images; _metaTube = metaTube; _sp = sp;
    }

    public record IngestResult(long MovieId, string? FolderPath, bool ImagesOk);

    public async Task<IngestResult> IngestAsync(Movie m, List<MagnetResult> magnets)
    {
        if (m.LibraryId is not long libId)
            throw new InvalidOperationException("入库需要一个目标媒体库 (LibraryId)。");

        // Resolve actor avatars up-front — these are network calls (MetaTube)
        // and must not run inside the write transaction below, or the DB lock
        // would be held across HTTP round-trips.
        var actorRows = new List<(string Name, string? Avatar)>();
        foreach (var a in m.Actors)
        {
            if (string.IsNullOrWhiteSpace(a.Name)) continue;
            var avatar = a.AvatarUrl;
            if (string.IsNullOrWhiteSpace(avatar))
            {
                try { avatar = (await _metaTube.GetActorAsync(a.Name))?.AvatarUrl; }
                catch { /* MetaTube optional — keep empty avatar */ }
            }
            actorRows.Add((a.Name, avatar));
        }

        await using var c = _db.Create();
        await c.OpenAsync();

        // One transaction for the upsert + association rebuild: without it, a
        // failure between "clear old actors/tags/magnets" and "re-insert" left
        // the movie stripped of its associations.
        await using var tx = c.BeginTransaction();

        // Upsert movie. INSERT ... ON CONFLICT ... RETURNING always yields the id.
        var prevJson = m.PreviewImages?.Any() == true
            ? System.Text.Json.JsonSerializer.Serialize(m.PreviewImages) : null;
        var movieId = await c.ExecuteScalarAsync<long>(@"
            INSERT INTO movies (library_id, number, title, original_title, summary, maker, label,
                                series, director, release_date, runtime_minutes, cover_url, thumb_url,
                                score, provider, homepage_url, preview_images)
            VALUES (@LibraryId,@Number,@Title,@OriginalTitle,@Summary,@Maker,@Label,@Series,@Director,
                    @ReleaseDate,@RuntimeMinutes,@CoverUrl,@ThumbUrl,@Score,@Provider,@HomepageUrl,@prevJson)
            ON CONFLICT(number, library_id) DO UPDATE SET
                title=excluded.title, original_title=excluded.original_title,
                summary=excluded.summary, maker=excluded.maker, label=excluded.label,
                series=excluded.series, director=excluded.director,
                release_date=excluded.release_date, runtime_minutes=excluded.runtime_minutes,
                cover_url=excluded.cover_url, thumb_url=excluded.thumb_url,
                score=excluded.score, provider=excluded.provider,
                homepage_url=excluded.homepage_url,
                preview_images=COALESCE(excluded.preview_images, movies.preview_images)
            RETURNING id;", new { m.LibraryId, m.Number, m.Title, m.OriginalTitle, m.Summary, m.Maker, m.Label, m.Series, m.Director, m.ReleaseDate, m.RuntimeMinutes, m.CoverUrl, m.ThumbUrl, m.Score, m.Provider, m.HomepageUrl, prevJson }, tx);

        // Clear old associations first (so rescrape replaces, not appends),
        // then re-insert — all inside the same transaction. Manually added
        // custom tags (and file-marker tags like 中文/无码修复/4K) survive the
        // rebuild: they don't come from MetaTube and would otherwise be wiped
        // on every rescrape.
        var keptTagIds = (await c.QueryAsync<long>(
            "SELECT mt.tag_id FROM movie_tags mt JOIN tags t ON t.id=mt.tag_id " +
            "WHERE mt.movie_id=@id AND t.category='custom' AND t.is_standard=0",
            new { id = movieId }, tx)).ToList();
        // Manually added actors survive rescrape too (MetaTube may return none).
        var keptManualActors = (await c.QueryAsync<long>(
            "SELECT actor_id FROM movie_actors WHERE movie_id=@id AND manual=1",
            new { id = movieId }, tx)).ToList();
        await c.ExecuteAsync("DELETE FROM movie_actors WHERE movie_id=@id AND manual=0", new { id = movieId }, tx);
        await c.ExecuteAsync("DELETE FROM movie_tags WHERE movie_id=@id", new { id = movieId }, tx);
        await c.ExecuteAsync("DELETE FROM magnets WHERE movie_id=@id", new { id = movieId }, tx);

        // Re-insert actors.
        var pendingAvatars = new List<(long ActorId, string Url)>();
        foreach (var (name, avatar) in actorRows)
        {
            await c.ExecuteAsync(
                "INSERT INTO actors(name, avatar_url) VALUES(@name,@avatar) ON CONFLICT(name) DO UPDATE SET avatar_url=COALESCE(@avatar, avatar_url)",
                new { name, avatar }, tx);
            var actorId = await c.ExecuteScalarAsync<long>(
                "SELECT id FROM actors WHERE name=@name", new { name }, tx);
            if (!string.IsNullOrWhiteSpace(avatar))
                pendingAvatars.Add((actorId, avatar));
            await c.ExecuteAsync(
                "INSERT OR IGNORE INTO movie_actors(movie_id, actor_id) VALUES(@m,@a)",
                new { m = movieId, a = actorId }, tx);
        }

        // Tags.
        foreach (var t in m.Tags)
        {
            if (string.IsNullOrWhiteSpace(t.Name)) continue;
            await c.ExecuteAsync(@"
                INSERT INTO tags(name, category, is_standard) VALUES(@name,@cat,@std)
                ON CONFLICT(name, category, is_standard) DO NOTHING",
                new { name = t.Name, cat = t.Category, std = t.IsStandard ? 1 : 0 }, tx);
            var tagId = await c.ExecuteScalarAsync<long>(
                "SELECT id FROM tags WHERE name=@name AND category=@cat AND is_standard=@std",
                new { name = t.Name, cat = t.Category, std = t.IsStandard ? 1 : 0 }, tx);
            await c.ExecuteAsync(
                "INSERT OR IGNORE INTO movie_tags(movie_id, tag_id) VALUES(@m,@t)",
                new { m = movieId, t = tagId }, tx);
        }

        // Re-link the preserved custom tags.
        foreach (var tid in keptTagIds)
            await c.ExecuteAsync(
                "INSERT OR IGNORE INTO movie_tags(movie_id, tag_id) VALUES(@m,@t)",
                new { m = movieId, t = tid }, tx);

        // Re-link the preserved manual actors.
        foreach (var aid in keptManualActors)
            await c.ExecuteAsync(
                "INSERT OR IGNORE INTO movie_actors(movie_id, actor_id, manual) VALUES(@m,@a,1)",
                new { m = movieId, a = aid }, tx);

        // Magnets.
        foreach (var mg in magnets.DistinctBy(x => x.MagnetUri))
        {
            await c.ExecuteAsync(@"
                INSERT INTO magnets(movie_id, query, title, size, magnet_uri, source)
                VALUES(@m,@q,@title,@size,@uri,@src)",
                new { m = movieId, q = m.Number, title = mg.Title, size = mg.Size, uri = mg.MagnetUri, src = mg.Source }, tx);
        }

        tx.Commit();

        // Prefetch actor avatars into the local cache now (outside the tx —
        // these are network downloads), so the actors page never depends on
        // the remote image host being reachable. Already-cached files skip.
        if (pendingAvatars.Count > 0)
        {
            var avatars = _sp.GetRequiredService<AvatarService>();
            foreach (var (actorId, url) in pendingAvatars)
            {
                if (File.Exists(avatars.LocalPathFor(actorId))) continue;
                try { await avatars.EnsureLocalAsync(actorId, url); }
                catch (Exception ex) { Serilog.Log.Warning(ex, "Avatar prefetch failed for actor {Id}", actorId); }
            }
        }

        // ---- File outputs: nfo / poster / thumb / magnet.txt ----
        string? folderPath = null;
        try
        {
            // Cloud-drive library (cache_local): all metadata files go to a
            // local cache folder under the app data dir — only the video file
            // itself stays on the remote drive.
            var cacheLocal = await c.ExecuteScalarAsync<long?>(
                "SELECT cache_local FROM libraries WHERE id=@id", new { id = libId }) == 1;
            var baseDir = cacheLocal
                ? Path.Combine(_db.DataDir, "cache")
                : await _libs.FirstAvailableDirectoryAsync(libId);
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                folderPath = Path.Combine(baseDir, SafeFolder(m.Number));
                Directory.CreateDirectory(folderPath);
                _nfo.Write(Path.Combine(folderPath, $"{m.Number}.nfo"), m, magnets);
                await _images.DownloadAsync(folderPath, m.Number, m.CoverUrl, m.ThumbUrl);
                ImageWriter.WriteMagnetTxt(Path.Combine(folderPath, "磁力链接.txt"), magnets, m.Number);
                await c.ExecuteAsync("UPDATE movies SET folder_path=@fp WHERE id=@id", new { fp = folderPath, id = movieId });

                // Optional: scrape + download a trailer (DMM free preview) when the
                // user has enabled it in Settings. Saved as {番号}-trailer.mp4 next
                // to the other files so the detail page can play it.
                var settings = _sp.GetRequiredService<SettingsService>();
                var on = (await settings.GetAsync(SettingsService.KeyScrapeTrailer))?.Trim();
                if (string.Equals(on, "true", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(on, "1", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var destTrailer = Path.Combine(folderPath, $"{m.Number}-trailer.mp4");
                        // Use case-insensitive lookup so a temp file saved as "BBI-168"
                        // is found even if MetaTube returns "bbi-168" etc.
                        var tempTrailer = TrailerClient.FindExistingTemp(m.Number);
                        if (tempTrailer != null)
                        {
                            File.Move(tempTrailer, destTrailer, overwrite: true);
                        }
                        else
                        {
                            var tc = _sp.GetRequiredService<TrailerClient>();
                            var trailerUrl = await tc.FindTrailerUrlAsync(m.Number);
                            if (!string.IsNullOrWhiteSpace(trailerUrl))
                            {
                                var bytes = await tc.DownloadAsync(trailerUrl);
                                if (bytes != null)
                                    await File.WriteAllBytesAsync(destTrailer, bytes);
                            }
                        }
                    }
                    catch (Exception ex) { Serilog.Log.Warning(ex, "Trailer download failed for {Number}", m.Number); }
                }
            }

            // Cache preview images locally — clear old cache first so rescrape
            // doesn't show stale images from a previous provider.
            var previewSvc = _sp.GetRequiredService<PreviewImageService>();
            try
            {
                var oldDir = previewSvc.DirFor(movieId);
                if (Directory.Exists(oldDir)) Directory.Delete(oldDir, recursive: true);
            }
            catch { /* best effort */ }
            if (m.PreviewImages?.Count > 0)
            {
                try { await previewSvc.CacheAsync(movieId, m.PreviewImages); }
                catch (Exception ex) { Serilog.Log.Warning(ex, "Preview cache failed for movie {Id}", movieId); }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "File output failed for {Number}", m.Number);
        }

        return new IngestResult(movieId, folderPath, true);
    }

    internal static string SafeFolder(string number)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(number.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }
}
