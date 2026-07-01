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

        await using var c = _db.Create();
        await c.OpenAsync();

        // Upsert movie.
        var prevJson = m.PreviewImages?.Any() == true
            ? System.Text.Json.JsonSerializer.Serialize(m.PreviewImages) : null;
        var movieId = await c.ExecuteScalarAsync<long?>(@"
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
            RETURNING id;", new { m.LibraryId, m.Number, m.Title, m.OriginalTitle, m.Summary, m.Maker, m.Label, m.Series, m.Director, m.ReleaseDate, m.RuntimeMinutes, m.CoverUrl, m.ThumbUrl, m.Score, m.Provider, m.HomepageUrl, prevJson });
        if (movieId is null)
            movieId = await c.ExecuteScalarAsync<long>(
                "SELECT id FROM movies WHERE number=@Number AND library_id=@LibraryId", m);

        // Actors — clear old associations first (so rescrape replaces, not appends),
        // then re-insert. Tags and magnets are also cleared for a clean update.
        await c.ExecuteAsync("DELETE FROM movie_actors WHERE movie_id=@id", new { id = movieId });
        await c.ExecuteAsync("DELETE FROM movie_tags WHERE movie_id=@id", new { id = movieId });
        await c.ExecuteAsync("DELETE FROM magnets WHERE movie_id=@id", new { id = movieId });

        // Re-insert actors — resolve avatar via MetaTube when none is present.
        foreach (var a in m.Actors)
        {
            if (string.IsNullOrWhiteSpace(a.Name)) continue;
            var avatar = a.AvatarUrl;
            if (string.IsNullOrWhiteSpace(avatar))
            {
                try { avatar = (await _metaTube.GetActorAsync(a.Name))?.AvatarUrl; }
                catch { /* MetaTube optional — keep empty avatar */ }
            }
            await c.ExecuteAsync(
                "INSERT INTO actors(name, avatar_url) VALUES(@name,@avatar) ON CONFLICT(name) DO UPDATE SET avatar_url=COALESCE(@avatar, avatar_url)",
                new { name = a.Name, avatar });
            var actorId = await c.ExecuteScalarAsync<long>(
                "SELECT id FROM actors WHERE name=@name", new { name = a.Name });
            await c.ExecuteAsync(
                "INSERT OR IGNORE INTO movie_actors(movie_id, actor_id) VALUES(@m,@a)",
                new { m = movieId, a = actorId });
        }

        // Tags.
        foreach (var t in m.Tags)
        {
            if (string.IsNullOrWhiteSpace(t.Name)) continue;
            await c.ExecuteAsync(@"
                INSERT INTO tags(name, category, is_standard) VALUES(@name,@cat,@std)
                ON CONFLICT(name, category, is_standard) DO NOTHING",
                new { name = t.Name, cat = t.Category, std = t.IsStandard ? 1 : 0 });
            var tagId = await c.ExecuteScalarAsync<long>(
                "SELECT id FROM tags WHERE name=@name AND category=@cat AND is_standard=@std",
                new { name = t.Name, cat = t.Category, std = t.IsStandard ? 1 : 0 });
            await c.ExecuteAsync(
                "INSERT OR IGNORE INTO movie_tags(movie_id, tag_id) VALUES(@m,@t)",
                new { m = movieId, t = tagId });
        }

        // Magnets.
        foreach (var mg in magnets.DistinctBy(x => x.MagnetUri))
        {
            await c.ExecuteAsync(@"
                INSERT INTO magnets(movie_id, query, title, size, magnet_uri, source)
                VALUES(@m,@q,@title,@size,@uri,@src)",
                new { m = movieId, q = m.Number, title = mg.Title, size = mg.Size, uri = mg.MagnetUri, src = mg.Source });
        }

        // ---- File outputs: nfo / poster / thumb / magnet.txt ----
        string? folderPath = null;
        try
        {
            var baseDir = await _libs.FirstAvailableDirectoryAsync(libId);
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
                var oldDir = previewSvc.DirFor(movieId.Value);
                if (Directory.Exists(oldDir)) Directory.Delete(oldDir, recursive: true);
            }
            catch { /* best effort */ }
            if (m.PreviewImages?.Count > 0)
            {
                try { await previewSvc.CacheAsync(movieId.Value, m.PreviewImages); }
                catch (Exception ex) { Serilog.Log.Warning(ex, "Preview cache failed for movie {Id}", movieId); }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "File output failed for {Number}", m.Number);
        }

        return new IngestResult(movieId.Value, folderPath, true);
    }

    private static string SafeFolder(string number)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(number.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }
}
