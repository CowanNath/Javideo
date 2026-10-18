using Dapper;
using Javideo.Worker.Db;
using Javideo.Worker.Models;
using Javideo.Worker.Services;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

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

    // Enabling cloud caching doesn't mean existing local records are cached.
    var localRoot = Path.Combine(root, "local-to-cloud");
    var mountRoot = Path.Combine(root, "mounted-cloud");
    var localFolder = Path.Combine(localRoot, "nested", "QA-005");
    var mountedFolder = Path.Combine(mountRoot, "nested", "QA-005");
    Directory.CreateDirectory(localFolder);
    Directory.CreateDirectory(mountedFolder);
    File.WriteAllText(Path.Combine(mountedFolder, "QA-005-poster.jpg"), "poster");
    File.WriteAllText(Path.Combine(mountedFolder, "original.mp4"), "video");
    var converted = new Library { Name = "converted", Directories = new() { localRoot } };
    var convertedId = await service.CreateAsync(converted);
    await c.ExecuteAsync("INSERT INTO movies(library_id,number,folder_path,source_path) VALUES(@id,'QA-005',@fp,@sp)",
        new { id = convertedId, fp = localFolder, sp = Path.Combine(localFolder, "original.mp4") });
    converted.CacheLocal = true;
    converted.Directories = new() { mountRoot };
    await service.UpdateAsync(convertedId, converted);
    var convertedRecord = await c.QuerySingleAsync<(string Folder, string Source)>("SELECT folder_path Folder,source_path Source FROM movies WHERE number='QA-005'");
    Check(convertedRecord.Folder == mountedFolder && convertedRecord.Source == Path.Combine(mountedFolder, "original.mp4"),
        "Local-to-cloud edit must relocate uncached metadata and video even while the old folder exists");
    Check(File.Exists(Path.Combine(convertedRecord.Folder, "QA-005-poster.jpg")), "Converted cloud cover must be readable");

    var staleCloudFolder = Path.Combine(root, "removed-local", "QA-006");
    var recoveredCloudFolder = Path.Combine(mountRoot, "QA-006");
    Directory.CreateDirectory(recoveredCloudFolder);
    await c.ExecuteAsync("INSERT INTO movies(library_id,number,folder_path) VALUES(@id,'QA-006',@fp)",
        new { id = convertedId, fp = staleCloudFolder });
    await service.UpdateAsync(convertedId, converted);
    Check(await c.ExecuteScalarAsync<string>("SELECT folder_path FROM movies WHERE number='QA-006'") == recoveredCloudFolder,
        "Saving an already-converted cloud library must repair uncached stale metadata paths");

    // Cached metadata remains readable even when local caching is disabled.
    cloud.CacheLocal = false;
    await service.UpdateAsync(cloudId, cloud);
    Check(await c.ExecuteScalarAsync<string>("SELECT folder_path FROM movies WHERE number='QA-004'") == cache,
        "Existing metadata cache must stay bound when disabling local caching");

    // A temporarily offline mount must not leave old paths behind after saving.
    var offlineRoot = Path.Combine(root, "offline-mount");
    var offlineFolder = Path.Combine(offlineRoot, "nested", "QA-007");
    var originalFolder = Path.Combine(localRoot, "nested", "QA-007");
    Directory.CreateDirectory(originalFolder);
    var offline = new Library { Name = "offline", Directories = new() { localRoot } };
    var offlineId = await service.CreateAsync(offline);
    await c.ExecuteAsync("INSERT INTO movies(library_id,number,folder_path,source_path) VALUES(@id,'QA-007',@fp,@sp)",
        new { id = offlineId, fp = originalFolder, sp = Path.Combine(originalFolder, "original.mp4") });
    offline.Directories = new() { offlineRoot };
    await service.UpdateAsync(offlineId, offline);
    var offlineRecord = await c.QuerySingleAsync<(string Folder, string Source)>("SELECT folder_path Folder,source_path Source FROM movies WHERE number='QA-007'");
    Check(offlineRecord.Folder == offlineFolder && offlineRecord.Source == Path.Combine(offlineFolder, "original.mp4"),
        "An explicit single-root replacement must update nested paths even while the mount is offline");
    Check(Directory.Exists(originalFolder) && !Directory.Exists(offlineRoot), "Path rebinding must not move files or create mount directories");

    Console.WriteLine("PASS: nested migration, video source, covers, stale repair, ambiguous paths, cloud conversion, cache preservation and offline mounts");

    // Exercise the real HTTP lists: converted cloud folders must not be probed
    // for badges, even when they happen to be available during a test run.
    var remoteFolder = Path.Combine(db.DataDir + "-remote", "CLOUD-001");
    var cachedFolder = Path.Combine(db.DataDir, "cache", "CLOUD-003");
    foreach (var folder in new[] { remoteFolder, cachedFolder })
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "original.mp4"), "video");
    }
    File.WriteAllText(Path.Combine(remoteFolder, "CLOUD-001-trailer.mp4"), "trailer");
    File.WriteAllText(Path.Combine(cachedFolder, "CLOUD-003-trailer.mp4"), "trailer");
    var gridLibraryId = await service.CreateAsync(new Library { Name = "cloud-grid", CacheLocal = true });
    await c.ExecuteAsync("INSERT INTO movies(library_id,number,folder_path,source_path) VALUES(@lib,@number,@folder,@source)", new[] {
        new { lib = gridLibraryId, number = "CLOUD-001", folder = remoteFolder, source = (string?)null },
        new { lib = gridLibraryId, number = "CLOUD-002", folder = @"\\192.0.2.1\offline\CLOUD-002", source = (string?)@"\\192.0.2.1\offline\original.mp4" },
        new { lib = gridLibraryId, number = "CLOUD-003", folder = cachedFolder, source = (string?)null }
    });
    var actorId = await c.ExecuteScalarAsync<long>("INSERT INTO actors(name) VALUES('Grid fixture'); SELECT last_insert_rowid();");
    var tagId = await c.ExecuteScalarAsync<long>("INSERT INTO tags(name) VALUES('Grid fixture'); SELECT last_insert_rowid();");
    await c.ExecuteAsync("INSERT INTO movie_actors(movie_id,actor_id) SELECT id,@actorId FROM movies WHERE library_id=@lib", new { actorId, lib = gridLibraryId });
    await c.ExecuteAsync("INSERT INTO movie_tags(movie_id,tag_id) SELECT id,@tagId FROM movies WHERE library_id=@lib", new { tagId, lib = gridLibraryId });
    await c.ExecuteAsync("INSERT INTO favorites(target_type,target_id) SELECT 'movie',id FROM movies WHERE library_id=@lib", new { lib = gridLibraryId });
    var resumedRoot = Path.Combine(root, "resume"); Directory.CreateDirectory(resumedRoot);
    File.WriteAllText(Path.Combine(resumedRoot, "resume.nfo"), "resume");
    var resumedId = await service.CreateAsync(new Library { Name = "resume", CacheLocal = true, Directories = new() { resumedRoot } });
    await c.ExecuteAsync("UPDATE libraries SET cache_job_id='resume-fixture',cache_status=@status WHERE id=@id",
        new { id = resumedId, status = JsonSerializer.Serialize(new LibraryCacheStatus("queued"), new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
    var startInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "javideo-worker.exe"))
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
    startInfo.ArgumentList.Add("--Javideo:DataDir=" + db.DataDir);
    using var worker = Process.Start(startInfo)!;
    try
    {
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string? port = null;
        while (await worker.StandardOutput.ReadLineAsync(startup.Token) is { } line)
            if (line.StartsWith("JAVIDEO_WORKER_PORT=")) { port = line.Split('=')[1]; break; }
        Check(port != null, "HTTP test worker must start");
        var output = worker.StandardOutput.ReadToEndAsync();
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(3) };
        async Task<LibraryCacheStatus> WaitCache(long libraryId, string expected = "completed")
        {
            for (var attempt = 0; attempt < 150; attempt++)
            {
                var current = (await http.GetFromJsonAsync<Library>($"/api/libraries/{libraryId}"))!.CacheStatus;
                if (current?.State is "completed" or "failed")
                { Check(current.State == expected, $"Cache state {current.State}, expected {expected}: {current.LastError}"); return current; }
                await Task.Delay(100);
            }
            throw new Exception("Background cache timed out");
        }
        Check((await WaitCache(resumedId)).DoneFiles == 1, "A queued cache must resume after worker startup");
        foreach (var path in new[] { "/api/movies/library/all", $"/api/movies/library/{gridLibraryId}", $"/api/actors/{actorId}/movies", $"/api/tags/{tagId}/movies", $"/api/actors/{actorId}/detail" })
        {
            using var response = await http.GetFromJsonAsync<JsonDocument>(path);
            var rows = (path.EndsWith("/detail") ? response!.RootElement.GetProperty("movies") : response!.RootElement)
                .Deserialize<List<Movie>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var uncached = rows.Single(m => m.Number == "CLOUD-001");
            Check(!uncached.HasVideo && !uncached.HasTrailer, "Cloud lists must not probe uncached folders: " + path);
            Check(uncached.LibraryId == gridLibraryId && uncached.ThumbUrl == $"/api/movies/{uncached.Id}/image/thumb", "Cards must retain library and image routes: " + path);
            Check(rows.Single(m => m.Number == "CLOUD-002").HasVideo, "Offline recorded source must keep its video badge: " + path);
            Check(rows.Single(m => m.Number == "CLOUD-003") is { HasVideo: true, HasTrailer: true }, "Cached metadata must retain badges: " + path);
            if (path.EndsWith("/all"))
                Check(rows.Single(m => m.Number == "QA-001").HasVideo, "Local libraries must retain video badges");
        }
        using var favorites = await http.GetFromJsonAsync<JsonDocument>("/api/favorites/movie");
        var favoriteRows = favorites!.RootElement.EnumerateArray().ToList();
        var favorite = favoriteRows.Single(r => r.GetProperty("name").GetString() == "CLOUD-001");
        Check(!favorite.GetProperty("hasVideo").GetBoolean() && !favorite.GetProperty("hasTrailer").GetBoolean(), "Favorites must not probe uncached cloud folders");
        Check(favoriteRows.Single(r => r.GetProperty("name").GetString() == "CLOUD-002").GetProperty("hasVideo").GetBoolean(), "Favorites must preserve known offline sources");
        Check(favoriteRows.Single(r => r.GetProperty("name").GetString() == "CLOUD-003").GetProperty("hasTrailer").GetBoolean(), "Favorites must retain cached trailer badges");
        Console.WriteLine("PASS: cloud/offline/local/cached cards through all, library, actor, actor detail, tag and favorite HTTP lists");

        // Hold two streamed responses open; another cloud request must queue,
        // while lists, local media and local preview caches remain available.
        var cachedId = await c.ExecuteScalarAsync<long>("SELECT id FROM movies WHERE number='CLOUD-003'");
        var remoteId = await c.ExecuteScalarAsync<long>("SELECT id FROM movies WHERE number='CLOUD-001'");
        var localId = await c.ExecuteScalarAsync<long>("SELECT id FROM movies WHERE number='QA-001'");
        using (var trailer = File.OpenWrite(Path.Combine(remoteFolder, "CLOUD-001-trailer.mp4"))) trailer.SetLength(64L * 1024 * 1024);
        var previewDir = Path.Combine(db.DataDir, "previews", cachedId.ToString());
        Directory.CreateDirectory(previewDir);
        File.WriteAllBytes(Path.Combine(previewDir, "0.jpg"), new byte[] { 1, 2, 3 });
        using var first = await http.GetAsync($"/api/movies/{remoteId}/trailer", HttpCompletionOption.ResponseHeadersRead);
        using var second = await http.GetAsync($"/api/movies/{remoteId}/trailer", HttpCompletionOption.ResponseHeadersRead);
        first.EnsureSuccessStatusCode(); second.EnsureSuccessStatusCode();
        var postRoot = Path.Combine(root, "auto-create"); Directory.CreateDirectory(postRoot);
        File.WriteAllText(Path.Combine(postRoot, "metadata.txt"), "new library metadata");
        using var createdResponse = await http.PostAsJsonAsync("/api/libraries", new Library { Name = "auto-create", CacheLocal = true, Directories = new() { postRoot } });
        createdResponse.EnsureSuccessStatusCode();
        var created = (await createdResponse.Content.ReadFromJsonAsync<Library>())!;
        Check(created.CacheStatus?.State == "queued", "Enabled library creation must queue caching and return immediately");
        using (var savedWhileBusy = await http.PutAsJsonAsync($"/api/libraries/{created.Id}", created))
            savedWhileBusy.EnsureSuccessStatusCode();
        using var queuedCancel = new CancellationTokenSource();
        var queued = http.GetAsync($"/api/movies/{remoteId}/image/poster", queuedCancel.Token);
        await Task.Delay(150);
        Check(!queued.IsCompleted, "A third cloud response must wait until a stream releases its slot");
        Check((await http.GetFromJsonAsync<Library>($"/api/libraries/{created.Id}"))!.CacheStatus?.State != "completed",
            "Background caching must share the same two cloud slots as streamed responses");
        Check((await http.GetFromJsonAsync<List<Movie>>("/api/movies/library/all"))!.Count > 0, "Lists must not wait behind cloud streams");
        Check((await http.GetByteArrayAsync($"/api/movies/{localId}/image/poster")).Length > 0, "Local libraries must bypass the cloud queue");
        Check((await http.GetByteArrayAsync($"/api/movies/{cachedId}/preview/0")).Length == 3, "Local preview cache must bypass the cloud queue");
        Check(await http.GetStringAsync($"/api/movies/{cachedId}/trailer") == "trailer", "Cached trailers must bypass the cloud queue");
        var remoteDetail = await http.GetFromJsonAsync<Movie>($"/api/movies/{remoteId}");
        Check(remoteDetail is { Number: "CLOUD-001", HasTrailer: false }, "Cloud detail metadata must not probe the mount or wait behind streams");
        var cachedDetail = await http.GetFromJsonAsync<Movie>("/api/movies/by-number/CLOUD-003");
        Check(cachedDetail is { HasTrailer: true, PreviewImages.Count: 1 }, "Detail by number must return local previews while cloud streams are busy");
        queuedCancel.Cancel();
        try { using var unexpected = await queued; throw new Exception("Canceled queued cloud request ran"); }
        catch (OperationCanceledException) { }
        first.Dispose();
        using var resumed = await http.GetAsync($"/api/movies/{remoteId}/image/poster");
        Check(resumed.StatusCode == System.Net.HttpStatusCode.NotFound, "Released stream slot must serve the next cloud request");
        second.Dispose();
        Check((await WaitCache(created.Id)).DoneFiles == 1, "Creating an enabled library must copy metadata after cloud slots become free");
        using var trailerStatus = await http.GetFromJsonAsync<JsonDocument>($"/api/movies/{remoteId}/trailer-status");
        Check(trailerStatus!.RootElement.GetProperty("hasTrailer").GetBoolean(), "Deferred cloud trailer check must retain the existing trailer");
        Console.WriteLine("PASS: shared two-request cloud limit, streamed-response lifetime, canceled queue, local/list/detail bypass, deferred trailer status and slot recovery");

        var sourceRoot = Path.Combine(root, "auto-update");
        var sourceFolder = Path.Combine(sourceRoot, "nested", "CACHE-001");
        Directory.CreateDirectory(Path.Combine(sourceFolder, "extrafanart"));
        var metadata = new Dictionary<string, string> { ["CACHE-001-poster.jpg"] = "poster", ["CACHE-001.nfo"] = "nfo",
            ["CACHE-001-trailer.mp4"] = "trailer", ["subtitles.srt"] = "subtitles", ["extrafanart/1.jpg"] = "screenshot", ["info.json"] = "info" };
        foreach (var file in metadata) File.WriteAllText(Path.Combine(sourceFolder, file.Key), file.Value);
        foreach (var name in new[] { "original.mp4", "part2.mkv", "disc.iso", "movie.strm" }) File.WriteAllText(Path.Combine(sourceFolder, name), "feature");
        var cacheLibrary = new Library { Name = "auto-update", Directories = new() { sourceRoot } };
        using var disabledResponse = await http.PostAsJsonAsync("/api/libraries", cacheLibrary);
        disabledResponse.EnsureSuccessStatusCode();
        cacheLibrary = (await disabledResponse.Content.ReadFromJsonAsync<Library>())!;
        Check(cacheLibrary.CacheStatus == null, "Disabled libraries must not schedule caching");
        var cacheMovie = await c.ExecuteScalarAsync<long>(@"INSERT INTO movies(library_id,number,folder_path,preview_images,title,summary)
            VALUES(@lib,'CACHE-001',@folder,'[""https://invalid.test/0.jpg"",""https://invalid.test/1.jpg""]','Cache fixture','Summary fixture'); SELECT last_insert_rowid();",
            new { lib = cacheLibrary.Id, folder = sourceFolder });
        var storedPreviews = Path.Combine(db.DataDir, "previews", cacheMovie.ToString()); Directory.CreateDirectory(storedPreviews);
        File.WriteAllText(Path.Combine(storedPreviews, "0.jpg"), "preview0"); File.WriteAllText(Path.Combine(storedPreviews, "1.jpg"), "preview1");
        cacheLibrary.CacheLocal = true;
        using (var saved = await http.PutAsJsonAsync($"/api/libraries/{cacheLibrary.Id}", cacheLibrary)) saved.EnsureSuccessStatusCode();
        var copied = await WaitCache(cacheLibrary.Id);
        Check(copied.DoneFiles == metadata.Count && copied.SkippedFiles == 0, "Saving an enabled library must copy every non-feature file");
        var binding = await c.QuerySingleAsync<(string Folder, string SourceFolder, string Source)>(
            "SELECT folder_path Folder,source_folder_path SourceFolder,source_path Source FROM movies WHERE id=@id", new { id = cacheMovie });
        Check(binding.Folder.StartsWith(Path.Combine(db.DataDir, "cache", "libraries")) && binding.SourceFolder == sourceFolder,
            "Metadata must rebind to the local copy while recording its original folder");
        Check(File.ReadAllText(binding.Source) == "feature" && binding.Source.StartsWith(sourceFolder), "Primary video source must remain on the original drive");
        foreach (var file in metadata)
            Check(File.ReadAllText(Path.Combine(binding.Folder, file.Key)) == file.Value && File.ReadAllText(Path.Combine(sourceFolder, file.Key)) == file.Value,
                "Cached metadata and originals must be intact: " + file.Key);
        Check(Directory.GetFiles(binding.Folder, "*", SearchOption.AllDirectories).Length == metadata.Count,
            "Primary MP4/MKV/ISO/STRM files must never be copied");
        var cachedMovie = (await http.GetFromJsonAsync<Movie>($"/api/movies/{cacheMovie}"))!;
        Check(cachedMovie is { HasTrailer: true, HasVideo: true, PreviewImages.Count: 2, Summary: "Summary fixture" }, "Cached detail must retain metadata, video, trailer and existing previews");
        Check(await http.GetStringAsync($"/api/movies/{cacheMovie}/image/poster") == "poster", "Image endpoint must serve the new local binding");
        var firstUrl = cachedMovie.CoverUrl;
        using (var saved = await http.PutAsJsonAsync($"/api/libraries/{cacheLibrary.Id}", cacheLibrary)) saved.EnsureSuccessStatusCode();
        Check((await WaitCache(cacheLibrary.Id)).SkippedFiles == metadata.Count, "Repeated saves must skip unchanged files");
        File.WriteAllText(Path.Combine(sourceFolder, "CACHE-001-poster.jpg"), "updated-poster");
        using (var retry = await http.PostAsync($"/api/libraries/{cacheLibrary.Id}/cache", null)) retry.EnsureSuccessStatusCode();
        Check((await WaitCache(cacheLibrary.Id)).SkippedFiles == metadata.Count - 1, "Retry must update only changed metadata");
        Check(await http.GetStringAsync($"/api/movies/{cacheMovie}/image/poster") == "updated-poster", "Changed posters must refresh");
        Check((await http.GetFromJsonAsync<Movie>($"/api/movies/{cacheMovie}"))!.CoverUrl != firstUrl, "Cache updates must invalidate browser image URLs");

        var movedRoot = Path.Combine(root, "auto-moved");
        cacheLibrary.Directories = new() { movedRoot };
        using (var saved = await http.PutAsJsonAsync($"/api/libraries/{cacheLibrary.Id}", cacheLibrary)) saved.EnsureSuccessStatusCode();
        Check((await WaitCache(cacheLibrary.Id, "failed")).LastError != null, "Offline roots must report a retryable failure");
        Check(await c.ExecuteScalarAsync<string>("SELECT folder_path FROM movies WHERE id=@id", new { id = cacheMovie }) == binding.Folder,
            "An offline root must preserve the last complete cache");
        var movedFolder = Path.Combine(movedRoot, "nested", "CACHE-001");
        Directory.CreateDirectory(Path.Combine(movedFolder, "extrafanart"));
        foreach (var file in Directory.GetFiles(sourceFolder, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(movedFolder, Path.GetRelativePath(sourceFolder, file)));
        using (var retry = await http.PostAsync($"/api/libraries/{cacheLibrary.Id}/cache", null)) retry.EnsureSuccessStatusCode();
        await WaitCache(cacheLibrary.Id);
        var moved = await c.QuerySingleAsync<(string Folder, string SourceFolder, string Source)>(
            "SELECT folder_path Folder,source_folder_path SourceFolder,source_path Source FROM movies WHERE id=@id", new { id = cacheMovie });
        Check(moved.Folder != binding.Folder && moved.SourceFolder == movedFolder && moved.Source.StartsWith(movedFolder),
            "Moving a cached library must relocate original paths and rebuild its local binding");
        Check(File.Exists(Path.Combine(binding.Folder, "CACHE-001-poster.jpg")) && Directory.Exists(sourceFolder), "Retries must preserve previous caches and source files");
        cacheLibrary.CacheLocal = false;
        using (var saved = await http.PutAsJsonAsync($"/api/libraries/{cacheLibrary.Id}", cacheLibrary)) saved.EnsureSuccessStatusCode();
        Check((await http.GetFromJsonAsync<Library>($"/api/libraries/{cacheLibrary.Id}"))!.CacheStatus == null, "Disabling caching must clear the queued job");
        using (var retry = await http.PostAsync($"/api/libraries/{cacheLibrary.Id}/cache", null))
            Check(retry.StatusCode == System.Net.HttpStatusCode.BadRequest, "Disabled libraries must reject automatic cache retries");
        Check(!Directory.GetFiles(Path.Combine(db.DataDir, "cache"), "*.tmp", SearchOption.AllDirectories).Any(), "Atomic cache copies must leave no temporary files");
        var flatRoot = Path.Combine(root, "flat-cloud"); Directory.CreateDirectory(flatRoot);
        File.WriteAllText(Path.Combine(flatRoot, "FLAT-001.mp4"), "feature");
        var oldMetadata = Path.Combine(db.DataDir, "cache", "FLAT-001"); Directory.CreateDirectory(oldMetadata);
        File.WriteAllText(Path.Combine(oldMetadata, "FLAT-001-poster.jpg"), "existing-local-poster");
        File.WriteAllText(Path.Combine(oldMetadata, "FLAT-001.nfo"), "existing-local-nfo");
        var flatLibrary = new Library { Name = "flat-cloud", CacheLocal = true, Directories = new() { flatRoot } };
        flatLibrary.Id = await service.CreateAsync(flatLibrary);
        var flatId = await c.ExecuteScalarAsync<long>("INSERT INTO movies(library_id,number,folder_path,source_path) VALUES(@lib,'FLAT-001',@folder,@source); SELECT last_insert_rowid();",
            new { lib = flatLibrary.Id, folder = oldMetadata, source = Path.Combine(flatRoot, "FLAT-001.mp4") });
        using (var saved = await http.PutAsJsonAsync($"/api/libraries/{flatLibrary.Id}", flatLibrary)) saved.EnsureSuccessStatusCode();
        await WaitCache(flatLibrary.Id);
        Check(await http.GetStringAsync($"/api/movies/{flatId}/image/poster") == "existing-local-poster",
            "A cloud folder containing only videos must preserve previously scraped local metadata");
        Check(File.Exists(Path.Combine(oldMetadata, "FLAT-001.nfo")), "Carrying local metadata forward must preserve its original cache");
        Console.WriteLine("PASS: save/create auto-cache, disabled opt-out, restart resume, non-feature copies, originals intact, local detail, incremental retries, offline recovery and cached-root relocation");
    }
    finally
    {
        if (!worker.HasExited) worker.Kill(entireProcessTree: true);
        await worker.WaitForExitAsync();
    }
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Check(root.StartsWith(Path.GetFullPath(AppContext.BaseDirectory) + "fixtures-", StringComparison.OrdinalIgnoreCase), "Fixture cleanup boundary");
    Directory.Delete(root, recursive: true);
}
