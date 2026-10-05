using Dapper;
using Javideo.Worker.Db;
using Javideo.Worker.Endpoints;
using Javideo.Worker.Models;
using System.Text.RegularExpressions;

namespace Javideo.Worker.Services;

/// <summary>User-owned highlights are separate from the disposable scrape cache.</summary>
public sealed class HighlightService
{
    public const long MaxUploadBytes = 256L * 1024 * 1024;
    public const int MaxAssets = 12;
    private readonly DbConnectionFactory _db;
    // Also held by backup and movie deletion: database snapshots and attachments agree.
    public SemaphoreSlim MutationGate { get; } = new(1, 1);
    public string DirectoryPath => Path.Combine(_db.DataDir, "highlights");
    public HighlightService(DbConnectionFactory db) => _db = db;

    private const string Columns = "id Id, movie_id MovieId, title Title, note Note, start_seconds StartSeconds, end_seconds EndSeconds, source_file_name SourceFileName";
    private const string AssetColumns = "id Id, highlight_id HighlightId, kind Kind, original_name OriginalName, storage_name StorageName, content_type ContentType";

    public async Task<List<MovieHighlight>?> ListAsync(long movieId)
    {
        await using var c = _db.Create();
        await c.OpenAsync();
        if (!await c.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM movies WHERE id=@movieId)", new { movieId })) return null;
        var records = (await c.QueryAsync<MovieHighlight>($"SELECT {Columns} FROM movie_highlights WHERE movie_id=@movieId ORDER BY start_seconds IS NULL, start_seconds, id", new { movieId })).ToList();
        var assets = (await c.QueryAsync<MovieHighlightAsset>($"SELECT {AssetColumns} FROM movie_highlight_assets WHERE movie_id=@movieId ORDER BY sort_order, id", new { movieId })).ToLookup(a => a.HighlightId);
        foreach (var record in records)
        {
            record.Assets = assets[record.Id].ToList();
            foreach (var asset in record.Assets)
                asset.Url = $"/api/movies/{movieId}/highlights/{record.Id}/assets/{asset.Id}";
        }
        return records;
    }

