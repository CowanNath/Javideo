using Dapper;
using Javideo.Worker.Db;
using Javideo.Worker.Models;

namespace Javideo.Worker.Services;

/// <summary>
/// CRUD for media libraries and their directories.
/// </summary>
public sealed class LibraryService
{
    private readonly DbConnectionFactory _db;
    public LibraryService(DbConnectionFactory db) => _db = db;

    public async Task<List<Library>> ListAsync()
    {
        await using var c = _db.Create();
        await c.OpenAsync();
        var libs = (await c.QueryAsync<Library>(@"
            SELECT l.id Id, l.name Name, l.metadata_source MetadataSource,
                   l.cache_local CacheLocal,
                   (SELECT COUNT(*) FROM movies m WHERE m.library_id=l.id) MovieCount
            FROM libraries l ORDER BY l.id")).ToList();

        foreach (var l in libs)
            l.Directories = (await c.QueryAsync<string>(
                "SELECT path FROM library_directories WHERE library_id=@id", new { id = l.Id })).ToList();
        return libs;
    }

    public async Task<Library?> GetAsync(long id)
    {
        await using var c = _db.Create();
        await c.OpenAsync();
        var lib = await c.QueryFirstOrDefaultAsync<Library>(@"
            SELECT l.id Id, l.name Name, l.metadata_source MetadataSource, l.cache_local CacheLocal
            FROM libraries l WHERE l.id=@id", new { id });
        if (lib == null) return null;
        lib.Directories = (await c.QueryAsync<string>(
            "SELECT path FROM library_directories WHERE library_id=@id", new { id })).ToList();
        return lib;
    }

    public async Task<long> CreateAsync(Library lib)
    {
        await using var c = _db.Create();
        await c.OpenAsync();
        var id = await c.ExecuteScalarAsync<long>(@"
            INSERT INTO libraries(name, metadata_source, cache_local) VALUES (@name, @src, @cacheLocal);
            SELECT last_insert_rowid();",
            new { name = lib.Name, src = string.IsNullOrWhiteSpace(lib.MetadataSource) ? "metatube" : lib.MetadataSource, cacheLocal = lib.CacheLocal });

        foreach (var d in lib.Directories)
        {
            if (!string.IsNullOrWhiteSpace(d))
                await c.ExecuteAsync(
                    "INSERT INTO library_directories(library_id, path) VALUES (@id, @p)",
                    new { id, p = d });
        }
        return id;
    }

    public async Task UpdateAsync(long id, Library lib)
    {
        await using var c = _db.Create();
        await c.OpenAsync();
        await c.ExecuteAsync(
            "UPDATE libraries SET name=@name, metadata_source=@src, cache_local=@cacheLocal WHERE id=@id",
            new { name = lib.Name, src = lib.MetadataSource, cacheLocal = lib.CacheLocal, id });
        await c.ExecuteAsync("DELETE FROM library_directories WHERE library_id=@id", new { id });
        foreach (var d in lib.Directories)
        {
            if (!string.IsNullOrWhiteSpace(d))
                await c.ExecuteAsync(
                    "INSERT INTO library_directories(library_id, path) VALUES (@id, @p)",
                    new { id, p = d });
        }
    }

    public async Task DeleteAsync(long id)
    {
        await using var c = _db.Create();
        await c.OpenAsync();
        await c.ExecuteAsync("DELETE FROM libraries WHERE id=@id", new { id });
    }

    /// <summary>First usable (existing) directory of a library, or null.</summary>
    public async Task<string?> FirstAvailableDirectoryAsync(long libraryId)
    {
        var lib = await GetAsync(libraryId);
        if (lib == null) return null;
        return lib.Directories.FirstOrDefault(Directory.Exists);
    }
}

public static class LibraryServiceExtensions
{
    /// <summary>Whether the library is a cloud-drive library whose metadata is
    /// cached locally (videos stay on the remote drive).</summary>
    public static async Task<bool> IsCacheLocalAsync(this DbConnectionFactory db, long libraryId)
    {
        await using var c = db.Create();
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<long?>(
            "SELECT cache_local FROM libraries WHERE id=@id", new { id = libraryId }) == 1;
    }
}
