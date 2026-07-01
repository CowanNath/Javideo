using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Javideo.Worker.Models;

namespace Javideo.Worker.Services;

/// <summary>
/// HTTP client for a user-supplied metatube-server (https://github.com/metatube-community/metatube-sdk-go).
/// Address/timeout/token come from the <see cref="SettingsService"/> so they can change at runtime.
///
/// Each call creates its own HttpClient (sharing one + mutating BaseAddress/Timeout
/// after the first request throws "Properties can only be modified before sending
/// the first request" — the same trap the magnet sources hit).
/// </summary>
public sealed class MetaTubeClient
{
    private readonly SettingsService _settings;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Shared handler (no mutable per-request state) — clients created from it
    // are cheap and don't leak sockets.
    private static readonly HttpClientHandler Handler = new()
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    };

    public MetaTubeClient(SettingsService settings) => _settings = settings;

    public const string KeyToken = "metatube.token";

    private async Task<(string baseUrl, int timeoutMs, string? token)> GetConfigAsync()
    {
        var addr = (await _settings.GetAsync(SettingsService.KeyMetaTubeAddress))?.Trim().TrimEnd('/');
        var timeout = await _settings.GetAsync(SettingsService.KeyMetaTubeTimeout);
        var token = (await _settings.GetAsync(KeyToken))?.Trim();
        int.TryParse(timeout, out var ms);
        if (ms <= 0) ms = 15000;
        return (addr ?? "", ms, string.IsNullOrEmpty(token) ? null : token);
    }

    private HttpClient NewClient(string baseUrl, int timeoutMs, string? token)
    {
        var http = new HttpClient(Handler, disposeHandler: false)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
        };
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return http;
    }

    /// <summary>Connectivity test — hits /v1/providers.</summary>
    public async Task<(bool ok, string detail)> TestConnectionAsync()
    {
        var (baseUrl, ms, token) = await GetConfigAsync();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return (false, "未配置 MetaTube 地址");
        try
        {
            using var http = NewClient(baseUrl, ms, token);
            using var resp = await http.GetAsync("/v1/providers");
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}");
            var body = await resp.Content.ReadAsStringAsync();
            return (true, body.Length > 0 ? "连通正常" : "连通(空响应)");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Resolve an actor by name: search MetaTube then fetch the detail.
    /// Returns a richly-populated Actor (avatar via the image proxy) or null
    /// when nothing is found. Used by the actor detail page.</summary>
    public async Task<ActorDetail?> GetActorAsync(string name)
    {
        var (baseUrl, ms, token) = await GetConfigAsync();
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new MetaTubeNotConfiguredException();

        using var http = NewClient(baseUrl, ms, token);

        // Step 1: search by name.
        using var searchResp = await http.GetAsync($"/v1/actors/search?q={WebUtility.UrlEncode(name)}");
        if (!searchResp.IsSuccessStatusCode) return null;
        var searchBody = await searchResp.Content.ReadAsStringAsync();
        var search = JsonSerializer.Deserialize<MtActorSearchResponse>(searchBody, Json);
        var hit = search?.Data?.FirstOrDefault();
        if (hit == null || string.IsNullOrEmpty(hit.Provider)) return null;

        // Step 2: fetch detail (may carry biography/measurements; Gfriends only has photos).
        MtActor? detail = null;
        try
        {
            using var d = await http.GetAsync($"/v1/actors/{hit.Provider}/{WebUtility.UrlEncode(hit.Id)}");
            if (d.IsSuccessStatusCode)
            {
                var body = await d.Content.ReadAsStringAsync();
                detail = JsonSerializer.Deserialize<MtDetailActorResponse>(body, Json)?.Data;
            }
        }
        catch { /* keep search result */ }
        var src = detail ?? hit;

        // Avatar via the public image proxy (more reliable than raw gfriends/dmm URLs).
        var avatar = $"{baseUrl}/v1/images/primary/{hit.Provider}/{WebUtility.UrlEncode(hit.Id)}";

        return new ActorDetail
        {
            Name = string.IsNullOrWhiteSpace(src.Name) ? name : src.Name,
            AvatarUrl = avatar,
            Summary = src.Summary,
            Birthday = src.Birthday,
            Height = src.Height,
            Measurements = src.Measurements,
            CupSize = src.CupSize,
            BloodType = src.BloodType,
            Hobby = src.Hobby,
            Skill = src.Skill,
            Nationality = src.Nationality,
            Aliases = src.Aliases ?? new(),
            Images = (src.Images ?? new()).Take(8).ToList(),
            Homepage = src.Homepage,
            Provider = hit.Provider,
        };
    }

    /// <summary>Fetch movie metadata by 番号 — maps metatube JSON to our Movie model.
    /// Throws <see cref="MetaTubeNotConfiguredException"/> when no server is set,
    /// so the endpoint can surface a friendly "go configure" message rather than 404.
    ///
    /// MetaTube's API is two-step: search by number, then fetch the detail by
    /// provider+id. The search may return candidates from several providers, so
    /// we fetch each candidate's detail concurrently and pick the most complete.</summary>
    public async Task<Movie?> GetMovieAsync(string number)
    {
        var (baseUrl, ms, token) = await GetConfigAsync();
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new MetaTubeNotConfiguredException();

        using var http = NewClient(baseUrl, ms, token);

        // Step 1: search by 番号 (fallback lets other providers answer too).
        List<MtMovie> hits;
        using (var searchResp = await http.GetAsync($"/v1/movies/search?q={WebUtility.UrlEncode(number)}&fallback=true"))
        {
            if (searchResp.StatusCode == HttpStatusCode.NotFound) return null;
            searchResp.EnsureSuccessStatusCode();
            var searchBody = await searchResp.Content.ReadAsStringAsync();
            var search = JsonSerializer.Deserialize<MtSearchResponse>(searchBody, Json);
            hits = search?.Data ?? new();
        }
        hits = hits.Where(h => !string.IsNullOrEmpty(h.Id) && !string.IsNullOrEmpty(h.Provider)).ToList();
        if (hits.Count == 0) return null;

        // Step 2: fetch each candidate's detail concurrently, keep the successful ones.
        var details = new List<MtMovie>();
        var tasks = hits.Select(async h =>
        {
            try
            {
                using var r = await http.GetAsync($"/v1/movies/{h.Provider}/{h.Id}");
                if (!r.IsSuccessStatusCode) return;
                var body = await r.Content.ReadAsStringAsync();
                var d = JsonSerializer.Deserialize<MtDetailResponse>(body, Json);
                if (d?.Data != null) lock (details) details.Add(d.Data);
            }
            catch { /* ignore individual failures */ }
        });
        await Task.WhenAll(tasks);

        // Pick the best candidate. FIRST prefer an exact 番号 match (case-insensitive)
        // — MetaTube's search is fuzzy and may return CWPBD-146 when you asked for
        // CWPBD-46. Only fall back to the most-complete candidate if no exact match.
        MtMovie best;
        if (details.Count > 0)
        {
            // Exact number match wins; among those, pick the most complete.
            best = details
                .OrderByDescending(d => string.Equals(d.Number, number, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(Completeness)
                .First();
        }
        else
            best = hits[0];

        return Map(best, number, baseUrl);
    }

    /// <summary>Return ALL search candidates (lightweight, no detail fetch) so
    /// the frontend can show a picker when there are multiple results. Each
    /// candidate includes provider, id, title, cover, and score for display.</summary>
    public async Task<List<MovieCandidate>> SearchCandidatesAsync(string number)
    {
        var (baseUrl, ms, token) = await GetConfigAsync();
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new MetaTubeNotConfiguredException();

        using var http = NewClient(baseUrl, ms, token);
        using var resp = await http.GetAsync($"/v1/movies/search?q={WebUtility.UrlEncode(number)}&fallback=true");
        if (!resp.IsSuccessStatusCode) return new();
        var body = await resp.Content.ReadAsStringAsync();
        var search = JsonSerializer.Deserialize<MtSearchResponse>(body, Json);
        var hits = search?.Data ?? new();

        return hits
            .Where(h => !string.IsNullOrEmpty(h.Id) && !string.IsNullOrEmpty(h.Provider))
            .Select(h => new MovieCandidate
            {
                Provider = h.Provider!,
                Id = h.Id!,
                Number = h.Number ?? number,
                Title = h.Title,
                // Use MetaTube image proxy instead of raw dmm URL — the webview
                // hits the proxy (localhost MetaTube), not dmm directly (avoids
                // Tracking Prevention). This is search-time only; after ingest,
                // images come from local files.
                CoverUrl = $"{baseUrl}/v1/images/primary/{h.Provider}/{WebUtility.UrlEncode(h.Id!)}?auto=false",
                Score = h.Score,
                ThumbUrl = h.ThumbUrl,
            })
            .ToList();
    }

    /// <summary>Fetch the full detail for a specific provider/id and map to Movie.</summary>
    public async Task<Movie?> GetMovieByProviderAsync(string provider, string id, string number)
    {
        var (baseUrl, ms, token) = await GetConfigAsync();
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new MetaTubeNotConfiguredException();

        using var http = NewClient(baseUrl, ms, token);
        using var resp = await http.GetAsync($"/v1/movies/{provider}/{id}");
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync();
        var detail = JsonSerializer.Deserialize<MtDetailResponse>(body, Json);
        return detail?.Data != null ? Map(detail.Data, number, baseUrl) : null;
    }

    /// <summary>Score how complete a candidate is, so we prefer rich results.</summary>
    private static int Completeness(MtMovie m)
    {
        int score = 0;
        if (!string.IsNullOrWhiteSpace(m.CoverUrl)) score += 4;
        if (!string.IsNullOrWhiteSpace(m.Summary)) score += 2;
        if (!string.IsNullOrWhiteSpace(m.ThumbUrl)) score += 2;
        if (m.Genres?.Count > 0) score += 2;
        if (m.Actors?.Count > 0) score += 1;
        if (!string.IsNullOrWhiteSpace(m.Maker)) score += 1;
        return score;
    }

    private static Movie Map(MtMovie mt, string number, string baseUrl)
    {
        // Prefer MetaTube's image-proxy endpoints over the raw dmm.co.jp URLs:
        //   /v1/images/primary/{provider}/{id}?auto=false  — full cover (no face-crop)
        //   /v1/images/thumb/{provider}/{id}              — thumbnail
        // auto=false keeps the original aspect ratio (MetaTube's primary endpoint
        // crops to a face by default); the CSS object-cover on each card/banner
        // handles display cropping, so we want the un-truncated source image.
        var hasImg = !string.IsNullOrEmpty(mt.Provider) && !string.IsNullOrEmpty(mt.Id);
        var cover = hasImg ? $"{baseUrl}/v1/images/primary/{mt.Provider}/{mt.Id}?auto=false" : mt.CoverUrl;
        var thumb = hasImg ? $"{baseUrl}/v1/images/thumb/{mt.Provider}/{mt.Id}"
                           : (string.IsNullOrEmpty(mt.ThumbUrl) ? mt.CoverUrl : mt.ThumbUrl);

        var m = new Movie
        {
            Number = string.IsNullOrWhiteSpace(mt.Number) ? number : mt.Number,
            Title = mt.Title,
            OriginalTitle = mt.Title,
            Summary = mt.Summary,
            Maker = mt.Maker,
            Label = mt.Label,
            Series = mt.Series,
            Director = mt.Director,
            ReleaseDate = mt.ReleaseDate,
            RuntimeMinutes = mt.Runtime,
            CoverUrl = cover,
            ThumbUrl = thumb,
            Score = mt.Score,
            Provider = mt.Provider,
            HomepageUrl = mt.Homepage,
            // metatube returns actor names as strings; avatar must come from a
            // separate actor lookup, so we leave it null here.
            Actors = (mt.Actors ?? new()).Select(a => new Actor { Name = a }).ToList(),
            PreviewImages = (mt.PreviewImages ?? new()).ToList(),
        };
        foreach (var g in mt.Genres ?? new())
            m.Tags.Add(new Tag { Name = g, Category = "genre", IsStandard = true });
        if (!string.IsNullOrWhiteSpace(mt.Series))
            m.Tags.Add(new Tag { Name = mt.Series!, Category = "series", IsStandard = true });
        if (!string.IsNullOrWhiteSpace(mt.Maker))
            m.Tags.Add(new Tag { Name = mt.Maker!, Category = "maker", IsStandard = true });
        return m;
    }

    // ---- Minimal metatube DTOs (subset of fields we use) ----
    // NOTE: metatube returns snake_case keys (cover_url, release_date, ...).
    // PropertyNameCaseInsensitive only ignores casing, it does NOT convert
    // snake_case → PascalCase, so snake_case fields need explicit attributes.
    private sealed class MtSearchResponse { public List<MtMovie>? Data { get; set; } }
    private sealed class MtDetailResponse { public MtMovie? Data { get; set; } }
    private sealed class MtMovie
    {
        public string? Id { get; set; }
        public string? Number { get; set; }
        public string? Title { get; set; }
        public string? Summary { get; set; }
        public string? Maker { get; set; }
        public string? Label { get; set; }
        public string? Series { get; set; }
        public string? Director { get; set; }
        [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
        public int? Runtime { get; set; }
        [JsonPropertyName("cover_url")] public string? CoverUrl { get; set; }
        [JsonPropertyName("thumb_url")] public string? ThumbUrl { get; set; }
        [JsonPropertyName("preview_images")] public List<string>? PreviewImages { get; set; }
        public double? Score { get; set; }
        public string? Provider { get; set; }
        public string? Homepage { get; set; }
        public List<string>? Genres { get; set; }
        public List<string>? Actors { get; set; }
    }

    // ---- Actor DTOs (returned by the actor detail endpoint) ----
    private sealed class MtActorSearchResponse { public List<MtActor>? Data { get; set; } }
    private sealed class MtDetailActorResponse { public MtActor? Data { get; set; } }
    private sealed class MtActor
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Provider { get; set; }
        public string? Homepage { get; set; }
        public string? Summary { get; set; }
        public string? Hobby { get; set; }
        public string? Skill { get; set; }
        [JsonPropertyName("blood_type")] public string? BloodType { get; set; }
        [JsonPropertyName("cup_size")] public string? CupSize { get; set; }
        public string? Measurements { get; set; }
        public string? Nationality { get; set; }
        public int? Height { get; set; }
        public List<string>? Aliases { get; set; }
        public List<string>? Images { get; set; }
        public string? Birthday { get; set; }
        [JsonPropertyName("debut_date")] public string? DebutDate { get; set; }
    }
}

/// <summary>Rich actor profile (from MetaTube) for the actor detail page.</summary>
public sealed class ActorDetail
{
    public string Name { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public string? Summary { get; set; }
    public string? Birthday { get; set; }
    public int? Height { get; set; }
    public string? Measurements { get; set; }
    public string? CupSize { get; set; }
    public string? BloodType { get; set; }
    public string? Hobby { get; set; }
    public string? Skill { get; set; }
    public string? Nationality { get; set; }
    public List<string> Aliases { get; set; } = new();
    public List<string> Images { get; set; } = new();
    public string? Homepage { get; set; }
    public string? Provider { get; set; }
}

/// <summary>A lightweight search candidate for the picker UI.</summary>
public sealed class MovieCandidate
{
    public string Provider { get; set; } = "";
    public string Id { get; set; } = "";
    public string Number { get; set; } = "";
    public string? Title { get; set; }
    public string? CoverUrl { get; set; }
    public string? ThumbUrl { get; set; }
    public double? Score { get; set; }
}
