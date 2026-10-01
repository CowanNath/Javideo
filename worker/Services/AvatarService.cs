using System.Security.Cryptography;
using System.Text;
using Javideo.Worker.Db;

namespace Javideo.Worker.Services;

/// <summary>
/// Caches actor avatars to disk so they don't depend on the MetaTube server
/// (or the upstream Gfriends/dmm image) being reachable at display time.
///
/// Files live under %AppData%/Javideo/actors/{actorId}.jpg. We store the local
/// filename in the DB's avatar_url column and serve it via a static endpoint.
/// </summary>
public sealed class AvatarService
{
    private readonly DbConnectionFactory _db;
    private readonly HttpClient _http;

    public AvatarService(DbConnectionFactory db, HttpClient http)
    {
        _db = db;
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>If a local avatar already exists for the actor, return its
    /// filename; otherwise download from the remote URL and cache it.</summary>
    public async Task<string?> EnsureLocalAsync(long actorId, string? remoteUrl)
    {
        if (remoteUrl == null) return null;

        // Already a local cached file? Keep it.
        var local = LocalPathFor(actorId);
        if (File.Exists(local)) return FilenameFor(actorId);

        try
        {
            if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)) return null;
            using var resp = await _http.GetAsync(uri);
            if (!resp.IsSuccessStatusCode) return null;
            // Download to a temp name, then move atomically — two concurrent
            // first-requests for the same actor would otherwise interleave
            // writes into one truncated/corrupt jpg.
            var tmp = local + ".tmp";
            try
            {
                await using (var fs = File.Create(tmp))
                    await resp.Content.CopyToAsync(fs);
                File.Move(tmp, local, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
            return FilenameFor(actorId);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to cache avatar for actor {Id}", actorId);
            return null;
        }
    }

    /// <summary>The filename we serve at /api/actors/{id}/avatar.</summary>
    public static string FilenameFor(long actorId) => $"{actorId}.jpg";

    public string LocalPathFor(long actorId) => Path.Combine(_db.AvatarsDir, FilenameFor(actorId));
}
