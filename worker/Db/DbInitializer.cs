using Dapper;

namespace Javideo.Worker.Db;

/// <summary>
/// Creates/migrates the SQLite schema on startup.
/// </summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(DbConnectionFactory factory)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync();

        // WAL: concurrent readers (image/grid requests) no longer block on the
        // writer and vice versa. journal_mode is persistent for the db file.
        await conn.ExecuteAsync("PRAGMA journal_mode=WAL;");

        // Libraries: a user-defined media library (name + metadata source + directories).
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS libraries (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT NOT NULL,
                metadata_source TEXT NOT NULL DEFAULT 'metatube',
                created_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
        """);

        // A library can have multiple directories.
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS library_directories (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                library_id  INTEGER NOT NULL,
                path        TEXT NOT NULL,
                FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE
            );
        """);

        // Movies (scraped + ingested).
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS movies (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                library_id      INTEGER,
                number          TEXT NOT NULL,          -- 番号 e.g. SSIS-001
                title           TEXT,
                original_title  TEXT,
                summary         TEXT,
                maker           TEXT,                   -- 厂商
                label           TEXT,
                series          TEXT,                   -- 系列
                director        TEXT,
                release_date    TEXT,
                runtime_minutes INTEGER,
                cover_url       TEXT,
                thumb_url       TEXT,
                score           REAL,
                provider        TEXT,
                homepage_url    TEXT,
                folder_path     TEXT,                   -- where nfo/poster/thumb were written
                preview_images  TEXT,                   -- JSON array of preview image URLs
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE (number, library_id),
                FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE SET NULL
            );
        """);
        await conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_movies_library ON movies(library_id);");
        await conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_movies_number ON movies(number);");
        try { await conn.ExecuteAsync("ALTER TABLE movies ADD COLUMN preview_images TEXT;"); } catch { }
        // Cached Thunder GCID content hash (computed once per video file).
        try { await conn.ExecuteAsync("ALTER TABLE movies ADD COLUMN gcid TEXT;"); } catch { }
        // Cloud-drive library option: metadata cached locally, video stays remote.
        try { await conn.ExecuteAsync("ALTER TABLE libraries ADD COLUMN cache_local INTEGER NOT NULL DEFAULT 0;"); } catch { }
        // Where the video file actually lives (set for cache_local libraries —
        // the video stays on the cloud drive while metadata is cached locally).
        try { await conn.ExecuteAsync("ALTER TABLE movies ADD COLUMN source_path TEXT;"); } catch { }

        // Actors.
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS actors (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT NOT NULL UNIQUE,
                avatar_url  TEXT
            );
        """);

        // Movie <-> Actor many-to-many.
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS movie_actors (
                movie_id INTEGER NOT NULL,
                actor_id INTEGER NOT NULL,
                manual   INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (movie_id, actor_id),
                FOREIGN KEY (movie_id) REFERENCES movies(id) ON DELETE CASCADE,
                FOREIGN KEY (actor_id) REFERENCES actors(id) ON DELETE CASCADE
            );
        """);

        // manual=1 → the actor link was added/kept by the user; rescrape only
        // rebuilds the non-manual (MetaTube) links.
        // Existing databases also need the column; run this after table creation.
        try { await conn.ExecuteAsync("ALTER TABLE movie_actors ADD COLUMN manual INTEGER NOT NULL DEFAULT 0;"); } catch { }

        // Tags. category: genre | series | maker | custom.
        // is_standard: 1 for standard-library tag, 0 for non-standard (custom) library.
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS tags (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT NOT NULL,
                category    TEXT NOT NULL DEFAULT 'genre',
                is_standard INTEGER NOT NULL DEFAULT 1
            );
        """);
        await conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_tags_name ON tags(name);");
        await conn.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS uq_tags_name_cat_std ON tags(name, category, is_standard);");

        // Movie <-> Tag many-to-many.
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS movie_tags (
                movie_id INTEGER NOT NULL,
                tag_id   INTEGER NOT NULL,
                PRIMARY KEY (movie_id, tag_id),
                FOREIGN KEY (movie_id) REFERENCES movies(id) ON DELETE CASCADE,
                FOREIGN KEY (tag_id)   REFERENCES tags(id)   ON DELETE CASCADE
            );
        """);

        // Favorites: target_type = movie | tag | actor.
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS favorites (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                target_type TEXT NOT NULL,
                target_id   INTEGER NOT NULL,
                created_at  TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE (target_type, target_id)
            );
        """);

        // Cached magnet results per movie (display-only; user downloads externally).
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS magnets (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                movie_id    INTEGER,
                query       TEXT,
                title       TEXT,
                size        TEXT,
                magnet_uri  TEXT NOT NULL,
                source      TEXT,
                found_at    TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (movie_id) REFERENCES movies(id) ON DELETE SET NULL
            );
        """);

        // Key/value settings (metatube address, timeout, player path, theme, etc.)
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT
            );
        """);
    }
}
