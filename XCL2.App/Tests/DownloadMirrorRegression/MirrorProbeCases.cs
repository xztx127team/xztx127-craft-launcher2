using System.Net;
using System.Net.Http.Headers;
using XCL2.App.Services;

internal static class MirrorProbeCases
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        DownloadEndpoints.ResetHealth();
        var candidates = new[] { "https://maven.fabricmc.net/fixture/loader.jar", "https://bmclapi2.bangbang93.com/maven/fixture/loader.jar" };
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using (var http = new HttpClient(new ProbeHandler(async (request, ct) =>
        {
            Require(request.Method == HttpMethod.Head, "Source probe downloaded a body.");
            if (Interlocked.Increment(ref requests) == 2) bothStarted.TrySetResult();
            await bothStarted.Task.WaitAsync(ct);
            if (request.RequestUri!.Host == "maven.fabricmc.net") await Task.Delay(100, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        })))
        {
            var ordered = await DownloadEndpoints.ProbeCandidatesAsync(http, candidates, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Require(requests == 2 && ordered[0] == candidates[1], "Sources were not probed concurrently or fastest healthy source was ignored.");
            var nextFiles = candidates.Select(url => url.Replace("loader.jar", "other.jar")).ToArray();
            await DownloadEndpoints.ProbeCandidatesAsync(http, nextFiles, CancellationToken.None);
            Require(requests == 2, "Every small library repeated the host probe instead of sharing its cached result.");
        }

        DownloadEndpoints.ResetHealth();
        using (var http = new HttpClient(new ProbeHandler((request, _) => Task.FromResult(new HttpResponseMessage(
            request.RequestUri!.Host == "maven.fabricmc.net" ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.OK)))))
        {
            var ordered = await DownloadEndpoints.ProbeCandidatesAsync(http, candidates, CancellationToken.None);
            Require(ordered.Count == 2 && ordered[0] == candidates[1] && ordered.Contains(candidates[0]),
                "A source without HEAD support was removed from the GET fallback pool.");
        }

        DownloadEndpoints.ResetHealth();
        using (var http = new HttpClient(new ProbeHandler((_, _) => throw new InvalidOperationException("Private request reached probe transport."))))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-only");
            await DownloadEndpoints.ProbeCandidatesAsync(http, candidates, CancellationToken.None);
            http.DefaultRequestHeaders.Authorization = null;
            await DownloadEndpoints.ProbeCandidatesAsync(http, candidates.Select(url => url + "?signature=private").ToArray(), CancellationToken.None);
        }

        DownloadEndpoints.ResetHealth();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        using (var cancel = new CancellationTokenSource())
        using (var http = new HttpClient(new ProbeHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref active);
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(HttpStatusCode.OK); }
            finally { Interlocked.Decrement(ref active); }
        })))
        {
            var operation = DownloadEndpoints.ProbeCandidatesAsync(http, candidates, cancel.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Source probing ignored cancellation."); }
            catch (OperationCanceledException) { }
            Require(active == 0, "Canceled source probing returned while a request was still active.");
        }
        DownloadEndpoints.ResetHealth();
    }

    private sealed class ProbeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
