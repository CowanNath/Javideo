using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Javideo.Worker.Magnet;

/// <summary>
/// Magnet search via yhg007.com. The observed search URL pattern is:
///   https://yhg007.com/search-{query}-0-0-1.html
///
/// Each result is a .ssbox block with this layout:
///   .title > h3 > a            ← TITLE (番号 + 后缀, e.g. "SNOS-025-uncensored-HD")
///   .slist > ul > li ...        ← contained files (we ignore these)
///   .sbar > a[href=magnet:?..]  ← the magnet link
///   .sbar text "大小:<b>5.4 GB</b>" ← total size
/// The magnet anchor is a SIBLING of .title (not a child), so the base class's
/// "walk up from the anchor" heuristic grabs the .sbar metadata block instead
/// of the title. We override both to target the .ssbox container explicitly.
/// </summary>
public sealed class Yhg007Source : HtmlMagnetSourceBase
{
    public override string Name => "yhg007.com";
    private const string BaseUrl = "https://yhg007.com";

    public Yhg007Source() { }

    protected override IEnumerable<string> SearchUrls(string query)
    {
        var orig = query.Trim();
        yield return $"{BaseUrl}/search-{orig}-0-0-1.html";
        var q = Uri.EscapeDataString(query);
        yield return $"{BaseUrl}/search?q={q}";
        yield return $"{BaseUrl}/search/{q}";
    }

    /// <summary>Find the enclosing .ssbox, then the .title &gt; h3 text.</summary>
    protected override string? ResolveTitle(string magnetUri, HtmlNode a, HtmlDocument doc)
    {
        // dn= is still most reliable if present.
        var dn = ExtractDn(magnetUri);
        if (!string.IsNullOrWhiteSpace(dn) && IsLikelyTitle(dn)) return dn;

        var box = AncestorWithClass(a, "ssbox");
        if (box != null)
        {
            // .title > h3 (innerText is the title; strip a trailing size if any).
            var h3 = box.SelectSingleNode(".//div[contains(@class,'title')]//h3")
                     ?? box.SelectSingleNode(".//h3");
            if (h3 != null)
            {
                var t = CleanText(h3.InnerText);
                if (IsLikelyTitle(t))
                    return Regex.Replace(t!, @"\s*\d+(?:\.\d+)?\s*(?:TB|GB|MB|KB)\s*$", "", RegexOptions.IgnoreCase);
            }
        }
        return base.ResolveTitle(magnetUri, a, doc);
    }

    /// <summary>Size lives in the .sbar block as "大小:&lt;b&gt;5.4 GB&lt;/b&gt;".</summary>
    protected override string BestSize(HtmlNode a)
    {
        var box = AncestorWithClass(a, "ssbox") ?? a.ParentNode?.ParentNode?.ParentNode;
        var text = CleanText(box?.InnerText) ?? "";
        // Prefer the explicit "大小:5.4 GB" marker.
        var m = Regex.Match(text, @"大小[:：]?\s*(\d+(?:\.\d+)?\s*(?:TB|GB|MB|KB))", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.Trim();
        return base.BestSize(a);
    }

    /// <summary>Walk up from a node until we hit an element whose class contains `cls`.</summary>
    private static HtmlNode? AncestorWithClass(HtmlNode node, string cls)
    {
        HtmlNode? n = node;
        for (int depth = 0; depth < 8 && n != null; depth++)
        {
            var c = n.GetAttributeValue("class", "");
            if (c.Split(' ').Any(x => x.Equals(cls, StringComparison.Ordinal))) return n;
            n = n.ParentNode;
        }
        return null;
    }
}
