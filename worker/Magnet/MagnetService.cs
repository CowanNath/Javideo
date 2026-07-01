using Javideo.Worker.Models;

namespace Javideo.Worker.Magnet;

/// <summary>
/// Aggregates magnet sources and runs them concurrently. Each source is given
/// a fresh HttpClient (created per-call) so concurrent sources never share
/// mutable request state.
/// </summary>
public sealed class MagnetService
{
    private readonly IServiceProvider _sp;
    public MagnetService(IServiceProvider sp) => _sp = sp;

    /// <summary>The configured sources, in the order tabs should appear.
    /// (pollack3.sbs dropped — it's behind Cloudflare's JS challenge, which a
    /// plain HttpClient can't solve, so it always returned empty.)</summary>
    public IMagnetSource[] CreateSources() => new IMagnetSource[]
    {
        ActivatorUtilities.CreateInstance<BtdigSource>(_sp),
        ActivatorUtilities.CreateInstance<Yhg007Source>(_sp),
        ActivatorUtilities.CreateInstance<NyaaSource>(_sp),
    };

    public async Task<List<MagnetResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var sources = CreateSources();
        var tasks = sources.Select(s => s.SearchAsync(query, ct)).ToArray();
        try { await Task.WhenAll(tasks); } catch { /* collect whichever finished */ }

        var all = new List<MagnetResult>();
        foreach (var t in tasks)
        {
            try { all.AddRange(t.Result); } catch { /* ignore individual failures */ }
        }

        // De-dup by infohash.
        return all
            .GroupBy(r => r.MagnetUri)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>Search every source and return results grouped per source,
    /// so the frontend can show a tab per source with counts (even empty ones).</summary>
    public async Task<List<MagnetSourceResult>> SearchGroupedAsync(string query, CancellationToken ct = default)
    {
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
