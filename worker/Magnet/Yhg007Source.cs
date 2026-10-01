using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Javideo.Worker.Models;

namespace Javideo.Worker.Magnet;

/// <summary>
/// Magnet search via yhg007.com. The site redesigned: search results no longer
/// contain magnet links directly — each row links to /hash/{infohash}.html.
/// We extract the 40-char infohash from those links and build
/// magnet:?xt=urn:btih:{hash} ourselves (no need to fetch each detail page).
///
/// New structure per row:
///   article.zsky-result-row
///     a.zsky-result-name href=/hash/{hash}.html  → title (link text)
///     .zsky-result-meta                            → size info
/// </summary>
public sealed class Yhg007Source : HtmlMagnetSourceBase
{
    public override string Name => "yhg007.com";

    public Yhg007Source() { }

    protected override IEnumerable<string> SearchUrls(string query)
    {
        // Path segment must be escaped — raw " " or "&" breaks the URL.
        var orig = Uri.EscapeDataString(query.Trim());
        yield return $"https://yhg007.com/search-{orig}-0-0-1.html";
        var q = Uri.EscapeDataString(query);
        yield return $"https://yhg007.com/search?q={q}";
    }

    public override async Task<List<MagnetResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var results = new List<MagnetResult>();
        using var handler = CreateHandler();
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        http.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        string? html = null;
        foreach (var url in SearchUrls(query))
        {
            try
            {
                using var resp = await http.GetAsync(url, ct);
                Serilog.Log.Information("Magnet[{Name}]: {Url} -> {Status}", Name, url, (int)resp.StatusCode);
                if (!resp.IsSuccessStatusCode) continue;
                html = await resp.Content.ReadAsStringAsync(ct);
                // New design: rows contain /hash/{40-hex}.html links.
                if (html.Contains("/hash/", StringComparison.OrdinalIgnoreCase)) break;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning("Magnet[{Name}]: {Url} -> {Msg}", Name, url, ex.Message);
            }
        }
        if (string.IsNullOrEmpty(html)) return results;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Each result row has a title link: a[href*="/hash/"] with 40-hex hash.
        var hashLinks = doc.DocumentNode.SelectNodes("//a[contains(@href,'/hash/')]");
        if (hashLinks == null) return results;

        var seen = new HashSet<string>();
        foreach (var link in hashLinks)
        {
            var href = link.GetAttributeValue("href", "");
            var m = Regex.Match(href, @"/hash/([a-fA-F0-9]{40})");
            if (!m.Success) continue;
            var hash = m.Groups[1].Value;
            if (!seen.Add(hash)) continue;

            var title = System.Net.WebUtility.HtmlDecode(link.InnerText)?.Trim();
            if (string.IsNullOrWhiteSpace(title)) title = query;

            // Size: look in the row's meta div.
            var size = "";
            var row = link.Ancestors("article").FirstOrDefault();
            if (row != null)
            {
                var sizeText = System.Net.WebUtility.HtmlDecode(row.InnerText);
                var sm = Regex.Match(sizeText, @"\d+(?:\.\d+)?\s*(?:TB|GB|MB|KB)", RegexOptions.IgnoreCase);
                if (sm.Success) size = sm.Value;
            }

            results.Add(new MagnetResult
            {
                Title = title,
                MagnetUri = $"magnet:?xt=urn:btih:{hash}",
                Size = size,
                Source = Name,
            });
            if (results.Count >= 30) break;
        }
        Serilog.Log.Information("Magnet[{Name}]: extracted {Count} magnets", Name, results.Count);
        return results;
    }
}
