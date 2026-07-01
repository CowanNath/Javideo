using Dapper;
using Javideo.Worker.Db;
using Javideo.Worker.Models;

namespace Javideo.Worker.Endpoints;

public static class TagEndpoints
{
    public static void MapTagEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/tags").WithTags("Tags");

        // category: genre | series | maker | custom
        // standard: true=标准库, false=非标准(自定义)
        g.MapGet("/", async (DbConnectionFactory db, string? category, bool? standard) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var sql = @"
                SELECT t.id Id, t.name Name, t.category Category, t.is_standard IsStandard,
                       (SELECT COUNT(*) FROM movie_tags mt WHERE mt.tag_id=t.id) MovieCount
                FROM tags t WHERE 1=1";
            if (!string.IsNullOrWhiteSpace(category))
                sql += " AND t.category=@category";
            if (standard.HasValue)
                sql += " AND t.is_standard=@std";
            sql += " ORDER BY MovieCount DESC";
            var rows = await c.QueryAsync<Tag>(sql, new { category, std = standard.HasValue ? (standard.Value ? 1 : 0) : (int?)null });
            return Results.Ok(rows);
        });

        // Movies by tag.
        g.MapGet("/{id:long}/movies", async (long id, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var movies = await c.QueryAsync<Movie>(@"
                SELECT m.id Id, m.number Number, m.title Title, m.cover_url CoverUrl,
                       m.thumb_url ThumbUrl, m.release_date ReleaseDate
                FROM movies m
                JOIN movie_tags mt ON mt.movie_id=m.id
                WHERE mt.tag_id=@id
                ORDER BY m.release_date DESC", new { id });
            return Results.Ok(movies);
        });

        // Tag info (name + category) so detail pages can show the real title.
        g.MapGet("/{id:long}/info", async (long id, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var tag = await c.QueryFirstOrDefaultAsync<Tag>(@"
                SELECT id Id, name Name, category Category, is_standard IsStandard,
                       (SELECT COUNT(*) FROM movie_tags mt WHERE mt.tag_id=t.id) MovieCount
                FROM tags t WHERE id=@id", new { id });
            return tag == null ? Results.NotFound() : Results.Ok(tag);
        });

        // Rename a tag (custom tags only).
        g.MapPut("/{id:long}", async (long id, RenameTagRequest req, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var tag = await c.QueryFirstOrDefaultAsync<Tag>(
                "SELECT id Id, name Name, category Category, is_standard IsStandard FROM tags WHERE id=@id", new { id });
            if (tag == null)
                return Results.NotFound(new { ok = false, detail = "标签不存在" });
            if (tag.IsStandard && tag.Category != "custom")
                return Results.BadRequest(new { ok = false, detail = "标准标签不可编辑" });
            await c.ExecuteAsync("UPDATE tags SET name=@name WHERE id=@id", new { id, name = req.Name.Trim() });
            return Results.Ok(new { ok = true, detail = "标签已更新" });
        });
    }
}

public record RenameTagRequest(string Name);
