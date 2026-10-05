using Javideo.Worker.Models;
using Javideo.Worker.Services;
using Microsoft.AspNetCore.Http.Features;
using System.Text.Json;

namespace Javideo.Worker.Endpoints;

public static class HighlightEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapHighlightEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/movies/{movieId:long}/highlights").WithTags("Highlights");
        g.MapGet("/", async (long movieId, HighlightService service) =>
            await service.ListAsync(movieId) is { } records ? Results.Ok(records) : Results.NotFound());
        g.MapGet("/video-files", async (long movieId, HighlightService service) =>
            await service.VideoFilesAsync(movieId) is { } files ? Results.Ok(files) : Results.NotFound());
        g.MapPost("/", (long movieId, HttpContext ctx, HighlightService service) => SaveAsync(movieId, null, ctx, service));
        g.MapPut("/{id:long}", (long movieId, long id, HttpContext ctx, HighlightService service) => SaveAsync(movieId, id, ctx, service));
        g.MapDelete("/{id:long}", async (long movieId, long id, HighlightService service) =>
            await service.DeleteAsync(movieId, id) ? Results.NoContent() : Results.NotFound());
        g.MapGet("/{id:long}/assets/{assetId:long}", async (long movieId, long id, long assetId, HttpContext ctx, HighlightService service) =>
        {
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var asset = await service.GetAssetAsync(movieId, id, assetId);
            return asset is { } file ? Results.File(file.Path, file.ContentType, enableRangeProcessing: true) : Results.NotFound();
        });
        g.MapPost("/{id:long}/assets/{assetId:long}/play", async (long movieId, long id, long assetId, HighlightService service, PlayerService player) =>
        {
            var asset = await service.GetAssetAsync(movieId, id, assetId);
            if (asset is not { } file || !file.ContentType.StartsWith("video/")) return Results.NotFound();
            return Results.Ok(await player.PlayAsync(file.Path));
        });
    }

    private static async Task<IResult> SaveAsync(long movieId, long? id, HttpContext ctx, HighlightService service)
    {
        if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { detail = "请使用附件表单提交记录。" });
        // ponytail: native multipart streaming; no upload library or base64 copies.
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = HighlightService.MaxUploadBytes + 1024 * 1024;
        try
        {
            var form = await ctx.Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = HighlightService.MaxUploadBytes }, ctx.RequestAborted);
            var metadata = form["metadata"].ToString();
            if (metadata.Length > 20000) return Results.BadRequest(new { detail = "记录内容过长。" });
            var req = JsonSerializer.Deserialize<SaveHighlightRequest>(metadata, JsonOptions);
            if (req == null) return Results.BadRequest(new { detail = "缺少记录内容。" });
            var result = await service.SaveAsync(movieId, id, req, form.Files, ctx.RequestAborted);
            return result == null ? Results.NotFound() : Results.Ok(result);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or BadHttpRequestException)
        {
            return Results.BadRequest(new { detail = ex is JsonException ? "记录格式无效。" : ex.Message });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Error(ex, "Highlight save failed for {MovieId}", movieId);
            return Results.Json(new { detail = "保存附件失败，原记录已保留。请检查磁盘空间和访问权限。" }, statusCode: 500);
        }
    }
}
