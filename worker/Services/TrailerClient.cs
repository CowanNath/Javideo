using System.Net;
using System.Text.RegularExpressions;

namespace Javideo.Worker.Services;

/// <summary>
/// Resolves a DMM/FANZA trailer (preview video) URL purely from the 番号 — no
/// third-party API, no HTML scraping. Ported from the get_m3u8_url_js project's
/// VideoPreviewService: it parses the 番号, maps the label to a DMM vendor id,
/// builds candidate URLs under cc3001.dmm.co.jp/litevideo/freepv, and probes
/// them with HEAD requests to find the one that exists.
///
/// DMM blocks non-JP IPs (403), so a proxy (configured in Settings) is used
/// when set.
/// </summary>
public sealed class TrailerClient
{
    private readonly SettingsService _settings;
    // DMM free-preview base.
    private const string DmmVideos = "https://cc3001.dmm.co.jp/litevideo/freepv";

    // label (lowercase, no number) → DMM vendor id prefix.
    private static readonly Dictionary<string, string> VendorLabels = new()
    {
        ["1"] = "aiav,boko,dandy,dldss,emois,fadss,fcdss,fsdss,fsvss,ftav,iene,kire,kkbt,kmhr,kmhrs,kuse,mgold,mist,mogi,moon,msfh,mtall,namh,nhdt,nhdta,nhdtb,noskn,open,piyo,rct,rctd,sace,sdab,sdam,sdde,sdjs,sdmf,sdmm,sdms,sdmt,sdmu,sdmua,sdnm,sdth,senn,setm,seth,sgki,shyn,silk,silks,silku,sply,star,stars,start,stzy,sun,suwk,svbgr,svcao,svdvd,svmgm,svnnp,svsha,svvrt,sw,wo",
        ["2"] = "cen,ckw,cwm,dfdm,dfe,dje,ecb,ekai,emsk,hkw,wdi,wsp,wss,wzen",
        ["13"] = "dsvr",
        ["18"] = "sprd",
        ["24"] = "bld,cvd,dkd,frd,isrd,nad,nhd,ped,tyd,ufd,vdd",
        ["41"] = "aibv,aidv,hodv,howy",
        ["53"] = "dv",
        ["55"] = "csct,hitma,hsrm,id,qqq,qvrt,t,tsms",
        ["59"] = "hez",
        ["118"] = "aas,abf,abp,abw,aka,bgn,chn,dic,dmr,dkn,dlv,fbu,fig,fit,fiv,gni,gdl,ggg,jbs,onez,ppt,ppx,pxh,sga,shf,sng,thu,yrk",
        ["5433"] = "btha",
        ["5642"] = "neob",
        ["h_019"] = "aczd",
        ["h_066"] = "fax",
        ["h_068"] = "mxbd,mxgs,mxsps",
        ["h_086"] = "hone,hthd,iora,iro,jrzd,jrze,jura,nuka",
        ["h_113"] = "cb,ps,se,sy,zm",
        ["h_139"] = "dhld,doks,dotm",
        ["h_172"] = "gghx,hmgl,hmnf",
        ["h_237"] = "ambi,clot,find,hdka,nacr,nacx,zmar",
        ["h_346"] = "rebd,rebdb",
        ["h_458"] = "hsm",
        ["h_491"] = "fneo,fone,tenn,tkou",
        ["h_720"] = "zex",
        ["h_796"] = "san",
        ["h_910"] = "vrtm",
        ["h_1100"] = "hzgd",
        ["h_1127"] = "gopj",
        ["h_1133"] = "gone,jstk,nine,tdan",
        ["h_1240"] = "milk",
        ["h_1324"] = "skmj",
        ["h_1350"] = "kamef,kamx,tmgv,vov,vovx",
        ["h_1472"] = "xox",
        ["h_1495"] = "bank",
        ["h_1539"] = "slr",
        ["h_1615"] = "beaf",
        ["h_1711"] = "dal,docd,docp,hmrk,maan,mfcd,mfct,mgtd",
        ["h_1712"] = "asi,dtt,fft,kbi,kbl,kbr,tuk",
        ["h_1757"] = "olm",
        ["h_1800"] = "yyds",
        ["n_707"] = "aims,fuka,jfic,jtdk,lbdd,mbdd,ohp",
        ["n_709"] = "maraa,mbraa,mbrau,mbraz,mbrba,mbrbi,mbrbm,mbrbn,mmraa",
        ["n_1428"] = "ap,ld,ss",
    };

    // Flattened reverse map: label → vendor id, built once.
    private static readonly Dictionary<string, string> LabelToVendor = VendorLabels
        .SelectMany(kv => kv.Value.Split(',').Select(label => (label, vendor: kv.Key)))
        .ToDictionary(x => x.label, x => x.vendor);

    private static readonly HashSet<string> VrLabels =
        new(("aqube,aquco,aquga,aquma,exmo,fsvss,gopj,komz,slr,urvrsp,vrkm").Split(','));

    public TrailerClient(SettingsService settings) => _settings = settings;

