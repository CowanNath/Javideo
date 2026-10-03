using Javideo.Worker.Services;

// Keep regression fixtures separate from the app's real preview cache.
var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
Environment.SetEnvironmentVariable("TMP", fixtureRoot);
Environment.SetEnvironmentVariable("TEMP", fixtureRoot);
var client = new TrailerClient(null!); // Cached/invalid-number cases never read settings or use the network.
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
try
{
    var a = TrailerClient.TempPathFor("QA-001");
    var b = TrailerClient.TempPathFor("QA-002");
    Directory.CreateDirectory(Path.GetDirectoryName(a)!);
    await File.WriteAllBytesAsync(a, new byte[] { 1, 2, 3 });
    await File.WriteAllBytesAsync(b, new byte[] { 4, 5, 6 });
    using (var playback = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(await client.GetPreviewAsync("QA-002") == b, "Next search must reuse cache while prior preview is open");
        Check(await client.GetPreviewAsync("qa-001") == a, "Repeat search must reuse an open preview, including lowercase numbers");
        Check(File.Exists(a) && File.Exists(b), "Consecutive searches must retain both trailers");
        var requests = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.GetPreviewAsync("QA-001")));
        Check(requests.All(p => p == a), "Concurrent repeat searches must return the complete cached file");
    }
    var old = TrailerClient.TempPathFor("QA-003");
    await File.WriteAllBytesAsync(old, new byte[] { 7 });
    File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
    Check(await client.GetPreviewAsync("invalid") == null, "Invalid number must return no preview");
    Check(!File.Exists(old) && File.Exists(a) && File.Exists(b), "Cleanup must expire only old trailers");
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    try { await client.GetPreviewAsync("QA-001", cancel.Token); throw new Exception("Canceled preview was accepted"); }
    catch (OperationCanceledException) { }
    Check(await client.GetPreviewAsync("QA-001") == a, "Canceled request must leave preview gate usable");
    Check(!Directory.GetFiles(Path.GetDirectoryName(a)!, "*.part").Any(), "No partial file should be exposed as a trailer");
    Console.WriteLine("PASS: consecutive/case-insensitive searches, locked playback, concurrent cache access, expiry and cancellation recovery");
}
finally { Directory.Delete(fixtureRoot, recursive: true); }
