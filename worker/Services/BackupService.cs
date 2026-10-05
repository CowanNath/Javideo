using System.IO.Compression;
using Javideo.Worker.Db;
using Microsoft.Data.Sqlite;
using Dapper;

namespace Javideo.Worker.Services;

/// <summary>
/// Backup / restore of all user data: the SQLite database, cached actor
/// avatars (actors/), preview images (previews/) and highlights. Uses the standard
/// library ZipFile — no third-party dependency.
/// </summary>
public sealed class BackupService
{
    private readonly DbConnectionFactory _db;
    private readonly HighlightService _highlights;
    public BackupService(DbConnectionFactory db, HighlightService highlights) { _db = db; _highlights = highlights; }

    /// <summary>Export all user data to a temp zip file and return its path.
    /// The caller (endpoint) streams it to the client and deletes the temp.</summary>
    public string Export()
    {
        _highlights.MutationGate.Wait();
        try { return ExportCore(); }
        finally { _highlights.MutationGate.Release(); }
    }

    private string ExportCore()
    {
        // Unique name — two exports in the same second must not fight over the
        // same file while one of them is still being streamed.
        var tempZip = Path.Combine(Path.GetTempPath(), $"javideo-backup-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");

        using var archive = ZipFile.Open(tempZip, ZipArchiveMode.Create);

        // 1. SQLite database — snapshot it via VACUUM INTO, which produces a
        //    consistent copy even while other endpoints are writing (a plain
        //    File.Copy of a live db can tear mid-transaction).
        var tempDb = Path.Combine(Path.GetTempPath(), $"javideo-db-{Guid.NewGuid():N}.db");
        try
        {
            using (var snap = new SqliteConnection($"Data Source={_db.DbPath}"))
            {
                snap.Open();
                using var cmd = snap.CreateCommand();
                cmd.CommandText = "VACUUM INTO @p";
                cmd.Parameters.AddWithValue("@p", tempDb);
                cmd.ExecuteNonQuery();
            }
            archive.CreateEntryFromFile(tempDb, "library.db");
        }
        finally
        {
            try { File.Delete(tempDb); } catch { }
        }

        // 2. Cached actor avatars.
        AddDirectory(archive, _db.AvatarsDir, "actors/");

        // 3. Cached preview images.
        var previewsDir = Path.Combine(_db.DataDir, "previews");
        AddDirectory(archive, previewsDir, "previews/");

        // 4. User-owned highlight attachments, including imported clips.
        AddDirectory(archive, _highlights.DirectoryPath, "highlights/");

        // Settings are inside library.db, no separate file needed.

        return tempZip;
    }

    /// <summary>Import a zip (uploaded by the user) by extracting its contents
    /// into the data directory. Existing files are overwritten. The caller
    /// should restart the worker afterwards so the DB reconnects.</summary>
    public void Import(string zipPath)
    {
        _highlights.MutationGate.Wait();
        try { ImportCore(zipPath); }
        finally { _highlights.MutationGate.Release(); }
    }

    private void ImportCore(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("备份文件不存在");

        // Extract to a temp staging dir first, validate, then move.
        var staging = Path.Combine(Path.GetTempPath(), $"javideo-import-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

            // Validate: must contain a real SQLite database.
            var srcDb = Path.Combine(staging, "library.db");
            if (!File.Exists(srcDb))
                throw new InvalidDataException("备份文件无效:缺少 library.db");
            if (!IsSqliteFile(srcDb))
                throw new InvalidDataException("备份文件无效:library.db 不是有效的 SQLite 数据库");

            ValidateHighlightFiles(srcDb, staging);

            // Keep a full, consistent recovery copy: a DB-only .bak cannot
            // recover user attachments overwritten by importing another backup.
            var snapshot = ExportCore();
            var previousBackup = Path.Combine(_db.DataDir, $"before-import-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
            File.Move(snapshot, previousBackup);

            try { RestoreFiles(staging); }
            catch
            {
                var rollback = Path.Combine(staging, "rollback");
                try
                {
                    ZipFile.ExtractToDirectory(previousBackup, rollback);
                    RestoreFiles(rollback);
                }
                catch (Exception ex)
                {
                    throw new IOException($"导入失败且自动恢复未完成，原数据完整备份保存在 {previousBackup}", ex);
                }
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private void RestoreFiles(string staging)
    {
        // Copy attachments first: interrupted writes cannot install a DB
        // whose newly referenced highlight files have not arrived yet.
        CopyDirOverwrite(Path.Combine(staging, "actors"), _db.AvatarsDir);
        CopyDirOverwrite(Path.Combine(staging, "previews"), Path.Combine(_db.DataDir, "previews"));
        CopyDirOverwrite(Path.Combine(staging, "highlights"), _highlights.DirectoryPath);
        // The complete before-import ZIP is the recovery copy, including files.
        SqliteConnection.ClearAllPools();
        File.Copy(Path.Combine(staging, "library.db"), _db.DbPath, overwrite: true);
    }

    private static void ValidateHighlightFiles(string dbPath, string staging)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        if (conn.ExecuteScalar<string>("PRAGMA quick_check") != "ok")
            throw new InvalidDataException("备份数据库损坏。");
        if (!conn.ExecuteScalar<bool>("SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='movie_highlight_assets')")) return;
        foreach (var asset in conn.Query<(long MovieId, string Name)>("SELECT movie_id, storage_name FROM movie_highlight_assets"))
        {
            if (asset.MovieId <= 0 || !HighlightService.IsStorageName(asset.Name) ||
                !File.Exists(Path.Combine(staging, "highlights", asset.MovieId.ToString(), asset.Name)))
                throw new InvalidDataException("备份文件无效：精彩记录附件缺失或路径非法。");
        }
    }

    // --- helpers ---

    /// <summary>SQLite files start with the 16-byte magic header.</summary>
    private static bool IsSqliteFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[16];
            if (fs.Read(header) < header.Length) return false;
            return "SQLite format 3\0"u8.SequenceEqual(header);
        }
        catch { return false; }
    }

    private static void AddIfExists(ZipArchive archive, string filePath, string entryName)
    {
        if (File.Exists(filePath))
            archive.CreateEntryFromFile(filePath, entryName);
    }

    private static void AddDirectory(ZipArchive archive, string dir, string prefix)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = prefix + Path.GetRelativePath(dir, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, rel);
        }
    }

    private static void CopyDirOverwrite(string src, string dst)
    {
        if (!Directory.Exists(src)) return;
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
