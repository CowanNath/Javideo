using System.IO.Compression;
using Javideo.Worker.Db;
using Microsoft.Data.Sqlite;

namespace Javideo.Worker.Services;

/// <summary>
/// Backup / restore of all user data: the SQLite database, cached actor
/// avatars (actors/) and cached preview images (previews/). Uses the standard
/// library ZipFile — no third-party dependency.
/// </summary>
public sealed class BackupService
{
    private readonly DbConnectionFactory _db;
    public BackupService(DbConnectionFactory db) => _db = db;

    /// <summary>Export all user data to a temp zip file and return its path.
    /// The caller (endpoint) streams it to the client and deletes the temp.</summary>
    public string Export()
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

        // 4. Settings are inside library.db, no separate file needed.

        return tempZip;
    }

    /// <summary>Import a zip (uploaded by the user) by extracting its contents
    /// into the data directory. Existing files are overwritten. The caller
    /// should restart the worker afterwards so the DB reconnects.</summary>
    public void Import(string zipPath)
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

            // Close pooled connections so no open handle corrupts the copy,
            // and keep a one-shot backup of the current db — import overwrites
            // everything, this is the only way back.
            SqliteConnection.ClearAllPools();
            if (File.Exists(_db.DbPath))
                File.Copy(_db.DbPath, _db.DbPath + ".bak", overwrite: true);
            File.Copy(srcDb, _db.DbPath, overwrite: true);

            // Move actors/ and previews/ directories.
            CopyDirOverwrite(Path.Combine(staging, "actors"), _db.AvatarsDir);
            CopyDirOverwrite(Path.Combine(staging, "previews"), Path.Combine(_db.DataDir, "previews"));
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
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
