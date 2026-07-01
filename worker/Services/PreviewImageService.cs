using Javideo.Worker.Db;

namespace Javideo.Worker.Services;

/// <summary>
/// Caches movie preview/trailer images to disk so the detail drawer doesn't
/// depend on the upstream MetaTube/dmm URLs at display time. One folder per
/// movie under %AppData%/Javideo/previews/{movieId}/, files named 0.jpg, 1.jpg…
/// </summary>
public sealed class PreviewImageService
{
    private readonly DbConnectionFactory _db;
    private readonly HttpClient _http;

    public PreviewImageService(DbConnectionFactory db, HttpClient http)
    {
        _db = db;
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public string DirFor(long movieId) => Path.Combine(_db.DataDir, "previews", movieId.ToString());

    /// <summary>Download every preview URL into previews/{movieId}/{n}.jpg (n
    /// from 0) and return how many were cached. Existing files are kept.</summary>
    public async Task<int> CacheAsync(long movieId, IEnumerable<string> urls)
    {
        var dir = DirFor(movieId);
        Directory.CreateDirectory(dir);
        int n = 0, cached = 0;
        foreach (var url in urls)
        {
            var path = Path.Combine(dir, $"{n}.jpg");
            if (!File.Exists(path))
            {
                try
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        using var resp = await _http.GetAsync(uri);
                        if (resp.IsSuccessStatusCode)
                        {
                            await using var fs = File.Create(path);
                            await resp.Content.CopyToAsync(fs);
                            cached++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Failed to cache preview {N} for movie {Id}", n, movieId);
                }
            }
            else { cached++; }
            n++;
        }
        return cached;
    }

    /// <summary>Sorted list of cached preview files for a movie (by name).</summary>
    public string[] ListFiles(long movieId)
    {
        var dir = DirFor(movieId);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.jpg").OrderBy(f => f, new NaturalFileNameComparer()).ToArray();
    }

    public int Count(long movieId) => ListFiles(movieId).Length;

    /// <summary>Path of the n-th cached file (0-based), in sorted order. Works
    /// regardless of whether files start at 0.jpg or 1.jpg.</summary>
    public string? PathFor(long movieId, int index)
    {
        var files = ListFiles(movieId);
        return (index >= 0 && index < files.Length) ? files[index] : null;
    }
}

// Natural sort by file name (so "2.jpg" < "10.jpg", not "10.jpg" < "2.jpg").
internal sealed class NaturalFileNameComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return 1;
        if (y == null) return -1;
        return StringComparer.OrdinalIgnoreCase.Compare(
            Path.GetFileNameWithoutExtension(x).PadLeft(6, '0'),
            Path.GetFileNameWithoutExtension(y).PadLeft(6, '0'));
    }
}
