using Javideo.Worker.Models;

namespace Javideo.Worker.Magnet;

/// <summary>
/// A magnet-link search source. All three listed sources (pollack3.sbs,
/// btdig.com, yhg007.com) return HTML rather than JSON, so each implementation
/// parses its own page. yhg007/btdig are stubbed for the first release.
/// </summary>
public interface IMagnetSource
{
    string Name { get; }
    Task<List<MagnetResult>> SearchAsync(string query, CancellationToken ct = default);
}
