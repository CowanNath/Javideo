using Javideo.Worker.Models;
using Javideo.Worker.Services;

namespace Javideo.Worker.Magnet;

/// <summary>
/// Aggregates magnet sources and runs them concurrently. Reads the proxy
/// config from settings (magnet sites are typically unreachable without it)
/// and hands it to each source.
/// </summary>
public sealed class MagnetService
{
    private readonly IServiceProvider _sp;
    private readonly SettingsService _settings;
    public MagnetService(IServiceProvider sp, SettingsService settings)
    {
        _sp = sp;
        _settings = settings;
    }

    private async Task<(string? addr, string? user, string? pass)> GetProxyAsync()
    {
        try
        {
            var addr = (await _settings.GetAsync(SettingsService.KeyProxy))?.Trim();
            var user = (await _settings.GetAsync("network.proxyUser"))?.Trim();
            var pass = await _settings.GetAsync("network.proxyPass");
            return (addr, user, pass);
        }
        catch { return (null, null, null); }
    }

    public IMagnetSource[] CreateSources() => new IMagnetSource[]
    {
        ActivatorUtilities.CreateInstance<BtdigSource>(_sp),
        ActivatorUtilities.CreateInstance<Yhg007Source>(_sp),
        ActivatorUtilities.CreateInstance<NyaaSource>(_sp),
    };

    public async Task<List<MagnetResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var grouped = await SearchGroupedAsync(query, ct);
        return grouped.SelectMany(g => g.Results).ToList();
    }

    /// <summary>Search every source and return results grouped per source,
    /// so the frontend can show a tab per source with counts (even empty ones).</summary>
    public async Task<List<MagnetSourceResult>> SearchGroupedAsync(string query, CancellationToken ct = default)
    {
        var (proxyAddr, proxyUser, proxyPass) = await GetProxyAsync();
        HtmlMagnetSourceBase.ConfigureProxy(proxyAddr, proxyUser, proxyPass);

        var sources = CreateSources();
        var tasks = sources.Select(async s =>
        {
            try { return (source: s, results: await s.SearchAsync(query, ct)); }
            catch { return (source: s, results: new List<MagnetResult>()); }
        }).ToArray();
        await Task.WhenAll(tasks);

        return tasks.Select(t => t.Result).Select(r => new MagnetSourceResult
        {
            Source = r.source.Name,
            Count = r.results.Count,
            Results = r.results,
        }).ToList();
    }
}

public sealed class MagnetSourceResult
{
    public string Source { get; set; } = "";
    public int Count { get; set; }
    public List<MagnetResult> Results { get; set; } = new();
}
