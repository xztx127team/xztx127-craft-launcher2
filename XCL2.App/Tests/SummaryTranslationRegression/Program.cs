using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
}
HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
{
    Content = new StringContent(body, Encoding.UTF8, "application/json")
};
HttpResponseMessage Success(string translated) => Json(JsonSerializer.Serialize(new
{
    responseStatus = 200, responseData = new { translatedText = translated }
}));
string QueryText(HttpRequestMessage request)
{
    Check(request.Method == HttpMethod.Get, "Translation is a lookup, never a contribution POST");
    var uri = request.RequestUri!;
    Check(uri.Scheme == "https" && uri.Host == "api.mymemory.translated.net" && uri.AbsolutePath == "/get", "Only documented HTTPS endpoint");
    var parameters = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
        .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
    Check(parameters.Count == 2 && parameters.ContainsKey("q") && parameters["langpair"] == "en|zh-CN", "Only summary and language pair sent; no account/key/email");
    Check(request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "No credentials sent");
    Check(Encoding.UTF8.GetByteCount(parameters["q"]) <= 500, "Each query respects 500 UTF-8 byte limit");
    return parameters["q"];
}
async Task Failure(Func<Task> action, SummaryTranslationFailure failure, string name)
{
    try { await action(); throw new InvalidOperationException(name + " did not fail"); }
    catch (SummaryTranslationException ex) { Check(ex.Failure == failure, name); }
}

var summaries = new[]
{
    "   Supports shaders & textures.  \r\n\r\n\tAdds an English description. \n",
    string.Concat(Enumerable.Repeat("Text 中文 😀 ", 80)),
    new string('x', 1400),
    "abc " + new string('中', 280) + "\n" + new string('x', 501),
    string.Concat(Enumerable.Repeat("😀", 400))
};
foreach (var original in summaries)
{
    var chunks = SummaryTranslationService.SplitSummary(original);
    Check(string.Concat(chunks.Select(c => c.Text)) == original, "Chunking is lossless including indentation and CRLF");
    foreach (var part in chunks.Where(c => c.Translate))
    {
        Check(!string.IsNullOrWhiteSpace(part.Text) && Encoding.UTF8.GetByteCount(part.Text) <= 500, "No empty or oversized request chunk");
        Check(!char.IsLowSurrogate(part.Text[0]) && !char.IsHighSurrogate(part.Text[^1]), "No split surrogate pairs");
    }
    using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Success(QueryText(request)))));
    var service = new SummaryTranslationService(http);
    Check(await service.TranslateToChineseAsync(original) == original, "Identity server preserves all original whitespace/line breaks");
}

var calls = 0;
using (var http = new HttpClient(new Handler((request, _) =>
{
    calls++;
    QueryText(request);
    return Task.FromResult(Success("中文 &amp; 世界"));
})))
{
    var service = new SummaryTranslationService(http, cacheCapacity: 2);
    Check(await service.TranslateToChineseAsync("First") == "中文 & 世界", "HTML entities decoded as plain text");
    await service.TranslateToChineseAsync("First");
    Check(calls == 1, "Repeated click uses memory cache");
    await service.TranslateToChineseAsync("Second");
    await service.TranslateToChineseAsync("Third");
    await service.TranslateToChineseAsync("First");
    Check(calls == 4, "Cache evicts older entries at capacity");
    await Failure(() => service.TranslateToChineseAsync(new string('中', 1334)), SummaryTranslationFailure.TooLong, "Oversized summary rejected before network");
    await Failure(() => service.TranslateToChineseAsync(string.Concat(Enumerable.Repeat("a\n", 100))), SummaryTranslationFailure.TooLong, "Too many short lines cannot generate an unbounded number of requests");
    Check(calls == 4, "Too-long summary never sent");
    Check(await service.TranslateToChineseAsync("\r\n \t") == "\r\n \t" && calls == 4, "Blank summary never sent");
}

foreach (var body in new[]
{
    "{\"quotaFinished\":true,\"responseStatus\":403}",
    "{\"quotaFinished\":\"true\",\"responseStatus\":200}",
    "{\"responseStatus\":429}",
    "{\"responseStatus\":\"429\"}"
})
{
    using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(body))));
    await Failure(() => new SummaryTranslationService(http).TranslateToChineseAsync("Example"), SummaryTranslationFailure.QuotaExceeded, "Provider quota is distinct from a translation");
}
using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.TooManyRequests)))))
    await Failure(() => new SummaryTranslationService(http).TranslateToChineseAsync("Example"), SummaryTranslationFailure.QuotaExceeded, "HTTP 429 shows quota feedback");

foreach (var body in new[] { "[]", "not JSON", "{}", "{\"responseStatus\":200,\"responseData\":null}", "{\"responseStatus\":200,\"responseData\":{\"translatedText\":\"\"}}", new string('x', 65537) })
{
    using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(body))));
    await Failure(() => new SummaryTranslationService(http).TranslateToChineseAsync("Example"), SummaryTranslationFailure.InvalidResponse, "Malformed/oversized response is rejected");
}
foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.Redirect })
{
    using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json("{}", status))));
    await Failure(() => new SummaryTranslationService(http).TranslateToChineseAsync("Example"), SummaryTranslationFailure.Unavailable, "Failure/redirect not accepted as translated text");
}

calls = 0;
using (var http = new HttpClient(new Handler((request, _) =>
{
    calls++;
    return Task.FromResult(calls == 2 ? Json("{}") : Success(QueryText(request)));
})))
{
    var service = new SummaryTranslationService(http);
    var original = new string('x', 1200);
    await Failure(() => service.TranslateToChineseAsync(original), SummaryTranslationFailure.InvalidResponse, "Second chunk failure rejects incomplete result");
    Check(await service.TranslateToChineseAsync(original) == original && calls == 5, "No partial result enters cache after failure");
}

calls = 0;
using (var http = new HttpClient(new Handler(async (request, cancellation) =>
{
    calls++;
    await Task.Delay(20, cancellation);
    return Success(QueryText(request));
})))
{
    var service = new SummaryTranslationService(http);
    await Task.WhenAll(service.TranslateToChineseAsync("Shared request"), service.TranslateToChineseAsync("Shared request"));
    Check(calls == 1, "Concurrent identical calls share completed cache result");
}

using (var http = new HttpClient(new Handler(async (_, cancellation) =>
{
    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
    return Success("unreachable");
})))
{
    await Failure(() => new SummaryTranslationService(http, TimeSpan.FromMilliseconds(40)).TranslateToChineseAsync("Example"), SummaryTranslationFailure.Timeout, "Bounded total timeout");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
    try
    {
        await new SummaryTranslationService(http).TranslateToChineseAsync("Example", cancellation.Token);
        throw new InvalidOperationException("Expected caller cancellation");
    }
    catch (OperationCanceledException) { Check(cancellation.IsCancellationRequested, "Unloading/user cancellation remains cancellation"); }
}

// Even a misbehaving handler that ignores cancellation must not publish/cache a canceled result.
using (var cancellation = new CancellationTokenSource())
using (var http = new HttpClient(new Handler((_, _) =>
{
    cancellation.Cancel();
    return Task.FromResult(Success("late reply"));
})))
{
    try
    {
        await new SummaryTranslationService(http).TranslateToChineseAsync("Example", cancellation.Token);
        throw new InvalidOperationException("Canceled response accepted");
    }
    catch (OperationCanceledException) { checks++; }
}
Console.WriteLine($"Summary translation regression checks passed: {checks}");

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
}
