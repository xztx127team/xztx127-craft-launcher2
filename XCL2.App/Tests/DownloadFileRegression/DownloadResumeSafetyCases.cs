using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class DownloadResumeSafetyCases
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static Task Download(DownloadService service, string path, string hash, long? size = null) =>
        (Task)typeof(DownloadService).GetMethod("DownloadFileWithSizeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, new object?[] { "https://resume-fixture.example.org/file.bin", path, hash, CancellationToken.None, size })!;

    private static HttpResponseMessage Bytes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    public static async Task RunAsync(string root)
    {
        var payload = new byte[1024]; new Random(9059).NextBytes(payload);
        var hash = Convert.ToHexString(SHA1.HashData(payload));

        // Missing hashes are common in loader libraries. Nonempty HTML and truncated
        // archive caches must still be rejected rather than becoming a persistent bad JAR.
        byte[] library;
        using (var memory = new MemoryStream())
        {
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = archive.CreateEntry("fixture.class").Open()) entry.Write(payload);
            library = memory.ToArray();
        }
        var libraryPath = Path.Combine(root, "no-hash-library.jar");
        await File.WriteAllTextAsync(libraryPath, "<html>temporary CDN error</html>");
        using (var http = new HttpClient(new Handler((request, _) => Task.FromResult(Bytes(library)))))
        using (var service = new DownloadService(DownloadSource.Official, http))
        {
            await Download(service, libraryPath, "").WaitAsync(TimeSpan.FromSeconds(5));
            Require(File.ReadAllBytes(libraryPath).SequenceEqual(library), "A nonempty invalid JAR cache was treated as a complete library.");
        }
        using (var http = new HttpClient(new Handler((request, _) => Task.FromResult(Bytes(payload)))))
        using (var service = new DownloadService(DownloadSource.Official, http))
        {
            var badPath = Path.Combine(root, "reject-no-hash.jar");
            var rejected = false;
            try { await Download(service, badPath, "").WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (IOException) { rejected = true; }
            Require(rejected && !File.Exists(badPath), "Invalid downloaded JAR data was atomically published despite having no usable archive.");
        }

        // A nonempty but short cached file without a digest must not override a known manifest size.
        var cachedPath = Path.Combine(root, "cached-size.bin");
        await File.WriteAllBytesAsync(cachedPath, payload[..10]);
        var cachedGets = 0;
        using (var http = new HttpClient(new Handler((_, _) =>
        { cachedGets++; return Task.FromResult(Bytes(payload)); })))
        using (var service = new DownloadService(DownloadSource.Official, http))
        {
            await Download(service, cachedPath, "", payload.Length).WaitAsync(TimeSpan.FromSeconds(5));
            Require(cachedGets == 1 && File.ReadAllBytes(cachedPath).SequenceEqual(payload),
                "A truncated cached file bypassed the manifest size check.");
        }

        // Even a same-size partial from an older URL must not become the new resource without a digest.
        foreach (var staleSize in new[] { payload.Length / 2, payload.Length })
        {
            var path = Path.Combine(root, "unverified-resume-" + staleSize + ".bin");
            await File.WriteAllBytesAsync(path + ".part", new byte[staleSize]);
            var gets = 0; var ranges = 0;
            using var http = new HttpClient(new Handler((request, _) =>
            {
                var response = Bytes(payload);
                response.Headers.AcceptRanges.Add("bytes");
                if (request.Method != HttpMethod.Head)
                {
                    gets++;
                    if (request.Headers.Range != null) ranges++;
                }
                return Task.FromResult(response);
            }));
            using var service = new DownloadService(DownloadSource.Official, http);
            await Download(service, path, "").WaitAsync(TimeSpan.FromSeconds(5));
            Require(gets == 1 && ranges == 0 && File.ReadAllBytes(path).SequenceEqual(payload),
                "An unverifiable partial was resumed or committed without downloading current content.");
        }

        // A real first stream drops after its prefix; the second round must request only the suffix.
        var retryPath = Path.Combine(root, "retry-resume.bin");
        var retryGets = 0; long? resumedFrom = null;
        using (var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = Bytes(Array.Empty<byte>()); head.Content.Headers.ContentLength = payload.Length;
                head.Headers.AcceptRanges.Add("bytes"); return Task.FromResult(head);
            }
            retryGets++;
            if (retryGets == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new BrokenStream(payload[..128])) };
                response.Content.Headers.ContentLength = payload.Length;
                return Task.FromResult(response);
            }
            resumedFrom = request.Headers.Range?.Ranges.Single().From;
            if (resumedFrom != 128) return Task.FromResult(Bytes(payload));
            var partialResponse = new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(payload[128..]) };
            partialResponse.Content.Headers.ContentRange = new ContentRangeHeaderValue(128, payload.Length - 1, payload.Length);
            return Task.FromResult(partialResponse);
        })))
        using (var service = new DownloadService(DownloadSource.Official, http))
        {
            await Download(service, retryPath, hash, payload.Length).WaitAsync(TimeSpan.FromSeconds(10));
            Require(retryGets == 2 && resumedFrom == 128 && File.ReadAllBytes(retryPath).SequenceEqual(payload),
                "The bounded retry discarded a verifiable partial instead of resuming it.");
        }
    }

    private sealed class BrokenStream(byte[] prefix) : MemoryStream(prefix)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position == Length) throw new IOException("fixture connection reset after a real partial write");
            return base.ReadAsync(buffer, ct);
        }
    }
}