    public async Task<MovieHighlight?> SaveAsync(long movieId, long? id, SaveHighlightRequest req, IFormFileCollection files, CancellationToken ct)
    {
        req.Title = Clean(req.Title);
        req.Note = Clean(req.Note);
        req.SourceFileName = Clean(req.SourceFileName);
        if ((req.Title?.Length ?? 0) > 120 || (req.Note?.Length ?? 0) > 4000)
            throw new InvalidDataException("标题最多 120 字，备注最多 4000 字。");
        if (req.StartSeconds is < 0 or > 359999 || req.EndSeconds is < 0 or > 359999 ||
            req.EndSeconds != null && (req.StartSeconds == null || req.EndSeconds <= req.StartSeconds))
            throw new InvalidDataException("结束时间必须晚于开始时间，时间须在 0–99:59:59 之间。");
        if (req.SourceFileName != null && (req.SourceFileName.Length > 255 ||
            req.SourceFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || req.SourceFileName is "." or ".."))
            throw new InvalidDataException("无效的视频文件名。");
        if (req.KeepAssetIds == null || req.KeepAssetIds.Count > MaxAssets || files.Count > MaxAssets ||
            req.KeepAssetIds.Distinct().Count() + files.Count > MaxAssets || files.Sum(f => f.Length) > MaxUploadBytes)
            throw new InvalidDataException("每条记录最多 12 个附件，每次导入总大小不超过 256 MB。");

        await MutationGate.WaitAsync(ct);
        var newFiles = new List<string>();
        var committed = false;
        try
        {
            await using var c = _db.Create();
            await c.OpenAsync(ct);
            await using var tx = c.BeginTransaction();
            if (!await c.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM movies WHERE id=@movieId)", new { movieId }, tx)) return null;
            if (id != null && !await c.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM movie_highlights WHERE id=@id AND movie_id=@movieId)", new { id, movieId }, tx)) return null;
            var oldAssets = id == null ? new List<MovieHighlightAsset>() : (await c.QueryAsync<MovieHighlightAsset>(
                $"SELECT {AssetColumns} FROM movie_highlight_assets WHERE highlight_id=@id AND movie_id=@movieId", new { id, movieId }, tx)).ToList();
            var kept = req.KeepAssetIds.Distinct().ToList();
            if (kept.Any(assetId => oldAssets.All(a => a.Id != assetId)))
                throw new InvalidDataException("附件不属于这条记录。");
            if (req.Title == null && req.Note == null && req.StartSeconds == null && kept.Count == 0 && files.Count == 0)
                throw new InvalidDataException("请添加时间、备注、图片或视频中的至少一项。");

            var recordId = id ?? await c.ExecuteScalarAsync<long>(
                "INSERT INTO movie_highlights(movie_id) VALUES(@movieId) RETURNING id", new { movieId }, tx);
            await c.ExecuteAsync("""
                UPDATE movie_highlights SET title=@Title, note=@Note, start_seconds=@StartSeconds,
                    end_seconds=@EndSeconds, source_file_name=@SourceFileName, updated_at=datetime('now') WHERE id=@recordId
                """, new { req.Title, req.Note, req.StartSeconds, req.EndSeconds, req.SourceFileName, recordId }, tx);

            var order = kept.Count;
            foreach (var file in files)
            {
                var (kind, contentType, extension) = await InspectFileAsync(file, ct);
                var storageName = $"{Guid.NewGuid():N}{extension}";
                var path = AssetPath(movieId, storageName)!;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                newFiles.Add(temp);
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await file.CopyToAsync(output, ct);
                File.Move(temp, path);
                newFiles.Add(path);
                var originalName = Path.GetFileName(file.FileName);
                await c.ExecuteAsync("""
                    INSERT INTO movie_highlight_assets(movie_id,highlight_id,kind,original_name,storage_name,content_type,sort_order)
                    VALUES(@movieId,@recordId,@kind,@originalName,@storageName,@contentType,@order)
                    """, new { movieId, recordId, kind, originalName, storageName, contentType, order }, tx);
                order++;
            }
            for (var i = 0; i < kept.Count; i++)
                await c.ExecuteAsync("UPDATE movie_highlight_assets SET sort_order=@i WHERE id=@assetId", new { i, assetId = kept[i] }, tx);
            var removed = oldAssets.Where(a => !kept.Contains(a.Id)).ToList();
            foreach (var asset in removed)
                await c.ExecuteAsync("DELETE FROM movie_highlight_assets WHERE id=@Id", asset, tx);
            tx.Commit();
            committed = true;
            foreach (var asset in removed) TryDelete(AssetPath(movieId, asset.StorageName));
            return (await ListAsync(movieId))!.First(r => r.Id == recordId);
        }
        finally
        {
            if (!committed) foreach (var path in newFiles) TryDelete(path);
            MutationGate.Release();
        }
    }

    public async Task<bool> DeleteAsync(long movieId, long id)
    {
        await MutationGate.WaitAsync();
        try
        {
            await using var c = _db.Create();
            await c.OpenAsync();
            await using var tx = c.BeginTransaction();
            var assets = (await c.QueryAsync<string>("SELECT storage_name FROM movie_highlight_assets WHERE movie_id=@movieId AND highlight_id=@id", new { movieId, id }, tx)).ToList();
            await c.ExecuteAsync("DELETE FROM movie_highlight_assets WHERE movie_id=@movieId AND highlight_id=@id", new { movieId, id }, tx);
            var deleted = await c.ExecuteAsync("DELETE FROM movie_highlights WHERE movie_id=@movieId AND id=@id", new { movieId, id }, tx);
            tx.Commit();
            foreach (var asset in assets) TryDelete(AssetPath(movieId, asset));
            return deleted != 0;
        }
        finally { MutationGate.Release(); }
    }

    public async Task<(string Path, string ContentType)?> GetAssetAsync(long movieId, long id, long assetId)
    {
        await using var c = _db.Create();
        await c.OpenAsync();
        var asset = await c.QueryFirstOrDefaultAsync<MovieHighlightAsset>($"SELECT {AssetColumns} FROM movie_highlight_assets WHERE movie_id=@movieId AND highlight_id=@id AND id=@assetId", new { movieId, id, assetId });
        var path = asset == null ? null : AssetPath(movieId, asset.StorageName);
        // Derive MIME from the restricted extension, including after restoring a backup.
        var contentType = Path.GetExtension(path) switch
        {
            ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp",
            ".mp4" or ".m4v" => "video/mp4", ".mov" => "video/quicktime", ".webm" => "video/webm",
            ".mkv" => "video/x-matroska", ".avi" => "video/x-msvideo", _ => "application/octet-stream"
        };
        return path != null && File.Exists(path) ? (path, contentType) : null;
    }

