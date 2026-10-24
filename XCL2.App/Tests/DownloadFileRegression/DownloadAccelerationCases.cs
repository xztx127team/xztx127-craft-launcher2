using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class DownloadAccelerationCases
{
    private static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static async Task RunAsync(string root)
    {
        const long mb = 1024 * 1024;
        Require(AdaptiveDownloadConcurrency.Decide(256 * mb, 1, true, 0, 1) == 1, "Explicit single-thread preference was ignored.");
        Require(AdaptiveDownloadConcurrency.Decide(256 * mb, 8, false, 0, 1) == 1, "A server without ranges was parallelized.");
        Require(AdaptiveDownloadConcurrency.Decide(4 * mb, 8, true, 0, 1) == 1, "Small files paid range setup costs.");
        Require(AdaptiveDownloadConcurrency.Decide(9 * mb, 8, true, 0, 1) == 2, "Medium files did not choose two ranges.");
        Require(AdaptiveDownloadConcurrency.Decide(256 * mb, 8, true, 0, 1) == 8, "Large unrestricted file did not use available capacity.");
        Require(AdaptiveDownloadConcurrency.Decide(256 * mb, 8, true, mb, 1) == 1, "Low shared rate cap opened unnecessary connections.");
        Require(AdaptiveDownloadConcurrency.Decide(256 * mb, 8, true, 0, 4) == 2, "Concurrent large downloads did not share the range budget.");
        Require(AdaptiveDownloadConcurrency.Decide(256 * mb, 8, true, 0, 1, recentFailures: 1) == 4, "Recent host failures did not reduce concurrency.");
        Require(AdaptiveDownloadConcurrency.Decide(9 * mb, 8, true, 0, 1, measuredPerConnection: 8 * mb) == 1, "Fast short transfer opened unnecessary ranges.");
        var small = new byte[1200]; new Random(71).NextBytes(small);
        var validationRequests = new List<string>();
        using (var http = new HttpClient(new Handler((request, _) =>
        {
            validationRequests.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(request.RequestUri.Host.StartsWith("invalid") ? "<html>expired</html>" : "valid-package")) });
        })))
        {
            var path = Path.Combine(root, "validated-mirror.bin");
            await DownloadEndpoints.DownloadFilesWithFallbackAsync(http,
                new[] { "https://invalid.example.org/file.bin", "https://valid.example.org/file.bin" }, false, path, "fixture",
                validateFile: temporary => { if (File.ReadAllText(temporary) != "valid-package") throw new InvalidDataException("not a package"); });
            Require(File.ReadAllText(path) == "valid-package" && validationRequests.Contains("valid.example.org"),
                "Format rejection did not fall back to the next mirror before committing the target.");
        }
        var headRequests = 0;
        using (var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Head) Interlocked.Increment(ref headRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(small) });
        })))
        using (var service = new DownloadService(DownloadSource.Official, http))
        {
            var detail = new VersionDetail { Id = "small-with-manifest-size", Downloads = new()
            { ["client"] = new DownloadArtifact { Url = "https://downloads.example.org/small.jar", Size = small.Length,
                Sha1 = Convert.ToHexString(SHA1.HashData(small)) } } };
            await service.DownloadClientJarOnlyAsync(root, detail, null).WaitAsync(TimeSpan.FromSeconds(5));
            Require(headRequests == 0, "A known small artifact still paid an unnecessary HEAD round-trip.");
        }

        var cfg = ConfigService.Active!.Config;
        var wasMulti = cfg.EnableMultiThreadDownload; var oldThreads = cfg.MaxDownloadThreads;
        cfg.EnableMultiThreadDownload = true; cfg.MaxDownloadThreads = 4;
        var payload = new byte[9 * 1024 * 1024]; new Random(881).NextBytes(payload);
        var hash = Convert.ToHexString(SHA1.HashData(payload));
        try
        {
            cfg.EnableMultiThreadDownload = false;
            var disabledRanges = 0;
            using (var http = new HttpClient(new Handler((request, _) =>
            {
                if (request.Headers.Range != null) Interlocked.Increment(ref disabledRanges);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
                response.Headers.AcceptRanges.Add("bytes"); return Task.FromResult(response);
            })))
            {
                await DownloadEndpoints.DownloadFileWithFallbackAsync(http, "https://single.example.org/file.bin", false,
                    Path.Combine(root, "disabled-ranges.bin"), "fixture", expectedSha1: hash);
                Require(disabledRanges == 0, "Disabling multi-threaded downloads still made range requests.");
            }
            cfg.EnableMultiThreadDownload = true;
            foreach (var mode in new[] { "range", "ignored", "truncated" })
            {
                var ranges = 0; var singles = 0; var reports = 0;
                using var http = new HttpClient(new Handler((request, _) =>
                {
                    Require(request.Method == HttpMethod.Get, "Generic transfer introduced a HEAD probe.");
                    if (request.Headers.Range is null)
                    {
                        Interlocked.Increment(ref singles);
                        var initial = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
                        initial.Headers.AcceptRanges.Add("bytes"); return Task.FromResult(initial);
                    }
                    Interlocked.Increment(ref ranges);
                    if (mode == "ignored")
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
                    var range = request.Headers.Range.Ranges.Single();
                    var from = range.From!.Value; var to = range.To!.Value;
                    var bytes = payload.AsSpan((int)from, mode == "truncated" ? 1 : (int)(to - from + 1)).ToArray();
                    var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes) };
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, payload.Length);
                    return Task.FromResult(response);
                }));
                var path = Path.Combine(root, "generic-acceleration-" + mode + ".bin");
                if (mode == "truncated") await File.WriteAllTextAsync(path, "previous complete file");
                try
                {
                    await DownloadEndpoints.DownloadFileWithFallbackAsync(http, "https://" + mode + ".acceleration.example.org/large.bin", false,
                        path, "fixture", new ImmediateProgress(_ => Interlocked.Increment(ref reports)), expectedSha1: hash)
                        .WaitAsync(TimeSpan.FromSeconds(10));
                    Require(mode != "truncated", "Truncated ranges were committed as complete.");
                    Require(File.ReadAllBytes(path).SequenceEqual(payload), "Generic parallel/fallback download corrupted the payload.");
                    Require(ranges >= 2, "A large generic resource did not use bounded range transfers.");
                    Require(mode != "ignored" || singles == 2, "Ignored Range did not retry as exactly one complete GET.");
                    Require(reports < 40, "Generic transfer flooded progress callbacks for individual buffers.");
                }
                catch (IOException) when (mode == "truncated")
                { Require(File.ReadAllText(path) == "previous complete file", "A failed parallel transfer replaced an intact target."); }
                Require(!Directory.EnumerateFiles(root, "*.fbpart-*").Any(), "Generic range transfer leaked temporary files.");
            }
            var before = DownloadTransferPolicy.TotalReceivedBytes;
            await DownloadTransferPolicy.ConsumeAsync(17, CancellationToken.None);
            await DownloadTransferPolicy.ConsumeAsync(29, CancellationToken.None);
            Require(DownloadTransferPolicy.TotalReceivedBytes - before == 46, "Global smart-bandwidth accounting did not include every transfer.");
        }
        finally { cfg.EnableMultiThreadDownload = wasMulti; cfg.MaxDownloadThreads = oldThreads; }
    }
    private sealed class ImmediateProgress(Action<ProgressInfo> report) : IProgress<ProgressInfo>
    { public void Report(ProgressInfo value) => report(value); }
}
