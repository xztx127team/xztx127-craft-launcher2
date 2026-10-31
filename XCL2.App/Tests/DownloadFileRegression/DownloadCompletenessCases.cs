using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class DownloadCompletenessCases
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    public static async Task RunAsync(string root)
    {
        var asset = Encoding.UTF8.GetBytes("complete asset");
        var hash = Convert.ToHexString(SHA1.HashData(asset)).ToLowerInvariant();
        var index = Encoding.UTF8.GetBytes("{\"objects\":{\"fixture\":{\"hash\":\"" + hash + "\",\"size\":" + asset.Length + "}}}");
        var detail = new VersionDetail { Id = "asset-completeness", AssetIndex = new AssetIndexRef
        {
            Id = "asset-completeness", Url = "https://index.example.org/assets.json",
            Sha1 = Convert.ToHexString(SHA1.HashData(index)), Size = index.Length
        } };
        var target = Path.Combine(root, "assets", "objects", hash[..2], hash);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, new byte[asset.Length]);
        var assetGets = 0;
        using (var http = new HttpClient(new FixtureHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "index.example.org") return Task.FromResult(Bytes(index));
            if (request.Method == HttpMethod.Get) Interlocked.Increment(ref assetGets);
            return Task.FromResult(Bytes(asset));
        })))
        using (var service = new DownloadService(DownloadSource.Official, http))
        {
            await service.DownloadAssetsOnlyAsync(root, detail, null);
            Require(File.ReadAllBytes(target).SequenceEqual(asset) && assetGets == 1,
                "Same-size corrupted assets were accepted as complete.");
            await service.DownloadAssetsOnlyAsync(root, detail, null);
            Require(assetGets == 1, "Already-verified assets were downloaded again.");
        }

        detail.AssetIndex.Id = "bad-index-completeness";
        var objectRequests = 0;
        using (var http = new HttpClient(new FixtureHandler((request, _) =>
        {
            if (request.RequestUri!.Host != "index.example.org") Interlocked.Increment(ref objectRequests);
            return Task.FromResult(Bytes(Encoding.UTF8.GetBytes("{}")));
        })))
        using (var service = new DownloadService(DownloadSource.Official, http))
        {
            try { await service.DownloadAssetsOnlyAsync(root, detail, null); throw new Exception("An unverified truncated asset index was accepted."); }
            catch (IOException) { }
            Require(objectRequests == 0, "Assets were requested from an index with the wrong publisher digest.");
        }

        byte[] jar;
        using (var memory = new MemoryStream())
        {
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            using (var output = zip.CreateEntry("fixture.class", CompressionLevel.NoCompression).Open())
                output.Write(Encoding.UTF8.GetBytes("fixture bytecode"));
            jar = memory.ToArray();
        }
        var malformed = jar.ToArray();
        // Keep a readable central directory but claim more entry bytes than actually exist.
        var central = Enumerable.Range(0, malformed.Length - 4).Single(offset =>
            malformed[offset] == 0x50 && malformed[offset + 1] == 0x4b && malformed[offset + 2] == 0x01 && malformed[offset + 3] == 0x02);
        BitConverter.GetBytes(100).CopyTo(malformed, central + 24);
        var libraryPath = Path.Combine(root, "libraries", "fixture", "loader", "1", "loader-1.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
        await File.WriteAllBytesAsync(libraryPath, malformed);
        var libraryGets = 0;
        using (var http = new HttpClient(new FixtureHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) Interlocked.Increment(ref libraryGets);
            return Task.FromResult(Bytes(jar));
        })))
        using (var service = new DownloadService(DownloadSource.Official, http))
        {
            await service.DownloadLibrariesOnlyAsync(root, new VersionDetail { Id = "legacy-library-completeness", Libraries = new()
            { new LibraryEntry { Name = "fixture:loader:1" } } }, null);
            Require(libraryGets == 1 && File.ReadAllBytes(libraryPath).SequenceEqual(jar),
                "A name-only Maven dependency or an incomplete hashless cached JAR was skipped.");
        }
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
