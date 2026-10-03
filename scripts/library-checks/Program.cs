using Dapper;
using Javideo.Worker.Db;
using Javideo.Worker.Models;
using Javideo.Worker.Services;
using Microsoft.Extensions.Configuration;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var db = new DbConnectionFactory(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Javideo:DataDir"] = Path.Combine(root, "data") }).Build());
await DbInitializer.InitializeAsync(db);
var service = new LibraryService(db);
void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
try
{
    var oldRoot = Path.Combine(root, "old");
    var newRoot = Path.Combine(root, "new");
    var oldFolder = Path.Combine(oldRoot, "nested", "QA-001");
    var newFolder = Path.Combine(newRoot, "nested", "QA-001");
    Directory.CreateDirectory(newFolder);
    File.WriteAllText(Path.Combine(newFolder, "QA-001-poster.jpg"), "poster");
    File.WriteAllText(Path.Combine(newFolder, "QA-001.mp4"), "video");
    var lib = new Library { Name = "local", Directories = new() { oldRoot } };
    var id = await service.CreateAsync(lib);
    await using var c = db.Create(); await c.OpenAsync();
    await c.ExecuteAsync("INSERT INTO movies(library_id,number,folder_path,source_path) VALUES(@id,'QA-001',@fp,@sp)", new { id, fp = oldFolder, sp = Path.Combine(oldFolder, "QA-001.mp4") });
    lib.Directories = new() { newRoot }; await service.UpdateAsync(id, lib);
    var record = await c.QuerySingleAsync<(string Folder, string Source)>("SELECT folder_path Folder,source_path Source FROM movies WHERE library_id=@id", new { id });
    Check(record.Folder == newFolder && record.Source == Path.Combine(newFolder, "QA-001.mp4"), "Directory edit must relocate nested movie and source paths");
    Check(File.Exists(Path.Combine(record.Folder, "QA-001-poster.jpg")), "Cover file must remain accessible");

    // Saving an already-changed library also repairs records from older versions.
    var stale = Path.Combine(root, "stale", "QA-002");
    var recovered = Path.Combine(newRoot, "QA-002"); Directory.CreateDirectory(recovered);
    await c.ExecuteAsync("INSERT INTO movies(library_id,number,folder_path) VALUES(@id,'QA-002',@fp)", new { id, fp = stale });
    await service.UpdateAsync(id, lib);
    Check(await c.ExecuteScalarAsync<string>("SELECT folder_path FROM movies WHERE number='QA-002'") == recovered, "Saving current directory must recover unique stale paths");

    var secondRoot = Path.Combine(root, "second"); Directory.CreateDirectory(Path.Combine(secondRoot, "QA-003")); Directory.CreateDirectory(Path.Combine(newRoot, "QA-003"));
    var ambiguous = Path.Combine(root, "stale", "QA-003");
    await c.ExecuteAsync("INSERT INTO movies(library_id,number,folder_path) VALUES(@id,'QA-003',@fp)", new { id, fp = ambiguous });
    lib.Directories = new() { newRoot, secondRoot }; await service.UpdateAsync(id, lib);
    Check(await c.ExecuteScalarAsync<string>("SELECT folder_path FROM movies WHERE number='QA-003'") == ambiguous, "Ambiguous matches must not rebind a movie");

    var cache = Path.Combine(db.DataDir, "cache", "QA-004"); Directory.CreateDirectory(cache);
    var oldCloud = Path.Combine(root, "old-cloud"); var newCloud = Path.Combine(root, "new-cloud");
    var newVideo = Path.Combine(newCloud, "QA-004", "original.mp4"); Directory.CreateDirectory(Path.GetDirectoryName(newVideo)!); File.WriteAllText(newVideo, "video");
    var cloud = new Library { Name = "cloud", CacheLocal = true, Directories = new() { oldCloud } }; var cloudId = await service.CreateAsync(cloud);
    await c.ExecuteAsync("INSERT INTO movies(library_id,number,folder_path,source_path) VALUES(@id,'QA-004',@fp,@sp)", new { id = cloudId, fp = cache, sp = Path.Combine(oldCloud, "QA-004", "original.mp4") });
    cloud.Directories = new() { newCloud }; await service.UpdateAsync(cloudId, cloud);
    var cloudRecord = await c.QuerySingleAsync<(string Folder, string Source)>("SELECT folder_path Folder,source_path Source FROM movies WHERE number='QA-004'");
    Check(cloudRecord.Folder == cache && cloudRecord.Source == newVideo, "Cloud root edit must preserve local metadata and relocate only video source");
    Console.WriteLine("PASS: nested directory migration, playable source, cover access, old-record repair, ambiguous paths and cloud metadata preservation");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Check(root.StartsWith(Path.GetFullPath(AppContext.BaseDirectory) + "fixtures-", StringComparison.OrdinalIgnoreCase), "Fixture cleanup boundary");
    Directory.Delete(root, recursive: true);
}
