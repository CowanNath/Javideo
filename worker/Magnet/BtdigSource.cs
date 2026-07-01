using Javideo.Worker.Models;

namespace Javideo.Worker.Magnet;

/// <summary>
/// Magnet search via btdig.com (DHT search engine). Search URL convention:
///   https://btdig.com/search?q={query}&amp;p={pageno}  (p is zero-based).
/// Results are HTML; extraction is handled by the base class.
/// </summary>
public sealed class BtdigSource : HtmlMagnetSourceBase
{
    public override string Name => "btdig.com";
    private const string BaseUrl = "https://btdig.com";

    public BtdigSource() { }

    protected override IEnumerable<string> SearchUrls(string query)
    {
        var q = Uri.EscapeDataString(query);
        // English-locale mirror first (often more stable), then the main domain.
        yield return $"https://en.btdig.com/search?q={q}&p=0";
        yield return $"{BaseUrl}/search?q={q}&p=0";
    }
}
