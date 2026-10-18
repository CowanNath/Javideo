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
    private readonly CloudIoService? _cloud;
    public LibraryService(DbConnectionFactory db, CloudIoService? cloud = null) { _db = db; _cloud = cloud; }

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
        await using var tx = c.BeginTransaction();
        var oldDirectories = (await c.QueryAsync<string>(
            "SELECT path FROM library_directories WHERE library_id=@id", new { id }, tx)).ToList();
        var directories = lib.Directories.Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.GetFullPath(d.Trim())).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var cacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(_db.DataDir, "cache")))
            + Path.DirectorySeparatorChar;
        var movies = await c.QueryAsync<(long Id, string? Folder, string? Source, string? SourceFolder)>(
            "SELECT id Id, folder_path Folder, source_path Source, source_folder_path SourceFolder FROM movies WHERE library_id=@id", new { id }, tx);
        Task<string?> Relocate(string? path, bool folder) => RelocatePath(path, oldDirectories, directories, folder, p =>
            _cloud == null ? Task.FromResult(folder ? Directory.Exists(p) : File.Exists(p))
                : _cloud.RunAsync(id, () => Task.Run(() => folder ? Directory.Exists(p) : File.Exists(p)), force: lib.CacheLocal));
        foreach (var movie in movies)
        {
            // Preserve actual cached metadata, not every folder in a library
            // whose cache option was just enabled.
            var cached = !string.IsNullOrWhiteSpace(movie.Folder)
                && Path.GetFullPath(movie.Folder).StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase);
            var folder = cached ? movie.Folder : await Relocate(movie.Folder, true);
            var source = await Relocate(movie.Source, false);
            var sourceFolder = await Relocate(movie.SourceFolder, true);
            if (folder != movie.Folder || source != movie.Source || sourceFolder != movie.SourceFolder)
                await c.ExecuteAsync("UPDATE movies SET folder_path=@folder, source_path=@source, source_folder_path=@sourceFolder WHERE id=@id",
                    new { folder, source, sourceFolder, id = movie.Id }, tx);
        }
        await c.ExecuteAsync(
            "UPDATE libraries SET name=@name, metadata_source=@src, cache_local=@cacheLocal WHERE id=@id",
            new { name = lib.Name, src = lib.MetadataSource, cacheLocal = lib.CacheLocal, id }, tx);
        await c.ExecuteAsync("DELETE FROM library_directories WHERE library_id=@id", new { id }, tx);
        foreach (var d in directories)
            await c.ExecuteAsync("INSERT INTO library_directories(library_id, path) VALUES (@id, @p)",
                new { id, p = d }, tx);
        tx.Commit();
    }

    private static async Task<string?> RelocatePath(string? path, List<string> oldDirectories, List<string> directories, bool folder, Func<string, Task<bool>> exists)
    {
        if (string.IsNullOrWhiteSpace(path) || directories.Count == 0) return path;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var oldRoots = oldDirectories.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var root in oldRoots.Intersect(directories, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(root, path);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar)) return path;
        }
        var removed = oldRoots.Except(directories, StringComparer.OrdinalIgnoreCase).ToList();
        var added = directories.Except(oldRoots, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var oldRoot in removed)
        {
            var relative = Path.GetRelativePath(oldRoot, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) continue;
            // ponytail: a single explicit root replacement needs no cloud stat;
            // multiple roots still require an unambiguous existing match below.
            if (removed.Count == 1 && added.Count == 1)
                return Path.Combine(added[0], relative);
            foreach (var root in directories)
            {
                var candidate = Path.Combine(root, relative);
                if (await exists(candidate)) candidates.Add(candidate);
            }
        }
        if (candidates.Count == 1) return candidates.Single();
        if (candidates.Count > 1 || await exists(path)) return path;
        // Also recover records from earlier versions that already saved the
        // new root without updating movie paths. Bind only an unambiguous match.
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrWhiteSpace(leaf) || leaf is "." or "..") return path;
        foreach (var root in directories)
        {
            var candidate = Path.Combine(root, leaf);
            if (await exists(candidate)) candidates.Add(candidate);
            if (!folder && Path.GetDirectoryName(path) is { } parent)
            {
                var parentName = Path.GetFileName(Path.TrimEndingDirectorySeparator(parent));
                if (string.IsNullOrWhiteSpace(parentName) || parentName is "." or "..") continue;
                candidate = Path.Combine(root, parentName, leaf);
                if (await exists(candidate)) candidates.Add(candidate);
            }
        }
        return candidates.Count == 1 ? candidates.Single() : path;
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
