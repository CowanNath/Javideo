using System.Text.Json;
using Dapper;
using Javideo.Worker.Db;
using Javideo.Worker.Services;

namespace Javideo.Worker.Endpoints;

/// <summary>
/// Subtitle matching via Thunder's (迅雷) subtitle API — the same endpoint the
/// Thunder client itself calls (reverse-engineered, see 52pojie thread
/// 1982723). Matched subtitle files are downloaded next to the video and named
/// after it, so external players auto-load them.
/// </summary>
public static class SubtitleEndpoints
{
    private const string ApiBase = "https://api-shoulei-ssl.xunlei.com/oracle/subtitle";
    private static readonly HttpClient Http = HttpGuard.CreateClient(TimeSpan.FromSeconds(30));

    public static void MapSubtitleEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/movies/{id:long}/subtitles").WithTags("Subtitles");

        // List subtitles matching this movie's video file. Computes + caches
        // the file's GCID on first call (streams the whole video once) — but
        // only for locally-held files; cloud-drive videos (cache_local
        // libraries) are matched by filename only to avoid pulling bandwidth.
        // Duration base for ranking: the real video duration when the container
        // is parseable (mp4/mkv), otherwise the scraped runtime.
        g.MapGet("/", async (long id, DbConnectionFactory db) =>
        {
            await using var c = db.Create();
            await c.OpenAsync();
            var row = await c.QueryFirstOrDefaultAsync<(string? Gcid, string? Folder, string? Number, int? Runtime, string? Source)>(
                "SELECT gcid Gcid, folder_path Folder, number Number, runtime_minutes Runtime, source_path Source FROM movies WHERE id=@id", new { id });
            if (row.Number == null)
                return Results.NotFound(new { ok = false, gcid = "", subtitles = Array.Empty<SubtitleItem>(), detail = "影片不存在" });

            var video = FindVideoFile(row.Folder)
                        ?? (!string.IsNullOrWhiteSpace(row.Source) && File.Exists(row.Source) ? row.Source : null);
            if (video == null)
                return Results.Ok(new { ok = false, gcid = "", subtitles = Array.Empty<SubtitleItem>(), detail = "未找到视频文件" });

            double? baseDurationSec = null;
            if (MediaDuration.TryGetSeconds(video, out var real))
                baseDurationSec = real;
            else if (row.Runtime is > 0)
                baseDurationSec = row.Runtime.Value * 60.0;

            var gcid = row.Gcid;
            if (string.IsNullOrWhiteSpace(gcid) && IsLocalDrive(video))
            {
                try { gcid = await Gcid.ComputeAsync(video); }
                catch (Exception ex) { return Results.Ok(new { ok = false, gcid = "", subtitles = Array.Empty<SubtitleItem>(), detail = "GCID 计算失败: " + ex.Message }); }
                await c.ExecuteAsync("UPDATE movies SET gcid=@g WHERE id=@id", new { g = gcid, id });
            }

            var apiUrl = $"{ApiBase}?gcid={gcid}&cid=&name={Uri.EscapeDataString(Path.GetFileName(video))}";
            if (!HttpGuard.IsSafePublicUrl(apiUrl, out var uri))
                return Results.BadRequest(new { ok = false, gcid, subtitles = Array.Empty<SubtitleItem>(), detail = "接口地址不合法" });

            var subs = new List<SubtitleItem>();
            string? err = null;
            try
            {
                var json = await Http.GetStringAsync(uri);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        var url = el.TryGetProperty("url", out var u) ? u.GetString() : null;
                        if (string.IsNullOrWhiteSpace(url)) continue;
                        long durationMs = el.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetInt64() : 0;
                        // metric_data_source == "mapped" (or a non-zero score)
                        // means Thunder matched the entry via the GCID
                        // fingerprint — i.e. the exact same video content.
                        var mapped = el.TryGetProperty("metric_data_source", out var mds)
                            && mds.ValueKind == JsonValueKind.String
                            && mds.GetString() == "mapped";
                        var score = el.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
                        // Closeness to the base duration — the ranking signal
                        // the user asked for: subs cut for the same release
                        // stay within seconds, different cuts differ by minutes.
                        double? diffSec = baseDurationSec != null && durationMs > 0
                            ? Math.Abs(durationMs / 1000.0 - baseDurationSec.Value)
                            : null;
                        subs.Add(new SubtitleItem(
                            Url: url,
                            Ext: el.TryGetProperty("ext", out var e) ? e.GetString() ?? "srt" : "srt",
                            Name: el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            Score: score,
                            Languages: el.TryGetProperty("languages", out var l) && l.ValueKind == JsonValueKind.Array
                                ? string.Join("/", l.EnumerateArray().Select(x => x.GetString() ?? "")) : "",
                            DurationMs: durationMs,
                            DurationDiffSec: diffSec,
                            GcidHit: mapped || score > 0));
                    }
                }
            }
            catch (Exception ex)
            {
                err = "字幕接口请求失败: " + ex.Message;
            }
            // GCID fingerprint hits first (exact same content), then score,
            // then duration closeness — stable sort keeps Thunder's own order
            // for ties.
            subs = subs
                .OrderByDescending(s => s.GcidHit)
                .ThenByDescending(s => s.Score)
                .ThenBy(s => s.DurationDiffSec ?? double.MaxValue)
                .ToList();
            return Results.Ok(new { ok = err == null, gcid, subtitles = subs, detail = err ?? (subs.Count == 0 ? "没有匹配到字幕" : "") });
        });

        // Download one subtitle next to the video file, named after it (so the
        // player auto-loads it) — for cloud-drive videos that means the remote
        // folder, but the file is only a few KB.
        g.MapPost("/download", async (long id, DownloadSubtitleRequest req, DbConnectionFactory db) =>
        {
            if (!HttpGuard.IsSafePublicUrl(req.Url, out var uri))
                return Results.BadRequest(new { ok = false, detail = "下载地址不合法" });
            await using var c = db.Create();
            await c.OpenAsync();
            var row = await c.QueryFirstOrDefaultAsync<(string? Folder, string? Source)>(
                "SELECT folder_path Folder, source_path Source FROM movies WHERE id=@id", new { id });
            var video = FindVideoFile(row.Folder)
                        ?? (!string.IsNullOrWhiteSpace(row.Source) && File.Exists(row.Source) ? row.Source : null);
            if (video == null)
                return Results.BadRequest(new { ok = false, detail = "未找到视频文件" });

            var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = ".srt";
            if (ext is not (".srt" or ".ass" or ".ssa" or ".vtt" or ".sub"))
                return Results.BadRequest(new { ok = false, detail = "不支持的字幕格式" });
            var dir = Path.GetDirectoryName(video)!;
            var stem = Path.GetFileNameWithoutExtension(video);
            var dest = Path.Combine(dir, $"{stem}{ext}");
            var temp = Path.Combine(dir, $".javideo-sub-{Guid.NewGuid():N}.tmp");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var response = await HttpGuard.GetAsync(Http, uri, timeout.Token);
                const int maxBytes = 8 * 1024 * 1024;
                if (response.Content.Headers.ContentLength > maxBytes)
                    throw new IOException("字幕文件超过 8 MB");
                await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    int count, total = 0;
                    while ((count = await source.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        total += count;
                        if (total > maxBytes) throw new IOException("字幕文件超过 8 MB");
                        await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    }
                    if (total == 0) throw new IOException("下载内容为空");
                }
                // ponytail: stage beside the video and rename without overwrite; concurrent saves get suffixes.
                for (var i = 2; ; i++)
                {
                    try { File.Move(temp, dest); break; }
                    catch (IOException) when (File.Exists(dest))
                    {
                        dest = Path.Combine(dir, $"{stem}-{i}{ext}");
                    }
                }
                return Results.Ok(new { ok = true, detail = "已保存: " + Path.GetFileName(dest) });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, detail = "下载失败: " + ex.Message });
            }
            finally
            {
                try { File.Delete(temp); }
                catch (Exception ex) { Serilog.Log.Warning(ex, "Failed to clean subtitle temporary file {File}", temp); }
            }
        });
    }

    /// <summary>GCID streaming reads the whole file — only do that for local
    /// fixed drives, never across a network/cloud mount.</summary>
    private static bool IsLocalDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return false;
            if (!root.StartsWith(@"\\")) // UNC = network
            {
                var drive = new DriveInfo(root);
                return drive.DriveType == DriveType.Fixed;
            }
            return false;
        }
        catch { return false; }
    }

    private static string? FindVideoFile(string? folder) => MediaExtensions.FindFirstVideo(folder);
}

public sealed record SubtitleItem(string Url, string Ext, string Name, double Score, string Languages, long DurationMs, double? DurationDiffSec, bool GcidHit);
public sealed record DownloadSubtitleRequest(long Id, string Url);
