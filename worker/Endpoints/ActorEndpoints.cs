using Dapper;
using Javideo.Worker.Db;
using Javideo.Worker.Models;
using Javideo.Worker.Services;

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
            var movies = await c.QueryAsync<Movie>(@"
                SELECT m.id Id, m.number Number, m.title Title, m.cover_url CoverUrl,
                       m.thumb_url ThumbUrl, m.release_date ReleaseDate
                FROM movies m
                JOIN movie_actors ma ON ma.movie_id=m.id
                WHERE ma.actor_id=@id
                ORDER BY m.release_date DESC", new { id });
            return Results.Ok(movies);
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
            var remoteAvatar = row.AvatarUrl;
            if (string.IsNullOrWhiteSpace(remoteAvatar) && !remoteAvatar!.StartsWith("/api/", StringComparison.Ordinal))
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
                       m.thumb_url ThumbUrl, m.release_date ReleaseDate
                FROM movies m
                JOIN movie_actors ma ON ma.movie_id=m.id
                WHERE ma.actor_id=@id
                ORDER BY m.release_date DESC", new { id })).ToList();

            return Results.Ok(new { actor = profile, name = row.Name, movies, configError });
        });
    }
}
