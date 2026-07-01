using Microsoft.Data.Sqlite;

namespace Javideo.Worker.Db;

/// <summary>
/// Owns the SQLite connection string + local storage paths. The database lives
/// in %AppData%/Javideo/library.db (single file, single-writer), and actor
/// avatars are downloaded to %AppData%/Javideo/actors/ so they don't depend on
/// the MetaTube server being reachable at display time.
/// </summary>
public sealed class DbConnectionFactory
{
    public string DataDir { get; }
    public string DbPath { get; }
    public string ConnectionString { get; }
    /// <summary>Folder for cached actor avatars (one file per actor).</summary>
    public string AvatarsDir { get; }

    public DbConnectionFactory(IConfiguration config)
    {
        // Allow override via settings; default to %AppData%/Javideo.
        var dataDir = config["Javideo:DataDir"];
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Javideo");
        }

        Directory.CreateDirectory(dataDir);
        DataDir = dataDir;
        DbPath = Path.Combine(dataDir, "library.db");
        ConnectionString = $"Data Source={DbPath}";
        AvatarsDir = Path.Combine(dataDir, "actors");
        Directory.CreateDirectory(AvatarsDir);
    }

    public SqliteConnection Create() => new(ConnectionString);
}
