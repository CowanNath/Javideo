using Javideo.Worker.Services;

namespace Javideo.Worker.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/settings").WithTags("Settings");

        g.MapGet("/", async (SettingsService svc) => Results.Ok(await svc.GetAllAsync()));

        g.MapGet("/{key}", async (string key, SettingsService svc) =>
            new { key, value = await svc.GetAsync(key) });

        // Accept a JSON body { "value": "..." } so the frontend can PUT a clean
        // payload without route/query binding ambiguity.
        g.MapPut("/{key}", async (string key, SettingValue body, SettingsService svc) =>
        {
            await svc.SetAsync(key, body.Value);
            return Results.Ok(new { key, value = body.Value });
        });
    }
}

public record SettingValue(string Value);
