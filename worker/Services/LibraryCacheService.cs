using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using Javideo.Worker.Db;
using Javideo.Worker.Endpoints;
using Javideo.Worker.Models;

namespace Javideo.Worker.Services;

/// <summary>Incremental, restartable copies of library metadata. Originals are never moved.</summary>
public sealed class LibraryCacheService(DbConnectionFactory db, LibraryService libraries, CloudIoService cloud,
    PreviewImageService previews) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<long, Job> _jobs = new();
    private readonly SemaphoreSlim _schedule = new(1, 1);
    private sealed record Job(long LibraryId, string Id, CancellationTokenSource Cancel);
    private sealed record Entry(string Source, string Destination, long Length, DateTime Modified);
    private sealed record MovieRow(long Id, string Number, string? Folder, string? Source, string? SourceFolder, string? PreviewJson);
    private sealed class Folder(string source, string destination)
    {
        public string Source { get; } = source;
        public string Destination { get; } = destination;
        public string? Video { get; set; }
        public List<Entry> Files { get; } = new();
    }

    public async Task<LibraryCacheStatus?> EnqueueAsync(long libraryId)
    {
        await _schedule.WaitAsync();
        try
        {
            if ((await libraries.GetAsync(libraryId))?.CacheLocal != true)
            {
                await CancelCoreAsync(libraryId);
                return null;
            }
            var job = new Job(libraryId, Guid.NewGuid().ToString("N"), new());
            var status = new LibraryCacheStatus("queued");
            await using var c = db.Create();
            await c.OpenAsync();
            if (await c.ExecuteAsync("UPDATE libraries SET cache_job_id=@jobId,cache_status=@status WHERE id=@libraryId AND cache_local=1",
                new { jobId = job.Id, status = JsonSerializer.Serialize(status, Json), libraryId }) == 0)
            { job.Cancel.Dispose(); return null; }
            Add(job);
            return status;
        }
        finally { _schedule.Release(); }
    }

    public async Task CancelAsync(long libraryId)
    {
        await _schedule.WaitAsync();
        try { await CancelCoreAsync(libraryId); }
        finally { _schedule.Release(); }
    }

    private async Task CancelCoreAsync(long libraryId)
    {
        if (_jobs.TryRemove(libraryId, out var previous)) Cancel(previous);
        await using var c = db.Create();
        await c.OpenAsync();
        await c.ExecuteAsync("UPDATE libraries SET cache_job_id=NULL,cache_status=NULL WHERE id=@libraryId", new { libraryId });
    }

    public async Task<Dictionary<long, LibraryCacheStatus?>> StatusesAsync()
    {
        await using var c = db.Create();
        await c.OpenAsync();
        return (await c.QueryAsync<(long Id, string Status)>("SELECT id Id,cache_status Status FROM libraries WHERE cache_status IS NOT NULL"))
            .ToDictionary(r => r.Id, r => JsonSerializer.Deserialize<LibraryCacheStatus>(r.Status, Json));
    }

    private void Add(Job job)
    {
        _jobs.AddOrUpdate(job.LibraryId, job, (_, previous) => { Cancel(previous); return job; });
        _queue.Writer.TryWrite(job);
    }

    private static void Cancel(Job job)
    {
        try { job.Cancel.Cancel(); } catch (ObjectDisposedException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _schedule.WaitAsync(stoppingToken);
        try
        {
            await using (var c = db.Create())
            {
                await c.OpenAsync(stoppingToken);
                var pending = await c.QueryAsync<(long Id, string JobId, string Status)>(
                    "SELECT id Id,cache_job_id JobId,cache_status Status FROM libraries WHERE cache_local=1 AND cache_job_id IS NOT NULL AND cache_status IS NOT NULL");
                foreach (var row in pending)
                    if (JsonSerializer.Deserialize<LibraryCacheStatus>(row.Status, Json)?.State is "queued" or "scanning" or "caching")
                        if (!_jobs.ContainsKey(row.Id)) Add(new(row.Id, row.JobId, new()));
            }
        }
        finally { _schedule.Release(); }
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancel.Token);
            var status = new LibraryCacheStatus("scanning");
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                var library = await libraries.GetAsync(job.LibraryId);
                if (library?.CacheLocal != true) continue;
                await PublishAsync(job, status);
                status = await CacheAsync(job, library, status, linked.Token);
                await PublishAsync(job, status with { State = status.FailedFiles == 0 ? "completed" : "failed" });
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                if (stoppingToken.IsCancellationRequested) await PublishAsync(job, new("queued"));
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Library metadata cache failed for {LibraryId}", job.LibraryId);
                await PublishAsync(job, status with { State = "failed", FailedFiles = status.FailedFiles + 1, LastError = ex.Message });
            }
            finally { job.Cancel.Dispose(); }
        }
    }

    private async Task PublishAsync(Job job, LibraryCacheStatus status)
    {
        await using var c = db.Create();
        await c.OpenAsync();
        await c.ExecuteAsync("UPDATE libraries SET cache_status=@status WHERE id=@id AND cache_job_id=@jobId",
            new { status = JsonSerializer.Serialize(status, Json), id = job.LibraryId, jobId = job.Id });
    }

    private async Task<LibraryCacheStatus> CacheAsync(Job job, Library library, LibraryCacheStatus status, CancellationToken ct)
    {
        var folders = new Dictionary<string, Folder>(StringComparer.OrdinalIgnoreCase);
        foreach (var configured in library.Directories)
        {
            ct.ThrowIfCancellationRequested();
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
            if (Inside(db.DataDir, root)) throw new IOException("媒体库目录不能使用应用数据目录。");
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..16];
            var destination = Path.GetFullPath(Path.Combine(db.DataDir, "cache", "libraries", library.Id.ToString(), key));
            var pending = new Queue<string>(); pending.Enqueue(root);
            while (pending.TryDequeue(out var path))
            {
                ct.ThrowIfCancellationRequested();
                var folder = new Folder(path, JoinInside(destination, Path.GetRelativePath(root, path)));
                try
                {
                    var children = await cloud.RunAsync(library.Id, () => Task.Run(() =>
                    {
                        var directories = new List<string>();
                        foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos())
                        {
                            ct.ThrowIfCancellationRequested();
                            if (item.LinkTarget != null || Inside(db.DataDir, item.FullName)) continue;
                            if (item is DirectoryInfo) directories.Add(item.FullName);
                            else if (item is FileInfo file)
                            {
                                if (IsMainVideo(file.Name)) { folder.Video ??= file.FullName; continue; }
                                folder.Files.Add(new(file.FullName, JoinInside(destination, Path.GetRelativePath(root, file.FullName)), file.Length, file.LastWriteTimeUtc));
                            }
                        }
                        return directories;
                    }, ct), ct);
                    folders[path] = folder;
                    foreach (var child in children) pending.Enqueue(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    status = status with { FailedFiles = status.FailedFiles + 1, LastError = ex.Message };
                }
            }
        }
        status = status with { State = "caching", TotalFiles = folders.Values.Sum(f => f.Files.Count) };
        await PublishAsync(job, status);
        await using var c = db.Create();
        await c.OpenAsync(ct);
        var movies = (await c.QueryAsync<MovieRow>(@"SELECT id Id,number Number,folder_path Folder,source_path Source,
            source_folder_path SourceFolder,preview_images PreviewJson FROM movies WHERE library_id=@id", new { id = library.Id })).ToList();
        var matches = movies.ToDictionary(m => m.Id, m => Match(m, folders));
        foreach (var movie in movies.Where(m => matches[m.Id] == null))
            status = status with { FailedFiles = status.FailedFiles + 1, LastError = $"{movie.Number}: 未找到对应的媒体目录。" };
        foreach (var folder in folders.Values)
        {
            ct.ThrowIfCancellationRequested();
            var failed = false;
            EnsureDirectory(folder.Destination);
            foreach (var file in folder.Files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var skipped = await CopyAsync(library.Id, file, ct);
                    status = status with
                    {
                        DoneFiles = status.DoneFiles + 1,
                        SkippedFiles = status.SkippedFiles + (skipped ? 1 : 0),
                        BytesCopied = status.BytesCopied + (skipped ? 0 : file.Length)
                    };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed = true;
                    status = status with { DoneFiles = status.DoneFiles + 1, FailedFiles = status.FailedFiles + 1, LastError = ex.Message };
                }
                await PublishAsync(job, status);
            }
            if (failed) continue; // Keep the previous metadata location until this folder has a complete copy.
            foreach (var movie in movies.Where(m => matches[m.Id] == folder))
            {
                ct.ThrowIfCancellationRequested();
                // Existing scraped metadata may exist only locally (e.g. a flat video-only cloud folder).
                // Carry missing files forward before switching the binding; remote copies take precedence.
                try
                {
                    if (movie.Folder != null && Inside(Path.Combine(db.DataDir, "cache"), movie.Folder)
                        && !movie.Folder.Equals(folder.Destination, StringComparison.OrdinalIgnoreCase) && Directory.Exists(movie.Folder))
                    {
                        if (Inside(movie.Folder, folder.Destination)) throw new IOException("新缓存不能嵌套在旧缓存内。");
                        foreach (var local in Directory.EnumerateFiles(movie.Folder, "*", new EnumerationOptions
                            { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (IsMainVideo(Path.GetFileName(local))) continue;
                            var target = JoinInside(folder.Destination, Path.GetRelativePath(movie.Folder, local));
                            if (File.Exists(target)) continue;
                            var info = new FileInfo(local);
                            status = status with { TotalFiles = status.TotalFiles + 1 };
                            await CopyAsync(0, new(local, target, info.Length, info.LastWriteTimeUtc), ct);
                            status = status with { DoneFiles = status.DoneFiles + 1, BytesCopied = status.BytesCopied + info.Length };
                            await PublishAsync(job, status);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    status = status with { FailedFiles = status.FailedFiles + 1, LastError = ex.Message };
                    continue;
                }
                await c.ExecuteAsync(@"UPDATE movies SET folder_path=@destination,source_folder_path=@sourceFolder,
                    source_path=CASE WHEN source_path IS NULL OR source_path='' THEN @video ELSE source_path END
                    WHERE id=@id AND library_id=@libraryId AND folder_path IS @oldFolder AND source_folder_path IS @oldSourceFolder
                    AND EXISTS(SELECT 1 FROM libraries WHERE id=@libraryId AND cache_local=1 AND cache_job_id=@jobId)",
                    new
                    {
                        destination = folder.Destination,
                        sourceFolder = folder.Source,
                        video = folder.Video,
                        id = movie.Id,
                        libraryId = library.Id,
                        oldFolder = movie.Folder,
                        oldSourceFolder = movie.SourceFolder,
                        jobId = job.Id
                    });
                // ponytail: reuse existing preview downloads; keep all successful cached images during a retry.
                if (!string.IsNullOrWhiteSpace(movie.PreviewJson))
                {
                    try
                    {
                        var urls = JsonSerializer.Deserialize<List<string>>(movie.PreviewJson) ?? new();
                        if (urls.Count > 0 && await previews.CacheAsync(movie.Id, urls, ct) < urls.Count)
                            status = status with { FailedFiles = status.FailedFiles + 1, LastError = $"{movie.Number}: 部分剧情截图未能缓存。" };
                    }
                    catch (JsonException ex) { status = status with { FailedFiles = status.FailedFiles + 1, LastError = ex.Message }; }
                }
            }
        }
        return status;
    }

    private Folder? Match(MovieRow movie, Dictionary<string, Folder> folders)
    {
        var source = movie.SourceFolder ?? (!string.IsNullOrWhiteSpace(movie.Folder) && !Inside(db.DataDir, movie.Folder)
            ? movie.Folder : string.IsNullOrWhiteSpace(movie.Source) ? null : Path.GetDirectoryName(movie.Source));
        if (source != null && folders.TryGetValue(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)), out var exact)) return exact;
        var matches = folders.Values.Where(f => Path.GetFileName(f.Source).Equals(IngestService.SafeFolder(movie.Number), StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private async Task<bool> CopyAsync(long libraryId, Entry entry, CancellationToken ct)
    {
        var existing = new FileInfo(entry.Destination);
        if (existing.Exists && existing.LinkTarget == null && existing.Length == entry.Length && existing.LastWriteTimeUtc == entry.Modified) return true;
        EnsureDirectory(Path.GetDirectoryName(entry.Destination)!);
        var temp = entry.Destination + ".javideo-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await cloud.RunAsync(libraryId, async () =>
            {
                if (new FileInfo(entry.Source).LinkTarget != null) throw new IOException("不能缓存符号链接文件。");
                await using (var source = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    var length = source.Length;
                    await source.CopyToAsync(output, ct);
                    if (output.Length != length) throw new IOException("缓存文件长度校验失败。");
                }
                ct.ThrowIfCancellationRequested();
                File.SetLastWriteTimeUtc(temp, entry.Modified);
                File.Move(temp, entry.Destination, overwrite: true);
                return true;
            }, ct);
            return false;
        }
        finally { try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    private static bool IsMainVideo(string name) =>
        !Path.GetFileNameWithoutExtension(name).Contains("-trailer", StringComparison.OrdinalIgnoreCase)
        && (MediaExtensions.Values.Contains(Path.GetExtension(name)) || Path.GetExtension(name).ToLowerInvariant() is
            ".iso" or ".m2ts" or ".mts" or ".mpg" or ".mpeg" or ".vob" or ".flv" or ".webm" or ".rmvb" or ".rm");

    private void EnsureDirectory(string path)
    {
        if (!Inside(Path.Combine(db.DataDir, "cache", "libraries"), path)) throw new IOException("缓存目录越界。");
        for (var parent = path; parent != null && Inside(db.DataDir, parent); parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && new DirectoryInfo(parent).LinkTarget != null) throw new IOException("缓存目录不能包含符号链接。");
        Directory.CreateDirectory(path);
    }

    private static bool Inside(string root, string path) => Path.GetFullPath(path).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string JoinInside(string root, string relative)
    {
        var destination = Path.GetFullPath(Path.Combine(root, relative));
        if (!Inside(root, destination)) throw new IOException("缓存路径越出目标目录。");
        return destination;
    }
}
