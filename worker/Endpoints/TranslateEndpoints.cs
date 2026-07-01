using Javideo.Worker.Models;
using Javideo.Worker.Services;

namespace Javideo.Worker.Endpoints;

public static class TranslateEndpoints
{
    public static void MapTranslateEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/translate").WithTags("Translate");

        g.MapPost("/", async (TranslateRequest req, TranslationService ts) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title) && string.IsNullOrWhiteSpace(req.Summary))
                return Results.Ok(new { });

            var movie = new Movie { Title = req.Title, Summary = req.Summary };
            var result = await ts.TranslateAsync(movie);
            return Results.Ok(new { title = result?.Title, summary = result?.Summary });
        });

        g.MapPost("/test", async (TranslationService ts) =>
        {
            var (ok, detail) = await ts.TestConnectionAsync();
            return Results.Ok(new { ok, detail });
        });
    }
}

public sealed class TranslateRequest
{
    public string? Title { get; set; }
    public string? Summary { get; set; }
}
