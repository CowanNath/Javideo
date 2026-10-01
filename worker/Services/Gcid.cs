using System.Security.Cryptography;

namespace Javideo.Worker.Services;

/// <summary>
/// Thunder (迅雷) GCID content hash — a reverse-engineered, community-documented
/// algorithm: split the file into 256KB blocks, SHA1 each block, feed the raw
/// digests into one outer SHA1, uppercase hex output. Identical content yields
/// an identical GCID regardless of filename or location, which is what Thunder
/// uses to match subtitles to a specific piece of content.
/// </summary>
public static class Gcid
{
    private const int ChunkSize = 256 * 1024;

    public static async Task<string> ComputeAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var outer = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[ChunkSize];
        int read;
        while ((read = await fs.ReadAsync(buffer.AsMemory(0, ChunkSize), ct)) > 0)
        {
            var chunkDigest = SHA1.HashData(buffer.AsSpan(0, read));
            outer.AppendData(chunkDigest);
        }
        return Convert.ToHexString(outer.GetHashAndReset());
    }
}
