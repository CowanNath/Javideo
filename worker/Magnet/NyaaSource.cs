using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Javideo.Worker.Magnet;

/// <summary>
/// Magnet search via sukebei.nyaa.si. Ported from get_m3u8_url_js.
///
/// Search URL: https://sukebei.nyaa.si/?f=0&c=0_0&q={keyword}
/// Parsing: table rows (tr.default, tr.success), extract title/magnet/size from
/// td:nth-child(2)/td:nth-child(3)/td:nth-child(4).
/// </summary>
public sealed class NyaaSource : HtmlMagnetSourceBase
{
    public override string Name => "nyaa.si";

    public NyaaSource() { }

    protected override IEnumerable<string> SearchUrls(string query)
    {
        yield return $"https://sukebei.nyaa.si/?f=0&c=0_0&q={Uri.EscapeDataString(query)}";
    }

    /// <summary>Nyaa has a fixed table layout — override the base extraction to
    /// parse rows precisely rather than scanning for anchor tags.</summary>
    public new async Task<List<Models.MagnetResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var results = new List<Models.MagnetResult>();
        using var handler = new System.Net.Http.HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8,ja;q=0.7");
        http.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        string? html = null;
        foreach (var url in SearchUrls(query))
        {
            try
            {
                using var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
                html = await resp.Content.ReadAsStringAsync(ct);
                if (html.Contains("magnet:?", StringComparison.OrdinalIgnoreCase)) break;
            }
            catch { /* try next */ }
        }
        if (string.IsNullOrEmpty(html)) return results;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Nyaa results are in table rows: tr.default (normal) or tr.success (trusted).
        var rows = doc.DocumentNode.SelectNodes("//tr[contains(@class,'default') or contains(@class,'success')]");
        if (rows == null) return results;

        foreach (var row in rows)
        {
            try
            {
                // td:nth-child(2) > a[1] — title link with title attribute.
                var titleCell = row.SelectSingleNode("td[2]");
                var titleLink = titleCell?.SelectSingleNode("a");
                if (titleLink == null) continue;
                var title = titleLink.GetAttributeValue("title", "")
                            ?? System.Net.WebUtility.HtmlDecode(titleLink.InnerText)?.Trim();
                if (string.IsNullOrWhiteSpace(title)) continue;

                // td:nth-child(3) > a:last — magnet link (last <a> in cell 3).
                var magnetCell = row.SelectSingleNode("td[3]");
                var magnetLinks = magnetCell?.SelectNodes("a");
                if (magnetLinks == null || magnetLinks.Count == 0) continue;
                var href = magnetLinks[magnetLinks.Count - 1].GetAttributeValue("href", "");
                if (string.IsNullOrWhiteSpace(href) || !href.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
                    continue;

                // td:nth-child(4) — size.
                var sizeCell = row.SelectSingleNode("td[4]");
                var size = sizeCell != null
                    ? System.Net.WebUtility.HtmlDecode(sizeCell.InnerText)?.Trim() ?? ""
                    : "";

                results.Add(new Models.MagnetResult
                {
                    Title = title,
                    MagnetUri = href,
                    Size = size,
                    Source = Name,
                });

                if (results.Count >= 20) break;
            }
            catch { /* skip bad row */ }
        }
        return results;
    }
}
