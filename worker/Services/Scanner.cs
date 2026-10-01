using System.Text.RegularExpressions;
using Dapper;
using Javideo.Worker.Db;
using Javideo.Worker.Endpoints;

namespace Javideo.Worker.Services;

/// <summary>
/// Scans a library's directories for media files with canonical 番号 naming.
/// File types follow the shared MediaExtensions list (mp4 / mkv / strm /
/// avi / wmv / mov / ts / m4v). Unavailable directories are skipped and logged.
/// </summary>
public sealed class Scanner
{
    private readonly DbConnectionFactory _db;

    // Canonical 番号 patterns — tried in order, first match wins.
    // Index matters: RecognizeNumber branches on it.
    private static readonly Regex[] Patterns =
    [
        // 0: FC2: FC2-PPV-4159520 / FC2PPV_4159520 / fc2-ppv-4159520.
        // Must run first — the generic label pattern would otherwise match the
        // "PPV" tail and truncate the id.
        new(@"FC2[-_ ]?PPV[-_ ]?(\d{5,10})", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // 1: FC2 bare (no PPV segment): FC2_4159520.
        new(@"FC2[-_ ](\d{5,10})", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // 2: standard with hyphen, label = letter-starting alnum token that may
        // contain digits (SSIS-456, T28-058, S2MBD-006). Trailing markers like
        // "-C" (中字) are dropped because we build the result from the groups.
        new(@"\b([A-Za-z][A-Za-z0-9]{1,7})-(\d{2,6})(?![A-Za-z0-9])", RegexOptions.Compiled),
        // 3: standard without hyphen, letters-only label (SSIS001 → SSIS-001,
        // HUNT801C → HUNT-801, START165C → START-165, ABP123CH → ABP-123). Up
        // to two trailing marker letters (C/U/CH — 中文/无码, recorded as tags)
        // are allowed after the digits; ≥3 letters keeps n0646-family names
        // (single letter + digits) away. RB007 keeps its own pattern below.
        new(@"\b([A-Za-z]{3,8})(\d{2,6})[A-Za-z]{0,2}(?![A-Za-z0-9])", RegexOptions.Compiled),
        // 4: sub-series letter: 2-6 letters + hyphen + 1 letter + digits (MKBD-S03, MKD-S93)
        new(@"([A-Za-z]{2,6})-([A-Za-z])(\d{2,5})", RegexOptions.Compiled),
        // 5: single-letter prefix + 3-5 digits (n0646, N0919). \b so it only
        // matches at a word start — mid-word "B007" out of "RB007" must not win.
        new(@"\b([A-Za-z])0?(\d{3,5})", RegexOptions.Compiled),
        // 6: two-letter prefix + digits (RB007, LB0009, SR138)
        new(@"([A-Za-z]{2})0?(\d{3,5})", RegexOptions.Compiled),
        // 7: date-number uncensored (092211-813, 050426_001)
        new(@"(\d{6})[-_](\d{2,3})", RegexOptions.Compiled),
    ];

    public Scanner(DbConnectionFactory db) => _db = db;

    public record ScanResult(int AvailableDirs, int SkippedDirs, List<ScannedFile> Files, List<string> Logs);
    public record ScannedFile(string Number, string FilePath, string FileName);

    public async Task<ScanResult> ScanLibraryAsync(long libraryId)
    {
        await using var c = _db.Create();
        await c.OpenAsync();
        var dirs = (await c.QueryAsync<string>(
            "SELECT path FROM library_directories WHERE library_id=@id", new { id = libraryId })).ToList();

        var files = new List<ScannedFile>();
        var logs = new List<string>();
        int available = 0, skipped = 0;

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
            {
                skipped++;
                logs.Add($"[跳过] 目录不可用: {dir}");
                continue;
            }
            available++;
            try
            {
                foreach (var file in EnumerateMedia(dir))
                {
                    var num = RecognizeNumber(file.Name);
                    if (num != null)
                        files.Add(new ScannedFile(num, file.FullName, file.Name));
                    else
                        logs.Add($"[未识别] {file.FullName} — 未匹配到番号");
                }
            }
            catch (Exception ex)
            {
                logs.Add($"[错误] 扫描 {dir} 失败: {ex.Message}");
            }
        }
        return new ScanResult(available, skipped, files, logs);
    }

    /// <summary>Recognize a 番号 from a file name. FC2 numbers are normalized
    /// to FC2-PPV-<id> (or FC2-<id>), standard numbers to LABEL-<id> (hyphen
    /// inserted, trailing markers like "-C" dropped); later patterns return
    /// the raw match.</summary>
    public static string? RecognizeNumber(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        for (var i = 0; i < Patterns.Length; i++)
        {
            var m = Patterns[i].Match(name);
            if (!m.Success) continue;
            return i switch
            {
                0 => $"FC2-PPV-{m.Groups[1].Value}",
                1 => $"FC2-{m.Groups[1].Value}",
                2 or 3 => $"{m.Groups[1].Value}-{m.Groups[2].Value}",
                _ => m.Value,
            };
        }
        return null;
    }

    private static IEnumerable<FileInfo> EnumerateMedia(string dir)
    {
        var di = new DirectoryInfo(dir);
        foreach (var fi in di.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            // Skip trailer files ({番号}-trailer.mp4) — they're generated by us.
            if (MediaExtensions.Values.Contains(fi.Extension) && !fi.Name.Contains("-trailer", StringComparison.OrdinalIgnoreCase))
                yield return fi;
        }
    }
}
