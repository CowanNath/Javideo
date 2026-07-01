using System.Text;
using Javideo.Worker.Models;

namespace Javideo.Worker.Services;

/// <summary>
/// Downloads poster/thumb images into the movie folder. Failures are logged
/// and swallowed — a missing image must not block ingestion.
///
/// Files are named with the 番号 prefix so they're easy to identify and
/// unambiguous when the folder is shared with other tools:
///   {番号}-poster.jpg  {番号}-thumb.jpg
/// </summary>
public sealed class ImageWriter
{
    private readonly HttpClient _http;
    public ImageWriter(HttpClient http) => _http = http;

    public async Task DownloadAsync(string folderPath, string number, string? posterUrl, string? thumbUrl)
    {
        Directory.CreateDirectory(folderPath);
        var safe = SafeName(number);

        if (!string.IsNullOrWhiteSpace(posterUrl))
            await TrySaveAsync(Path.Combine(folderPath, $"{safe}-poster.jpg"), posterUrl);
        if (!string.IsNullOrWhiteSpace(thumbUrl))
            await TrySaveAsync(Path.Combine(folderPath, $"{safe}-thumb.jpg"), thumbUrl);
    }

    private static string SafeName(string number)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(number.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }

    private async Task TrySaveAsync(string dest, string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            using var resp = await _http.GetAsync(uri);
            if (!resp.IsSuccessStatusCode) return;
            await using var fs = File.Create(dest);
            await resp.Content.CopyToAsync(fs);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to download image {Url}", url);
        }
    }

    /// <summary>Writes the magnet links as a plain text file.</summary>
    public static void WriteMagnetTxt(string destPath, IEnumerable<MagnetResult> magnets, string query)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var sb = new StringBuilder();
        sb.AppendLine($"# 番号: {query}");
        sb.AppendLine($"# 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("# 磁力链接由 Javideo 收集,请使用外部下载工具处理。");
        sb.AppendLine();
        int i = 1;
        foreach (var mg in magnets)
        {
            sb.AppendLine($"[{i}] {mg.Title}  ({mg.Size})");
            sb.AppendLine($"    {mg.MagnetUri}");
            sb.AppendLine();
            i++;
        }
        File.WriteAllText(destPath, sb.ToString(), System.Text.Encoding.UTF8);
    }
}
