using System.IO.Compression;
using Javideo.Worker.Db;

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
        var tempZip = Path.Combine(Path.GetTempPath(), $"javideo-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (File.Exists(tempZip)) File.Delete(tempZip);

        using var archive = ZipFile.Open(tempZip, ZipArchiveMode.Create);

        // 1. SQLite database — copy first since the live file is locked by the
        //    worker process itself (SQLite holds an exclusive write lock).
        var tempDb = Path.Combine(Path.GetTempPath(), $"javideo-db-{Guid.NewGuid():N}.db");
        File.Copy(_db.DbPath, tempDb, overwrite: true);
        archive.CreateEntryFromFile(tempDb, "library.db");

        // 2. Cached actor avatars.
        AddDirectory(archive, _db.AvatarsDir, "actors/");

        // 3. Cached preview images.
        var previewsDir = Path.Combine(_db.DataDir, "previews");
        AddDirectory(archive, previewsDir, "previews/");

        // 4. Settings are inside library.db, no separate file needed.

        // Clean up the temp db copy (zip already read it).
        try { File.Delete(tempDb); } catch { }

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

            // Validate: must contain library.db.
            if (!File.Exists(Path.Combine(staging, "library.db")))
                throw new InvalidDataException("备份文件无效:缺少 library.db");

            // Move library.db.
            var srcDb = Path.Combine(staging, "library.db");
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
