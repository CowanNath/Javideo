using Dapper;

namespace Javideo.Worker.Services;

/// <summary>
/// Thin key/value settings store backed by the settings table.
/// Also offers typed accessors for the well-known keys.
/// </summary>
public sealed class SettingsService
{
    private readonly Db.DbConnectionFactory _db;

    public const string KeyMetaTubeAddress = "metatube.address";
    public const string KeyMetaTubeTimeout = "metatube.timeoutMs";
    public const string KeyPlayerPath = "player.path";
    public const string KeyTheme = "ui.theme";
    public const string KeyLanguage = "ui.language";
    public const string KeyCardSize = "ui.cardSize";
    public const string KeyCloseBehavior = "ui.closeBehavior";
    public const string KeyDebug = "ui.debug";
    public const string KeyScrapeTrailer = "ui.scrapeTrailer";
    public const string KeyProxy = "network.proxy";
    public const string KeyLlmEndpoint = "llm.endpoint";
    public const string KeyLlmApiKey = "llm.apiKey";
    public const string KeyLlmModel = "llm.model";

    public SettingsService(Db.DbConnectionFactory db) => _db = db;

    public async Task<string?> GetAsync(string key)
    {
        await using var c = _db.Create();
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<string?>(
            "SELECT value FROM settings WHERE key=@k", new { k = key });
    }

    public async Task SetAsync(string key, string? value)
    {
        await using var c = _db.Create();
        await c.OpenAsync();
        await c.ExecuteAsync(
            "INSERT INTO settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=@v",
            new { k = key, v = value });
    }

    public async Task<Dictionary<string, string?>> GetAllAsync()
    {
        await using var c = _db.Create();
        await c.OpenAsync();
        var rows = await c.QueryAsync<(string key, string value)>(
            "SELECT key, value FROM settings");
        return rows.ToDictionary(r => r.key, r => (string?)r.value);
    }
}
