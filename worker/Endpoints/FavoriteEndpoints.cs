using Dapper;
using Javideo.Worker.Db;

namespace Javideo.Worker.Endpoints;

public static class FavoriteEndpoints
{
    public static void MapFavoriteEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/favorites").WithTags("Favorites");

        // target_type: movie | tag | actor — resolves each favorite to a
        // human-readable name/number/cover so the UI can show something useful.
        // NOTE: map to a strong type (FavoriteRow) — DapperRow is a dict and the
        // JSON naming policy isn't applied to dict keys, which would leak the
        // PascalCase SQL aliases to the frontend as "TargetId" (mismatching the
        // camelCase TS type → "undefined").
        g.MapGet("/{targetType}", async (string targetType, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var t = targetType.ToLowerInvariant();
            string sql = t switch
            {
                "movie" => @"
                    SELECT f.id AS Id, f.target_type AS TargetType, f.target_id AS TargetId,
                           m.number AS Name, m.title AS Subtitle, m.cover_url AS Cover
                    FROM favorites f
                    LEFT JOIN movies m ON m.id = f.target_id
                    WHERE f.target_type='movie'
                    ORDER BY f.id DESC",
                "actor" => @"
                    SELECT f.id AS Id, f.target_type AS TargetType, f.target_id AS TargetId,
                           a.name AS Name, a.avatar_url AS Cover
                    FROM favorites f
                    LEFT JOIN actors a ON a.id = f.target_id
                    WHERE f.target_type='actor'
                    ORDER BY f.id DESC",
                "tag" => @"
                    SELECT f.id AS Id, f.target_type AS TargetType, f.target_id AS TargetId,
                           t.name AS Name
                    FROM favorites f
                    LEFT JOIN tags t ON t.id = f.target_id
                    WHERE f.target_type='tag'
                    ORDER BY f.id DESC",
                _ => "SELECT id AS Id, target_type AS TargetType, target_id AS TargetId FROM favorites WHERE target_type=@t",
            };
            object param = t is "movie" or "actor" or "tag" ? new { } : new { t };
            var rows = (await c.QueryAsync<FavoriteRow>(sql, param)).ToList();
            return Results.Ok(rows);
        });

        // Just the favorited target IDs for a type — cheap to fetch and lets the
        // UI mark cards/heart icons with real state.
        g.MapGet("/{targetType}/ids", async (string targetType, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var ids = await c.QueryAsync<long>(
                "SELECT target_id FROM favorites WHERE target_type=@t",
                new { t = targetType });
            return Results.Ok(ids);
        });

        g.MapPost("/", async (FavoriteDto dto, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            await c.ExecuteAsync(
                "INSERT OR IGNORE INTO favorites(target_type, target_id) VALUES (@t, @id)",
                new { t = dto.TargetType, id = dto.TargetId });
            return Results.Ok();
        });

        g.MapDelete("/{targetType}/{targetId:long}", async (string targetType, long targetId, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            await c.ExecuteAsync(
                "DELETE FROM favorites WHERE target_type=@t AND target_id=@id",
                new { t = targetType, id = targetId });
            return Results.NoContent();
        });

        // Batch unfavorite.
        g.MapPost("/batch-remove", async (BatchFavoriteDto dto, DbConnectionFactory db) =>
        {
            // Drop any non-positive ids (null/0 that slipped through from the client).
            var ids = dto.TargetIds.Where(x => x > 0).ToList();
            if (ids.Count == 0) return Results.Ok(new { removed = 0 });
            await using var c = db.Create();
            await c.OpenAsync();
            await c.ExecuteAsync(
                "DELETE FROM favorites WHERE target_type=@t AND target_id IN @ids",
                new { t = dto.TargetType, ids });
            return Results.Ok(new { removed = ids.Count });
        });
    }
}

public record FavoriteDto(string TargetType, long TargetId);

// Strong type so JSON serialization applies the camelCase naming policy.
// (DapperRow is a dictionary and would leak PascalCase SQL aliases.)
public sealed class FavoriteRow
{
    public long Id { get; set; }
    public string? TargetType { get; set; }
    public long TargetId { get; set; }
    public string? Name { get; set; }
    public string? Subtitle { get; set; }
    public string? Cover { get; set; }
}
// TargetIds defaults to empty so a missing/null array doesn't 400. Accept both
// camelCase (ASP.NET default) and PascalCase (what the client historically sent).
public record BatchFavoriteDto
{
    public string TargetType { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("targetIds")]
    public long[] TargetIds { get; init; } = Array.Empty<long>();
}
