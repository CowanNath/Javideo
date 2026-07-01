namespace Javideo.Worker.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", () => new { ok = true, name = "javideo-worker", time = DateTime.UtcNow })
           .WithTags("Health");
    }
}
