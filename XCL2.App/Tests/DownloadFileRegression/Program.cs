using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.IO.Compression;
using System.Security.Cryptography;
using XCL2.App.Models;
using XCL2.App.Services;

// The child mode exercises actual OS ownership rather than a second in-process semaphore.
if (args is ["--hold-lock", var target, var ready, var release])
{
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    using var ownership = await DownloadTargetLock.AcquireAsync(target, lifetime.Token);
    await File.WriteAllTextAsync(ready, "ready", lifetime.Token);
    while (!File.Exists(release)) await Task.Delay(20, lifetime.Token);
    return;
}

static void Require(bool value, string message) { if (!value) throw new Exception(message); }
static async Task Until(Func<bool> predicate, string message)
{
    var deadline = Stopwatch.StartNew();
    while (!predicate())
    {
        if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(message);
        await Task.Delay(10);
    }
}
static Task Download(DownloadService service, string path, string sha1, CancellationToken ct = default) =>
    (Task)typeof(DownloadService).GetMethod("DownloadFileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(service, new object[] { "https://downloads.example.org/file.bin", path, sha1, ct, true })!;
static async Task ExpectCanceled(Task operation)
{
    try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (OperationCanceledException) { return; }
    throw new Exception("Waiting cancellation was accepted as successful download.");
}
static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

var root = Path.Combine(Path.GetTempPath(), "xcl-download-file-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    // Exercise the real public repair APIs with an in-memory transport; no external network is used.
    var clientBytes = System.Text.Encoding.UTF8.GetBytes("verified client fixture");
    var clientSha1 = Convert.ToHexString(SHA1.HashData(clientBytes));
    using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(Bytes(clientBytes)))))
    using (var service = new DownloadService(DownloadSource.Official, http))
    {
        var client = new VersionDetail { Id = "original-client-id", Downloads = new()
        { ["client"] = new DownloadArtifact { Url = "https://downloads.example.org/client.jar", Sha1 = clientSha1 } } };
        var destination = Path.Combine(root, "versions", "renamed-client", "original-client-id.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); await File.WriteAllBytesAsync(destination, Array.Empty<byte>());
        await service.DownloadClientJarOnlyAsync(root, client, null, ct: default, targetVersionId: "renamed-client", targetJarBaseName: "original-client-id").WaitAsync(TimeSpan.FromSeconds(10));
        Require(File.ReadAllBytes(destination).SequenceEqual(clientBytes) && !Directory.Exists(Path.Combine(root, "versions", "original-client-id")),
            "Client repair confused the renamed folder and original metadata ID.");
        Require(client.Id == "original-client-id", "Repair mutated parsed client metadata.");
    }
    var pe32 = new byte[256];
    using (var writer = new BinaryWriter(new MemoryStream(pe32)))
    { writer.Write((ushort)0x5a4d); writer.BaseStream.Position = 0x3c; writer.Write(128u); writer.BaseStream.Position = 128; writer.Write(0x00004550u); writer.Write((ushort)0x014c); }
    byte[] nativeZip;
    using (var memory = new MemoryStream())
    {
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        { using var entry = zip.CreateEntry("fixture.dll").Open(); entry.Write(pe32); }
        nativeZip = memory.ToArray();
    }
    var wrongArchRequests = 0;
    using (var http = new HttpClient(new Handler((request, _) =>
    {
        if (request.RequestUri!.AbsolutePath.Contains("native64")) Interlocked.Increment(ref wrongArchRequests);
        return Task.FromResult(Bytes(nativeZip));
    })))
    using (var service = new DownloadService(DownloadSource.Official, http))
    {
        var nativeSha1 = Convert.ToHexString(SHA1.HashData(nativeZip));
        var metadata = new VersionDetail { Id = "native-target", Libraries = new() { new LibraryEntry
        {
            Name = "fixture:natives:1", Natives = new() { ["windows"] = "natives-windows-${arch}" },
            Downloads = new LibraryDownloads { Classifiers = new()
            {
                ["natives-windows-32"] = new DownloadArtifact { Url = "https://downloads.example.org/native32.jar", Sha1 = nativeSha1 },
                ["natives-windows-64"] = new DownloadArtifact { Url = "https://downloads.example.org/native64.jar", Sha1 = nativeSha1 }
            } }
        } } };
        await service.DownloadLibrariesOnlyAsync(root, metadata, null, ct: default, architecture: "x86").WaitAsync(TimeSpan.FromSeconds(10));
        var natives = NativeDirectoryPolicy.Resolve(Path.Combine(root, "versions", "native-target"), "x86");
        Require(wrongArchRequests == 0 && NativeDirectoryPolicy.HasCompatibleDll(natives, "x86"),
            "Actual native repair selected the OS architecture instead of the requested Java architecture.");
    }
    var payload = new byte[9 * 1024 * 1024]; new Random(401).NextBytes(payload);
    var hash = Convert.ToHexString(SHA1.HashData(payload));
    foreach (var invalidRangeHeader in new[] { false, true })
    {
        var heads = 0; var ranges = 0; var singles = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
            {
                Interlocked.Increment(ref heads);
                var head = Bytes(Array.Empty<byte>()); head.Content.Headers.ContentLength = payload.Length;
                head.Headers.AcceptRanges.Add("bytes"); return Task.FromResult(head);
            }
            if (request.Headers.Range != null)
            {
                Interlocked.Increment(ref ranges);
                if (!invalidRangeHeader) return Task.FromResult(Bytes(payload)); // HEAD promises bytes, GET ignores Range.
                var range = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[] { 1 }) };
                range.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, payload.Length);
                return Task.FromResult(range);
            }
            Interlocked.Increment(ref singles); return Task.FromResult(Bytes(payload));
        }));
        using var service = new DownloadService(DownloadSource.Official, http);
        var destination = Path.Combine(root, "fallback-" + invalidRangeHeader + ".bin");
        await Download(service, destination, hash).WaitAsync(TimeSpan.FromSeconds(10));
        Require(heads == 1 && ranges >= 1 && singles == 1, "Unsupported/malformed Range did not fall back within the same attempt.");
        Require(File.ReadAllBytes(destination).SequenceEqual(payload), "Range fallback produced corrupt output.");
        Require(!Directory.EnumerateFiles(root, "*.chunks-*").Any(), "Chunk temp files survived completion.");
    }

    using (var http = new HttpClient(new Handler((request, _) =>
    {
        if (request.Method == HttpMethod.Head)
        {
            var head = Bytes(Array.Empty<byte>()); head.Content.Headers.ContentLength = payload.Length;
            head.Headers.AcceptRanges.Add("bytes"); return Task.FromResult(head);
        }
        var requested = request.Headers.Range!.Ranges.Single();
        var from = requested.From!.Value; var to = requested.To!.Value;
        var truncated = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[1]) };
        truncated.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, payload.Length);
        return Task.FromResult(truncated);
    })))
    using (var service = new DownloadService(DownloadSource.Official, http))
    {
        var destination = Path.Combine(root, "truncated-no-hash.bin");
        var rejected = false;
        try { await Download(service, destination, "").WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (IOException) { rejected = true; }
        Require(rejected && !File.Exists(destination), "Sparse/truncated chunks passed as complete when no hash was supplied.");
    }

    // A truthful range header still cannot make a short chunked body complete.
    var resumePath = Path.Combine(root, "short-resume.bin");
    await File.WriteAllBytesAsync(resumePath + ".part", payload[..16]);
    using (var http = new HttpClient(new Handler((request, _) =>
    {
        if (request.Method == HttpMethod.Head)
        {
            var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(Array.Empty<byte>()) };
            head.Headers.AcceptRanges.Add("bytes"); return Task.FromResult(head);
        }
        if (request.Headers.Range != null)
        {
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new UnknownLengthContent(payload[16..20]) };
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(16, payload.Length - 1, payload.Length);
            return Task.FromResult(partial);
        }
        return Task.FromResult(Bytes(payload));
    })))
    using (var service = new DownloadService(DownloadSource.Official, http))
    {
        await Download(service, resumePath, "").WaitAsync(TimeSpan.FromSeconds(10));
        Require(File.ReadAllBytes(resumePath).SequenceEqual(payload), "Short resumed body without Content-Length was committed without a hash.");
    }

    // The same normalized destination is serialized across downloader instances and the fallback adapter.
    var path = Path.Combine(root, "shared.bin");
    await File.WriteAllTextAsync(path + ".part", "resume-owned-by-first");
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var requests = 0;
    using (var http = new HttpClient(new Handler(async (request, ct) =>
    {
        if (request.Method == HttpMethod.Head) { var head = Bytes(Array.Empty<byte>()); head.Content.Headers.ContentLength = payload.Length; return head; }
        Interlocked.Increment(ref requests); started.TrySetResult(); await releaseDownload.Task.WaitAsync(ct); return Bytes(payload);
    })))
    using (var first = new DownloadService(DownloadSource.Official, http))
    using (var second = new DownloadService(DownloadSource.Official, http))
    {
        var firstDownload = Download(first, path, hash);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var canceledWaiter = Download(second, Path.Combine(root, ".", "shared.bin"), hash, cancel.Token);
        cancel.Cancel(); await ExpectCanceled(canceledWaiter);
        Require(requests == 1, "Canceled waiter reached the network while another task owned its target.");
        Require(File.ReadAllText(path + ".part") == "resume-owned-by-first", "Canceled lock waiter deleted another download's partial file.");
        var fallbackRequests = 0;
        using var fallback = new HttpClient(new Handler((_, _) => { Interlocked.Increment(ref fallbackRequests); return Task.FromResult(Bytes(payload)); }));
        var adapterDownload = DownloadEndpoints.DownloadFileWithFallbackAsync(fallback, "https://downloads.example.org/file.bin", false, path, "fixture", expectedSha1: hash);
        await Task.Delay(100);
        Require(fallbackRequests == 0 && !adapterDownload.IsCompleted, "Fallback adapter bypassed the shared target lock.");
        releaseDownload.TrySetResult();
        await Task.WhenAll(firstDownload, adapterDownload).WaitAsync(TimeSpan.FromSeconds(10));
        Require(File.ReadAllBytes(path).SequenceEqual(payload), "Canceling a lock waiter damaged the active download.");
        await Download(second, path, hash).WaitAsync(TimeSpan.FromSeconds(5));
        Require(requests == 1, "Waiting download did not reuse the committed and verified target.");
    }

    // Manual cleanup must respect ownership from another task, just like a downloader.
    var cleanupTarget = Path.Combine(root, "cleanup-shared.bin");
    var cleanupPart = cleanupTarget + ".part";
    await File.WriteAllTextAsync(cleanupPart, "active resume bytes");
    var observation = new DownloadTransferObservation(); observation.Temporary(cleanupPart, resumable: true);
    using (var ownership = await DownloadTargetLock.AcquireAsync(cleanupTarget, CancellationToken.None))
    {
        Require(await Task.Run(observation.DeleteResumableFiles).WaitAsync(TimeSpan.FromSeconds(5)) == 0,
            "Manual cleanup stole another task's target ownership.");
        Require(File.ReadAllText(cleanupPart) == "active resume bytes", "Busy partial was deleted during cleanup.");
    }
    Require(observation.DeleteResumableFiles() == 1 && !File.Exists(cleanupPart), "Released resumable file could not be explicitly cleaned.");

    // Real child process ownership blocks the parent until release; canceling a waiter does not steal it.
    var childReady = Path.Combine(root, "child-ready"); var childRelease = Path.Combine(root, "child-release");
    var childPath = Path.Combine(root, "cross-process.bin");
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var value in new[] { "--hold-lock", childPath, childReady, childRelease }) start.ArgumentList.Add(value);
    using (var child = Process.Start(start)!)
    {
        try
        {
            await Until(() => File.Exists(childReady) || child.HasExited, "Child did not acquire its file lock.");
            Require(!child.HasExited, "Child failed to acquire its file lock.");
            using var cancel = new CancellationTokenSource(200);
            await ExpectCanceled(DownloadTargetLock.AcquireAsync(childPath, cancel.Token));
            await File.WriteAllTextAsync(childRelease, "release");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Require(child.ExitCode == 0, "Cross-process lock holder failed.");
            using var acquired = await DownloadTargetLock.AcquireAsync(childPath, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    // Periodic sampling observes a Bedrock start and exit without collection changes, and stops when idle.
    var probes = 0; var bedrock = 0; var observed = 0;
    using (var monitor = new DownloadGameActivityMonitor(() => { Interlocked.Increment(ref probes); return Volatile.Read(ref bedrock) != 0; },
        running => Volatile.Write(ref observed, running ? 1 : 0), TimeSpan.FromMilliseconds(20)))
    {
        monitor.Update(true, false, false); await Task.Delay(80); Require(probes == 0, "Idle launcher scanned processes.");
        monitor.Update(true, true, false); await Until(() => Volatile.Read(ref probes) > 0, "Active download did not start its game probe.");
        Volatile.Write(ref bedrock, 1); await Until(() => Volatile.Read(ref observed) == 1, "Bedrock start did not lower the download limit.");
        Volatile.Write(ref bedrock, 0); await Until(() => Volatile.Read(ref observed) == 0, "Bedrock exit left the gaming limit enabled.");
        monitor.Update(true, true, true); Require(observed == 1, "Java state was not reflected immediately.");
        monitor.Update(true, false, false); await Task.Delay(50); var last = probes; await Task.Delay(100);
        Require(observed == 0 && probes == last, "Idle monitor kept probing or retained stale game state.");
    }
    // A blocked native process snapshot cannot accumulate workers across download toggles.
    using (var releaseProbe = new ManualResetEventSlim(false))
    {
        var blockedProbes = 0; var finishedProbes = 0;
        using (var monitor = new DownloadGameActivityMonitor(() =>
        {
            Interlocked.Increment(ref blockedProbes); releaseProbe.Wait();
            Interlocked.Increment(ref finishedProbes); return false;
        }, _ => { }, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(20)))
        {
            monitor.Update(true, true, false);
            await Until(() => Volatile.Read(ref blockedProbes) == 1, "Slow probe did not begin.");
            for (var i = 0; i < 4; i++)
            {
                monitor.Update(true, false, false); monitor.Update(true, true, false); await Task.Delay(25);
            }
            Require(blockedProbes == 1, "Timed-out process snapshots accumulated blocked workers.");
        }
        releaseProbe.Set();
        await Until(() => Volatile.Read(ref finishedProbes) == 1, "Canceled monitor retained its completed process probe.");
    }
    Console.WriteLine("DownloadFileRegression passed: actual transfer fallback, target ownership, cancellation, child process, periodic game state.");
}
finally { Directory.Delete(root, recursive: true); }

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }

sealed class UnknownLengthContent(byte[] bytes) : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
}