    /// <summary>Temp dir for search-time trailer downloads (moved to library
    /// folder on ingest, or deleted on next search).</summary>
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "javideo-trailers");

    public static string TempPathFor(string fanHao) =>
        Path.Combine(TempDir, $"{fanHao.ToUpperInvariant()}.mp4");

    /// <summary>Delete old temp trailers (called before a new search).</summary>
    public static void CleanupTemp()
    {
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); } catch {}
    }

    /// <summary>Find an existing temp trailer matching the fanHao case-insensitively,
    /// so ingest can find the file even if MetaTube returned a differently-cased number.</summary>
    public static string? FindExistingTemp(string fanHao)
    {
        if (!Directory.Exists(TempDir)) return null;
        var target = fanHao.ToUpperInvariant();
        var files = Directory.GetFiles(TempDir, "*.mp4");
        foreach (var f in files)
        {
            if (Path.GetFileNameWithoutExtension(f).Equals(target, StringComparison.OrdinalIgnoreCase))
                return f;
        }
        // Fallback: if only one temp file exists, use it (common case — just searched one number).
        if (files.Length == 1) return files[0];
        return null;
    }

    /// <summary>Build an HttpClient that uses the proxy from settings (if set),
    /// since DMM blocks non-JP IPs with 403. Supports optional username/password.</summary>
    private HttpClient CreateClient(TimeSpan timeout)
    {
        var proxyStr = _settings.GetAsync(SettingsService.KeyProxy).GetAwaiter().GetResult()?.Trim();
        var user = _settings.GetAsync("network.proxyUser").GetAwaiter().GetResult()?.Trim();
        var pass = _settings.GetAsync("network.proxyPass").GetAwaiter().GetResult();
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(proxyStr) && Uri.TryCreate(proxyStr, UriKind.Absolute, out var proxyUri))
        {
            var proxy = new WebProxy(proxyUri);
            if (!string.IsNullOrWhiteSpace(user))
                proxy.Credentials = new NetworkCredential(user, pass ?? "");
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>Find the first working trailer URL for a 番号, or null.
    /// Tries each candidate sequentially with retries — concurrent probing was
    /// unreliable through proxies (connection reset / 403 race conditions).</summary>
    public async Task<string?> FindTrailerUrlAsync(string fanHao)
    {
        var parsed = ParseFanHao(fanHao);
        if (parsed == null)
        {
            Serilog.Log.Warning("TrailerClient: failed to parse 番号 '{FanHao}'", fanHao);
            return null;
        }
        var (label, number, suffix) = parsed.Value;
        var candidates = GenerateUrls(label, number, suffix);
        Serilog.Log.Information("TrailerClient: trying {Count} URLs for {FanHao}", candidates.Count, fanHao);

        foreach (var url in candidates)
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using var http = CreateClient(TimeSpan.FromSeconds(15));
                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                    Serilog.Log.Information("TrailerClient: {Url} -> {Status} (attempt {N})", url, (int)resp.StatusCode, attempt);
                    if (resp.IsSuccessStatusCode) return url;
                    break; // 4xx/5xx — no point retrying the same URL
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning("TrailerClient: {Url} -> exception (attempt {N}): {Msg}", url, attempt, ex.Message);
                    if (attempt < 2) await Task.Delay(1000);
                }
            }
        }
        Serilog.Log.Warning("TrailerClient: no working trailer found for {FanHao}", fanHao);
        return null;
    }

    private static (string label, string number, string suffix)? ParseFanHao(string s)
    {
        var m = Regex.Match(s.Trim(), @"^([a-zA-Z]+)[-_]?(\d+)([a-zA-Z]+)?$");
        if (!m.Success) return null;
        return (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value,
                (m.Groups[3].Success ? m.Groups[3].Value : "").ToLowerInvariant());
    }

    private static (string id, string vid) GenerateIds(string label, string number, string suffix)
    {
        var vendor = LabelToVendor.TryGetValue(label, out var v) ? v : "";
        var id = $"{vendor}{label}{number}{suffix}";
        var vid = $"{vendor}{label}{number.PadLeft(5, '0')}{suffix}";
        return (id, vid);
    }

    private static List<string> GenerateUrls(string label, string number, string suffix)
    {
        var (id, vid) = GenerateIds(label, number, suffix);
        var urls = new List<string>();
        var isVr = label.EndsWith("vr") || VrLabels.Contains(label);

        if (isVr)
        {
            urls.Add(Url(vid, "vrlite"));
            urls.Add(Url(id, "vrlite"));
            return urls;
        }
        foreach (var sfx in new[] { "hhb", "mhb", "_dmb_w", "_dm" })
        {
            urls.Add(Url(vid, sfx));
            urls.Add(Url(id, sfx));
        }
        return urls;
    }

    private static string Url(string key, string sfx)
        => $"{DmmVideos}/{key[0]}/{key[..3]}/{key}/{key}{sfx}.mp4";

    /// <summary>Download the trailer bytes via the proxy-aware client, with
    /// up to 3 retries (network/proxy can be flaky).</summary>
    public async Task<byte[]?> DownloadAsync(string url)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var http = CreateClient(TimeSpan.FromSeconds(90));
                return await http.GetByteArrayAsync(url);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Trailer download attempt {N}/3 failed for {Url}", attempt, url);
                if (attempt < 3) await Task.Delay(1000 * attempt); // backoff 1s, 2s
            }
        }
        return null;
    }
}