    public async Task<List<string>?> VideoFilesAsync(long movieId)
    {
        await using var c = _db.Create();
        await c.OpenAsync();
        var movie = await c.QueryFirstOrDefaultAsync<Movie>("SELECT folder_path FolderPath, source_path SourcePath, library_id LibraryId, number Number FROM movies WHERE id=@movieId", new { movieId });
        if (movie == null) return null;
        var paths = new List<string?> { movie.FolderPath, Path.GetDirectoryName(movie.SourcePath) };
        if (movie.SourcePath == null && movie.LibraryId != null)
        {
            var roots = await c.QueryAsync<string>("SELECT path FROM library_directories WHERE library_id=@id", new { id = movie.LibraryId });
            paths.AddRange(roots.Select(p => Path.Combine(p, IngestService.SafeFolder(movie.Number))));
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(path)) foreach (var file in Directory.EnumerateFiles(path!))
                    if (!file.Contains("-trailer", StringComparison.OrdinalIgnoreCase) && MediaExtensions.Values.Contains(Path.GetExtension(file)))
                        names.Add(Path.GetFileName(file));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void RemoveMovieFiles(long movieId)
    {
        if (movieId <= 0) return;
        var path = Path.GetFullPath(Path.Combine(DirectoryPath, movieId.ToString()));
        if (!path.StartsWith(Path.GetFullPath(DirectoryPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Serilog.Log.Warning(ex, "Could not remove highlight files for {MovieId}", movieId); }
    }

    private string? AssetPath(long movieId, string name)
    {
        if (movieId <= 0 || !IsStorageName(name)) return null;
        var dir = Path.GetFullPath(Path.Combine(DirectoryPath, movieId.ToString()));
        var path = Path.GetFullPath(Path.Combine(dir, name));
        return path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static void TryDelete(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Serilog.Log.Warning(ex, "Could not remove highlight attachment {Path}", path); }
    }
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static bool IsStorageName(string name) => Regex.IsMatch(name, @"\A[a-f0-9]{32}\.(jpg|jpeg|png|gif|webp|mp4|m4v|mov|webm|mkv|avi)\z");

    private static async Task<(string Kind, string ContentType, string Extension)> InspectFileAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0 || file.Length > MaxUploadBytes || file.FileName.Length > 255)
            throw new InvalidDataException("附件为空、文件名过长或大小超过 256 MB。");
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var header = new byte[16];
        await using var stream = file.OpenReadStream();
        var length = await stream.ReadAtLeastAsync(header, 12, throwOnEndOfStream: false, cancellationToken: ct);
        var jpg = length >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff;
        var png = length >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var gif = length >= 6 && (header.AsSpan(0, 6).SequenceEqual("GIF87a"u8) || header.AsSpan(0, 6).SequenceEqual("GIF89a"u8));
        var riff = length >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8);
        var webp = riff && header.AsSpan(8, 4).SequenceEqual("WEBP"u8);
        var avi = riff && header.AsSpan(8, 4).SequenceEqual("AVI "u8);
        var mp4 = length >= 12 && header.AsSpan(4, 4).SequenceEqual("ftyp"u8);
        var ebml = length >= 4 && header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 });
        return ext switch
        {
            ".jpg" or ".jpeg" when jpg => ("image", "image/jpeg", ext),
            ".png" when png => ("image", "image/png", ext),
            ".gif" when gif => ("image", "image/gif", ext),
            ".webp" when webp => ("image", "image/webp", ext),
            ".mp4" or ".m4v" when mp4 => ("video", "video/mp4", ext),
            ".mov" when mp4 => ("video", "video/quicktime", ext),
            ".webm" when ebml => ("video", "video/webm", ext),
            ".mkv" when ebml => ("video", "video/x-matroska", ext),
            ".avi" when avi => ("video", "video/x-msvideo", ext),
            _ => throw new InvalidDataException("请选择有效的 JPG、PNG、GIF、WebP 图片或 MP4、M4V、MOV、WebM、MKV、AVI 视频。")
        };
    }
}
