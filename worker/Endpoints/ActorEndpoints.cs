using Dapper;
using Javideo.Worker.Db;
using Javideo.Worker.Models;
using Javideo.Worker.Services;
using Microsoft.Data.Sqlite;

namespace Javideo.Worker.Endpoints;

public static class ActorEndpoints
{
    public static void MapActorEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/actors").WithTags("Actors");

        // All actors with movie counts. avatars are served from a local cache,
        // so the avatar_url is the static endpoint path (no upstream dependency).
        g.MapGet("/", async (DbConnectionFactory db, string? q) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var sql = @"
                SELECT a.id Id, a.name Name,
                       (SELECT COUNT(*) FROM movie_actors ma WHERE ma.actor_id=a.id) MovieCount
                FROM actors a";
            if (!string.IsNullOrWhiteSpace(q))
                sql += " WHERE a.name LIKE @q";
            sql += " ORDER BY MovieCount DESC";
            // avatar_url points at the local file endpoint (added below).
            var rows = (await c.QueryAsync<(long Id, string Name, long MovieCount)>(
                sql, new { q = $"%{q}%" }))
                .Select(r => new Actor
                {
                    Id = r.Id,
                    Name = r.Name,
                    MovieCount = r.MovieCount,
                    AvatarUrl = $"/api/actors/{r.Id}/avatar",
                });
            return Results.Ok(rows);
        });

        // Serve the cached avatar file from disk. If it isn't cached yet, lazily
        // download it from the actor's stored remote URL (so the actors list —
        // which doesn't open the detail page — still gets avatars on demand).
        g.MapGet("/{id:long}/avatar", async (long id, AvatarService avatars, DbConnectionFactory db, MetaTubeClient mt) =>
        {
            var path = avatars.LocalPathFor(id);
            if (!File.Exists(path))
            {
                await using var c = db.Create();
                await c.OpenAsync();
                var remote = await c.ExecuteScalarAsync<string?>(
                    "SELECT avatar_url FROM actors WHERE id=@id", new { id });
                // Resolve via MetaTube if no remote URL stored yet.
                if (string.IsNullOrWhiteSpace(remote))
                {
                    try { remote = (await mt.GetActorAsync(
                        await c.ExecuteScalarAsync<string?>("SELECT name FROM actors WHERE id=@id", new { id }) ?? ""))?.AvatarUrl; }
                    catch { remote = null; }
                }
                await avatars.EnsureLocalAsync(id, remote);
            }
            return File.Exists(path)
                ? Results.File(path, "image/jpeg")
                : Results.NotFound();
        });

        // Movies by actor (the DB side).
        g.MapGet("/{id:long}/movies", async (long id, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var movies = (await c.QueryAsync<Movie>(@"
                SELECT m.id Id, m.number Number, m.title Title, m.cover_url CoverUrl,
                       m.thumb_url ThumbUrl, m.release_date ReleaseDate, m.folder_path FolderPath,
                       m.source_path SourcePath
                FROM movies m
                JOIN movie_actors ma ON ma.movie_id=m.id
                WHERE ma.actor_id=@id
                ORDER BY m.release_date DESC", new { id })).ToList();
            // Route covers through the local image endpoint (serves the
            // ingested poster/thumb file, proxying remote only as fallback) —
            // raw DB URLs die whenever the MetaTube server is unreachable.
            foreach (var m in movies)
            {
                if (!string.IsNullOrWhiteSpace(m.CoverUrl))
                    m.CoverUrl = $"/api/movies/{m.Id}/image/poster";
                if (!string.IsNullOrWhiteSpace(m.ThumbUrl))
                    m.ThumbUrl = $"/api/movies/{m.Id}/image/thumb";
                // Card badges: video file present / trailer downloaded.
                m.HasVideo = MovieEndpoints.HasVideoFile(m.FolderPath, m.Number ?? "", m.SourcePath);
                m.HasTrailer = !string.IsNullOrWhiteSpace(m.FolderPath)
                    && File.Exists(Path.Combine(m.FolderPath, $"{m.Number}-trailer.mp4"));
            }
            return Results.Ok(movies);
        });

        // Resolve avatar endpoints for a batch of actor names — used by the
        // search page BEFORE ingest (scraped cast has no avatars of its own).
        // Known actors reuse their local avatar endpoint; unknown names are
        // looked up via MetaTube and inserted so the avatar lazy-downloads on
        // first display.
        g.MapPost("/resolve", async (ResolveAvatarsRequest req, DbConnectionFactory db, MetaTubeClient mt) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var result = new Dictionary<string, string?>();
            foreach (var name in req.Names ?? new())
            {
                if (string.IsNullOrWhiteSpace(name) || result.ContainsKey(name)) continue;
                var id = await c.ExecuteScalarAsync<long?>("SELECT id FROM actors WHERE name=@name", new { name });
                if (id == null)
                {
                    string? remote = null;
                    try { remote = (await mt.GetActorAsync(name))?.AvatarUrl; }
                    catch { /* MetaTube optional */ }
                    if (string.IsNullOrWhiteSpace(remote)) { result[name] = null; continue; }
                    id = await c.ExecuteScalarAsync<long>(
                        "INSERT INTO actors(name, avatar_url) VALUES(@name,@avatar) " +
                        "ON CONFLICT(name) DO UPDATE SET avatar_url=COALESCE(@avatar, avatar_url) RETURNING id",
                        new { name, avatar = remote });
                }
                result[name] = $"/api/actors/{id}/avatar";
            }
            return Results.Ok(result);
        });

        // Rename an actor (global — the actor is a shared entity).
        g.MapPut("/{id:long}", async (long id, RenameActorRequest req, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var exists = await c.ExecuteScalarAsync<long?>("SELECT id FROM actors WHERE id=@id", new { id });
            if (exists == null)
                return Results.NotFound(new { ok = false, detail = "演员不存在" });
            var name = req.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { ok = false, detail = "演员名不能为空" });
            try
            {
                await c.ExecuteAsync("UPDATE actors SET name=@name WHERE id=@id", new { id, name });
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // UNIQUE name
            {
                return Results.Conflict(new { ok = false, detail = "同名演员已存在" });
            }
            return Results.Ok(new { ok = true, detail = "演员已更新" });
        });

        // Full actor detail: MetaTube profile (bio) + cached avatar + this
        // actor's ingested movies. Avatar is downloaded to disk on first
        // request and served locally thereafter.
        g.MapGet("/{id:long}/detail", async (long id, DbConnectionFactory db, MetaTubeClient mt, AvatarService avatars) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var row = await c.QueryFirstOrDefaultAsync<(string Name, string? AvatarUrl)>(
                "SELECT name Name, avatar_url AvatarUrl FROM actors WHERE id=@id", new { id });
            if (row.Name == null)
                return Results.NotFound(new { ok = false, detail = "演员不存在" });

            // Ensure the avatar is cached locally (uses the stored remote URL).
            // Persist the remote URL if we don't have it yet (resolve via MetaTube).
            // A stored "/api/..." path is our own cache endpoint — the remote URL
            // must be re-resolved from MetaTube.
            var remoteAvatar = row.AvatarUrl;
            if (string.IsNullOrWhiteSpace(remoteAvatar) || remoteAvatar.StartsWith("/api/", StringComparison.Ordinal))
            {
                try { remoteAvatar = (await mt.GetActorAsync(row.Name))?.AvatarUrl; }
                catch { /* MetaTube optional */ }
            }
            await avatars.EnsureLocalAsync(id, remoteAvatar);

            // Build the profile from MetaTube (bio fields), but force the avatar
            // to the local endpoint so it works offline.
            ActorDetail? profile = null;
            string? configError = null;
            try
            {
                profile = await mt.GetActorAsync(row.Name);
            }
            catch (MetaTubeNotConfiguredException)
            {
                configError = "未配置 MetaTube 服务地址,无法获取演员资料(仅显示本地作品)";
            }
            catch (Exception ex)
            {
                configError = $"获取演员资料失败: {ex.Message}";
            }
            if (profile != null)
                profile.AvatarUrl = $"/api/actors/{id}/avatar";
            else
                profile = new ActorDetail { Name = row.Name, AvatarUrl = $"/api/actors/{id}/avatar" };

            var movies = (await c.QueryAsync<Movie>(@"
                SELECT m.id Id, m.number Number, m.title Title, m.cover_url CoverUrl,
                       m.thumb_url ThumbUrl, m.release_date ReleaseDate, m.folder_path FolderPath,
                       m.source_path SourcePath
                FROM movies m
                JOIN movie_actors ma ON ma.movie_id=m.id
                WHERE ma.actor_id=@id
                ORDER BY m.release_date DESC", new { id })).ToList();
            // This is the query the actor DETAIL page renders — route covers
            // through the local image endpoint and compute the card badges
            // (video / trailer), same as the /movies endpoint.
            foreach (var m in movies)
            {
                if (!string.IsNullOrWhiteSpace(m.CoverUrl))
                    m.CoverUrl = $"/api/movies/{m.Id}/image/poster";
                if (!string.IsNullOrWhiteSpace(m.ThumbUrl))
                    m.ThumbUrl = $"/api/movies/{m.Id}/image/thumb";
                m.HasVideo = MovieEndpoints.HasVideoFile(m.FolderPath, m.Number ?? "", m.SourcePath);
                m.HasTrailer = !string.IsNullOrWhiteSpace(m.FolderPath)
                    && File.Exists(Path.Combine(m.FolderPath, $"{m.Number}-trailer.mp4"));
            }

            return Results.Ok(new { actor = profile, name = row.Name, movies, configError });
        });
    }
}

public record RenameActorRequest(string Name);

public record ResolveAvatarsRequest(List<string>? Names);
