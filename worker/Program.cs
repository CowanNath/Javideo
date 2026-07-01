using System.Data;
using Dapper;
using Javideo.Worker.Db;
using Javideo.Worker.Endpoints;
using Javideo.Worker.Magnet;
using Javideo.Worker.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Serilog;

// ---- Logging ----
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

// Dapper type handler: SQL TEXT <-> List<string> (JSON array).
SqlMapper.AddTypeHandler(new StringListHandler());

try
{
    Log.Information("Javideo Worker starting...");

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // ---- CORS (allow the Tauri webview origin) ----
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

    // ---- Singletons ----
    builder.Services.AddSingleton<DbConnectionFactory>();
    builder.Services.AddSingleton<SettingsService>();
    builder.Services.AddSingleton<MetaTubeClient>();
    builder.Services.AddHttpClient<ImageWriter>();
    builder.Services.AddSingleton<MagnetService>();
    builder.Services.AddSingleton<Scanner>();
    builder.Services.AddSingleton<LibraryService>();
    builder.Services.AddSingleton<NfoWriter>();
    builder.Services.AddSingleton<IngestService>();
    builder.Services.AddSingleton<PlayerService>();
    builder.Services.AddSingleton<TrailerClient>();
    builder.Services.AddSingleton<BackupService>();
    builder.Services.AddHttpClient<AvatarService>();
    builder.Services.AddHttpClient<PreviewImageService>();
    builder.Services.AddSingleton<TranslationService>();

    // Force camelCase JSON for ALL endpoints (Dapper returns PascalCase property
    // names on anonymous/record results; without this the frontend's camelCase
    // types don't bind, causing the favorites "undefined" bug).
    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        o.SerializerOptions.PropertyNameCaseInsensitive = true;
    });

    // Bind to 127.0.0.1 on an OS-assigned port (port 0).
    builder.WebHost.ConfigureKestrel(opts =>
    {
        opts.Listen(System.Net.IPAddress.Loopback, 0);
    });

    var app = builder.Build();

    // ---- Migrate / init DB on startup ----
    await DbInitializer.InitializeAsync(app.Services.GetRequiredService<DbConnectionFactory>());

    // ---- CORS: allow the Tauri webview origin ----
    app.UseCors();

    // ---- Endpoints ----
    app.MapHealthEndpoints();
    app.MapLibraryEndpoints();
    app.MapMovieEndpoints();
    app.MapMetaTubeEndpoints();
    app.MapMagnetEndpoints();
    app.MapFavoriteEndpoints();
    app.MapActorEndpoints();
    app.MapTagEndpoints();
    app.MapScanEndpoints();
    app.MapSettingsEndpoints();
    app.MapBackupEndpoints();
    app.MapTranslateEndpoints();

    // ---- Emit the port handshake on stdout ----
    // Tauri's Rust layer reads this exact line to learn the worker URL.
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var addrs = server.Features.Get<IServerAddressesFeature>()!.Addresses;
            var first = addrs.First();
            if (System.Uri.TryCreate(first, System.UriKind.Absolute, out var u))
            {
                Console.Out.WriteLine($"JAVIDEO_WORKER_PORT={u.Port}");
                Console.Out.Flush();
                Log.Information("Worker listening on {Url}", first);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to emit port handshake");
        }
    });

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Javideo Worker terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

internal sealed class StringListHandler : SqlMapper.TypeHandler<List<string>>
{
    public override List<string> Parse(object value) =>
        value is string s && !string.IsNullOrEmpty(s)
            ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(s) ?? new()
            : new();

    public override void SetValue(IDbDataParameter parameter, List<string>? value) =>
        parameter.Value = value?.Any() == true
            ? System.Text.Json.JsonSerializer.Serialize(value)
            : DBNull.Value;
}
