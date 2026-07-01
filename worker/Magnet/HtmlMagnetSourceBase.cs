using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Javideo.Worker.Models;

namespace Javideo.Worker.Magnet;

/// <summary>
/// Shared base for magnet sources that serve HTML and require scraping.
/// Centralizes HTTP setup, magnet-link extraction and defensive error handling
/// so each concrete source only declares its search URL conventions.
///
/// Each source owns a private HttpClient (created per source) — we must NOT
/// share one across sources, because setting DefaultRequestHeaders/Timeout on
/// a shared instance after its first request throws
/// "Properties can only be modified before sending the first request."
/// </summary>
public abstract class HtmlMagnetSourceBase : IMagnetSource
{
    public abstract string Name { get; }

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    // Each source instance gets its own handler+client so concurrent sources
    // never share mutable state.
    private static readonly HttpClientHandler Handler = new()
    {
        // Some magnet sites misbehave; be lenient.
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    };

    // Match the full magnet URI (run against HTML-decoded text so &amp; is &):
    // scheme + btih hash + everything up to the next whitespace/quote/bracket.
    private static readonly Regex MagnetRegex =
        new(@"magnet:\?xt=urn:btih:[a-zA-Z0-9]{16,}[^""'<>\s]*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    protected HtmlMagnetSourceBase() { }

    /// <summary>Candidate search URLs to try, in order. First that yields
    /// magnet links wins.</summary>
    protected abstract IEnumerable<string> SearchUrls(string query);

    public async Task<List<MagnetResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var results = new List<MagnetResult>();
        using var http = new HttpClient(Handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        // Cloudflare-protected sites (pollack3.sbs etc.) reject bare requests,
        // so send a full browser-like header set.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        http.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");

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
            catch
            {
                /* try next url */
            }
        }
        if (string.IsNullOrEmpty(html)) return results;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // 1) Anchor-based: <a href="magnet:?..."> with nearby text for title/size.
        var anchors = doc.DocumentNode.SelectNodes("//a[contains(@href,'magnet:?')]");
        if (anchors != null)
        {
            foreach (var a in anchors)
            {
                // Decode HTML entities (&amp; → &) so the magnet URI is well-formed
                // and the dn= parameter survives intact (needed for the real title).
                var href = System.Net.WebUtility.HtmlDecode(a.GetAttributeValue("href", ""));
                var magnet = MagnetRegex.Match(href);
                if (!magnet.Success) continue;
                results.Add(new MagnetResult
                {
                    MagnetUri = magnet.Value,
                    Title = ResolveTitle(magnet.Value, a, doc) ?? query,
                    Size = BestSize(a),
                    Source = Name,
                });
            }
        }

        // 2) Fallback: any magnet substring in the raw HTML (decode entities first).
        if (results.Count == 0)
        {
            var decoded = System.Net.WebUtility.HtmlDecode(html);
            foreach (Match m in MagnetRegex.Matches(decoded))
                results.Add(new MagnetResult { MagnetUri = m.Value, Title = ExtractDn(m.Value) ?? query, Source = Name });
        }
        return results;
    }

    /// <summary>Pick the best available title for a result. Virtual so a source
    /// with a known DOM structure (e.g. yhg007's h3>a>div) can target the title
    /// node precisely instead of walking parents. Priority:
    ///   1. the magnet's own `dn=` (display name) parameter — the torrent's real name;
    ///   2. the anchor's title attribute;
    ///   3. surrounding text, skipping short CTA/button labels like "Download".</summary>
    protected virtual string? ResolveTitle(string magnetUri, HtmlNode a, HtmlDocument doc)
    {
        var dn = ExtractDn(magnetUri);
        if (!string.IsNullOrWhiteSpace(dn) && IsLikelyTitle(dn)) return dn;

        var fromAttr = CleanText(a.GetAttributeValue("title", ""));
        if (IsLikelyTitle(fromAttr)) return fromAttr;

        // Walk up looking for a substantial text node (skip CTA buttons).
        HtmlNode? node = a;
        for (int depth = 0; depth < 5 && node != null; depth++)
        {
            var t = CleanText(node.InnerText);
            if (IsLikelyTitle(t))
            {
                // For sites where the row bundles "Name <size> magnet", strip a
                // trailing size so the title stays clean.
                t = Regex.Replace(t, @"\s*\d+(?:\.\d+)?\s*(?:TB|GB|MB|KB)\s*$", "", RegexOptions.IgnoreCase);
                return t;
            }
            node = node.ParentNode;
        }
        return null;
    }

    /// <summary>Extract the `dn=` display-name from a magnet URI (URL-decoded).</summary>
    protected static string? ExtractDn(string magnetUri)
    {
        var m = Regex.Match(magnetUri, @"[?&]dn=([^&]+)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        try { return Uri.UnescapeDataString(m.Groups[1].Value); }
        catch { return m.Groups[1].Value; }
    }

    /// <summary>A text is a plausible title only if it's long enough and doesn't
    /// look like a download button caption.</summary>
    protected static bool IsLikelyTitle(string? t)
    {
        if (string.IsNullOrWhiteSpace(t) || t.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            return false;
        if (t.Length < 8) return false; // too short → likely a button label
        var lower = t.ToLowerInvariant();
        if (lower.Contains("download") || lower.Contains("magnet-link") || lower.Contains("点击")
            || lower == "magnet link" || lower.Contains("btdig") || lower.Contains("yhg007")
            || lower.Contains("search") || lower.Contains("home"))
            return false;
        return true;
    }

    /// <summary>Heuristic size extraction — scan a wider ancestor window (the
    /// torrent row) for a "123.4 GB/MB" pattern. Virtual so a source with a
    /// known DOM (yhg007) can target the size precisely.</summary>
    protected virtual string BestSize(HtmlNode a)
    {
        HtmlNode? node = a;
        for (int depth = 0; depth < 6 && node != null; depth++)
        {
            var text = CleanText(node.InnerText) ?? "";
            var m = Regex.Match(text, @"\b\d+(?:\.\d+)?\s*(?:TB|GB|MB|KB)\b", RegexOptions.IgnoreCase);
            if (m.Success) return m.Value;
            node = node.ParentNode;
        }
        return "";
    }

    protected static string? CleanText(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : System.Net.WebUtility.HtmlDecode(s).Trim();
}
