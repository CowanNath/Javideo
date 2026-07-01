using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Javideo.Worker.Models;

namespace Javideo.Worker.Services;

public sealed class TranslationService
{
    private readonly SettingsService _settings;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly HttpClientHandler Handler = new()
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    };

    public TranslationService(SettingsService settings) => _settings = settings;

    private async Task<(string endpoint, string apiKey, string model)> GetConfigAsync()
    {
        var ep = (await _settings.GetAsync(SettingsService.KeyLlmEndpoint))?.Trim().TrimEnd('/');
        var key = (await _settings.GetAsync(SettingsService.KeyLlmApiKey))?.Trim() ?? "";
        var model = (await _settings.GetAsync(SettingsService.KeyLlmModel))?.Trim();
        if (string.IsNullOrEmpty(model)) model = "gpt-4o-mini";
        return (ep ?? "", key, model);
    }

    public async Task<(bool ok, string detail)> TestConnectionAsync()
    {
        var (ep, key, model) = await GetConfigAsync();
        if (string.IsNullOrWhiteSpace(ep))
            return (false, "未配置 LLM 地址");
        try
        {
            using var http = NewHttpClient(ep, key);
            var body = new
            {
                model,
                messages = new[] { new { role = "user", content = "Hi" } },
                max_tokens = 1,
                stream = false
            };
            using var resp = await http.PostAsync("/v1/chat/completions",
                new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"));
            return resp.StatusCode == HttpStatusCode.Unauthorized
                ? (false, "API Key 无效")
                : resp.IsSuccessStatusCode
                    ? (true, "LLM 连通正常")
                    : (false, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<Movie?> TranslateAsync(Movie movie)
    {
        var (ep, key, model) = await GetConfigAsync();
        if (string.IsNullOrWhiteSpace(ep)) return movie;

        var prompt = BuildPrompt(movie);
        using var http = NewHttpClient(ep, key);

        var reqBody = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = "你是一个翻译助手。将日语/英语的成人影片元数据翻译成简体中文。只返回 JSON，不要包含 markdown 代码块标记。\n{\n  \"title\": \"翻译后的标题\",\n  \"summary\": \"翻译后的简介\"\n}" },
                new { role = "user", content = prompt }
            },
            temperature = 0.3,
            stream = false
        };

        try
        {
            using var resp = await http.PostAsync("/v1/chat/completions",
                new StringContent(JsonSerializer.Serialize(reqBody, Json), Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) return movie;

            var raw = await resp.Content.ReadAsStringAsync();
            var chatResp = JsonSerializer.Deserialize<ChatCompletionResponse>(raw, Json);
            var content = chatResp?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrEmpty(content)) return movie;

            var translated = JsonSerializer.Deserialize<TranslationResult>(content, Json);
            if (translated == null) return movie;

            movie.Title = translated.Title ?? movie.Title;
            movie.Summary = translated.Summary ?? movie.Summary;
            return movie;
        }
        catch
        {
            return movie;
        }
    }

    private static string BuildPrompt(Movie movie)
    {
        var sb = new StringBuilder();
        sb.AppendLine("翻译以下影片元数据：");
        if (!string.IsNullOrEmpty(movie.Title))
            sb.AppendLine($"标题: {movie.Title}");
        if (!string.IsNullOrEmpty(movie.Summary))
            sb.AppendLine($"简介: {movie.Summary}");
        return sb.ToString();
    }

    private static HttpClient NewHttpClient(string endpoint, string apiKey)
    {
        var http = new HttpClient(Handler, disposeHandler: false)
        {
            BaseAddress = new Uri(endpoint),
            Timeout = TimeSpan.FromSeconds(60),
        };
        if (!string.IsNullOrEmpty(apiKey))
            http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        return http;
    }

    private sealed class ChatCompletionResponse
    {
        public List<Choice>? Choices { get; set; }
    }

    private sealed class Choice
    {
        public Message? Message { get; set; }
    }

    private sealed class Message
    {
        public string? Content { get; set; }
    }

    private sealed class TranslationResult
    {
        public string? Title { get; set; }
        public string? Summary { get; set; }
    }
}
