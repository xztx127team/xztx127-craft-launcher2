using System.Net;
using System.Net.Http;
using System.Text;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class ModelFetchChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            checks++;
        }

        const string models = "{\"data\":[{\"id\":\"same-model\"},{\"id\":\"text-embedding-test\"}]}";
        Check(AiModelListFetcher.BuildModelsUrl("https://second.example/prefix/v1/chat/completions?region=west#local")
            == "https://second.example/prefix/v1/models?region=west", "model fetch normalizes complete chat URL and preserves query");
        Check(AiModelListFetcher.BuildModelsUrl("https://second.example/prefix/v1/models/?region=west")
            == "https://second.example/prefix/v1/models?region=west", "model fetch does not duplicate models suffix");

        var endpoint = new AiApiEndpoint
        {
            Id = "second", Name = "Second endpoint", BaseUrl = "https://second.example/prefix/v1/chat/completions?region=west",
            ApiKeys = new() { "first-test-key", "second-test-key" }, Enabled = true
        };
        using (var handler = new RecordingHandler((request, attempt, _) => Task.FromResult(
            Reply(attempt == 1 ? 401 : 200, attempt == 1 ? "{\"error\":\"invalid_api_key\"}" : models))))
        using (var client = new HttpClient(handler))
        {
            var result = await AiModelListFetcher.FetchAsync(endpoint, client);
            Check(result.Success && result.Models.Single().ProviderId == "second"
                && result.Models.Single().ProviderName == "Second endpoint" && result.SkippedNonChat == 1,
                "fetched models retain exact endpoint identity and skip embeddings");
            Check(result.SuccessfulKeyIndex == 1 && handler.Requests.Select(r => r.Key).SequenceEqual(endpoint.ApiKeys),
                "auth failure tries next key in the same endpoint and reports successful key index");
            Check(handler.Requests.All(r => r.Url == "https://second.example/prefix/v1/models?region=west"),
                "every key uses selected endpoint URL without public or legacy fallback");
        }
        using (var handler = new RecordingHandler((_, _, _) => Task.FromResult(Reply(401, "invalid_api_key"))))
        using (var client = new HttpClient(handler))
        {
            var result = await AiModelListFetcher.FetchAsync(endpoint, client, enableKeyFailover: false);
            Check(!result.Success && handler.Requests.Count == 1, "model fetch respects disabled key failover");
        }
        foreach (var (status, body, expectedRequests) in new[]
        {
            (403, "forbidden by organization policy", 1),
            (403, "invalid_api_key", 2),
            (400, "insufficient_quota", 2),
            (500, "upstream insufficient_quota", 1),
            (404, "model list unavailable", 1)
        })
        {
            using var handler = new RecordingHandler((_, _, _) => Task.FromResult(Reply(status, body)));
            using var client = new HttpClient(handler);
            var result = await AiModelListFetcher.FetchAsync(endpoint, client);
            Check(!result.Success && handler.Requests.Count == expectedRequests,
                $"model fetch classifies status {status} / {body} without wasting keys");
        }
        using (var handler = new RecordingHandler((request, _, _) => Task.FromResult(Reply(401,
                   "invalid_api_key: " + request.Headers.Authorization?.Parameter))))
        using (var client = new HttpClient(handler))
        {
            var result = await AiModelListFetcher.FetchAsync(endpoint, client);
            Check(endpoint.ApiKeys.All(key => !result.Error.Contains(key, StringComparison.Ordinal)),
                "model fetch errors redact API credentials");
        }
        using (var handler = new RecordingHandler((_, _, _) => Task.FromResult(Reply(200, models))))
        using (var client = new HttpClient(handler))
        {
            var disabled = endpoint.Clone();
            disabled.Enabled = false;
            var result = await AiModelListFetcher.FetchAsync(disabled, client);
            Check(!result.Success && handler.Requests.Count == 0, "disabled endpoint never sends a model request");
            var local = endpoint.Clone();
            local.ApiKeys.Clear();
            result = await AiModelListFetcher.FetchAsync(local, client);
            Check(result.Success && handler.Requests.Single().Key == null,
                "local compatible endpoint can fetch models without an Authorization header");
        }
        using (var cancellation = new CancellationTokenSource())
        using (var handler = new RecordingHandler((_, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Reply(200, models));
        }))
        using (var client = new HttpClient(handler))
        {
            var cancelled = false;
            try { await AiModelListFetcher.FetchAsync(endpoint, client, ct: cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && handler.Requests.Count == 1, "cancelled fetch stops without trying another key");
        }
        return checks;
    }

    private static HttpResponseMessage Reply(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<(string Url, string? Key)> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.Parameter));
            return send(request, Requests.Count, cancellationToken);
        }
    }
}
