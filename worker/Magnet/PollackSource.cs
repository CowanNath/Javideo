using Javideo.Worker.Models;

namespace Javideo.Worker.Magnet;

/// <summary>
/// Magnet search via pollack3.sbs. The observed search URL pattern is:
///   https://pollack3.sbs/search/{query}_ctime_1.html
/// where {query} is the raw number (e.g. "abs-082"). Results are HTML;
/// extraction is handled by <see cref="HtmlMagnetSourceBase"/>.
/// </summary>
public sealed class PollackSource : HtmlMagnetSourceBase
{
    public override string Name => "pollack3.sbs";
    private const string BaseUrl = "https://pollack3.sbs";

    public PollackSource() { }

    protected override IEnumerable<string> SearchUrls(string query)
    {
        // Verified live URL is LOWERCASE: /search/snos-025_ctime_1.html
        var raw = query.Trim().ToLowerInvariant();
        var orig = query.Trim();
        var q = Uri.EscapeDataString(query);
        // Primary observed pattern (lowercase 番号).
        yield return $"{BaseUrl}/search/{raw}_ctime_1.html";
        // Other sorting suffixes the site may accept.
        yield return $"{BaseUrl}/search/{raw}_1.html";
        yield return $"{BaseUrl}/search/{raw}.html";
        // Original-case + query-string fallbacks.
        yield return $"{BaseUrl}/search/{orig}_ctime_1.html";
        yield return $"{BaseUrl}/search?q={q}";
        yield return $"{BaseUrl}/?q={q}";
    }
}
