using System.Net;
using System.Text.Json;
using XCL2.App.Services;

var checks = 0;
void Check(bool result, string message)
{
    if (!result) throw new Exception(message);
    checks++;
}
string Maven(string version) => $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{version}/neoforge-{version}-installer.jar";
string Cdn(string version) => $"https://modloaders.forgecdn.net/fixture/neoforge-{version}-installer.jar";
Dictionary<string, object?> Detail(string version) => new()
{
    ["name"] = "neoforge-" + version, ["type"] = 6, ["installMethod"] = 6,
    ["forgeVersion"] = version, ["mavenVersionString"] = "net.neoforged:neoforge:" + version,
    ["filename"] = "neoforge-" + version + "-installer.jar", ["approved"] = true,
    ["downloadUrl"] = Cdn(version)
};
string Json(Dictionary<string, object?> value) => JsonSerializer.Serialize(new { data = value });
HttpResponseMessage Response(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text) };
const string Version = "21.1.1";
var original = Maven(Version);
var expected = Cdn(Version);
var canonical = "neoforge-" + Version;
Check(CurseForgeNeoForgeCdnService.TryReadInstaller(original, out var artifact), "Official installer URL rejected.");
Check(CurseForgeNeoForgeCdnService.TryReadInstaller(Maven("26.1.0.5-beta"), out _), "Modern beta installer rejected.");
foreach (var url in new[]
{
    original.Replace("maven.neoforged.net", "maven.neoforged.net.evil.invalid"), original.Replace("https:", "http:"),
    original.Replace("maven.neoforged.net", "user:secret@maven.neoforged.net"), original + "?signature=secret",
    original + "#token=secret", original.Replace("-installer.jar", "-universal.jar"),
    original.Replace("releases/", "releases-other/"), original.Replace("/net/neoforged/neoforge/", "/net/neoforged/forge/"),
    original.Replace(Version + "/", "21.1.2/"), original.Replace("maven.neoforged.net", "maven.neoforged.net:8443")
}) Check(!CurseForgeNeoForgeCdnService.TryReadInstaller(url, out _), "Non-installer/private/spoofed URL resolved: " + url);

Check(CurseForgeNeoForgeCdnService.ReadCdnUrl(Json(Detail(Version)), canonical, artifact) == expected, "API-published URL was not retained exactly.");
foreach (var host in new[] { "edge.forgecdn.net", "mediafilez.forgecdn.net" })
{
    var detail = Detail(Version);
    detail["downloadUrl"] = expected.Replace("modloaders.forgecdn.net", host);
    Check(CurseForgeNeoForgeCdnService.ReadCdnUrl(Json(detail), canonical, artifact) == (string)detail["downloadUrl"]!, "Known API-provided CDN host rejected.");
}
foreach (var pair in new (string Key, object? Value)[]
{
    ("type", 1), ("type", "6"), ("installMethod", 1), ("approved", false), ("name", "neoforge-21.1.2"),
    ("forgeVersion", "21.1.2"), ("mavenVersionString", "net.neoforged:neoforge:21.1.2"),
    ("filename", "neoforge-21.1.2-installer.jar"), ("downloadUrl", null),
    ("downloadUrl", expected + "?token=secret"), ("downloadUrl", expected + "#token=secret"),
    ("downloadUrl", expected.Replace("https:", "http:")),
    ("downloadUrl", expected.Replace("modloaders.forgecdn.net", "modloaders.forgecdn.net.evil.invalid")),
    ("downloadUrl", expected.Replace("modloaders.forgecdn.net", "user:secret@modloaders.forgecdn.net")),
    ("downloadUrl", expected.Replace("modloaders.forgecdn.net", "127.0.0.1")),
    ("downloadUrl", expected.Replace("modloaders.forgecdn.net", "modloaders.forgecdn.net:8443")),
    ("downloadUrl", expected.Replace("-installer.jar", "-universal.jar"))
})
{
    var detail = Detail(Version); detail[pair.Key] = pair.Value;
    Check(CurseForgeNeoForgeCdnService.ReadCdnUrl(Json(detail), canonical, artifact) == null, "Mismatched/private CDN metadata accepted: " + pair.Key);
}
var unidentified = Detail(Version); unidentified.Remove("forgeVersion"); unidentified.Remove("mavenVersionString");
Check(CurseForgeNeoForgeCdnService.ReadCdnUrl(Json(unidentified), canonical, artifact) == null, "Artifact without an explicit matching version accepted.");
Check(CurseForgeNeoForgeCdnService.ReadCdnUrl("[]", canonical, artifact) == null, "Malformed envelope accepted.");

var calls = 0;
using (var client = new HttpClient(new Handler((request, _) =>
{
    calls++;
    Check(request.RequestUri!.AbsoluteUri == "https://api.curseforge.com/v1/minecraft/modloader/" + canonical, "Canonical detail was not requested directly.");
    Check(request.Headers.GetValues("x-api-key").Single() == "fixture-only", "API credential missing.");
    return Task.FromResult(Response(Json(Detail(Version))));
})))
{
    var resolver = new CurseForgeNeoForgeCdnService.Resolver(client, () => "fixture-only");
    Check(client.MaxResponseContentBufferSize == 4 * 1024 * 1024, "Metadata HTTP response is not memory-bounded.");
    Check(await resolver.TryResolveAsync(original) == expected, "Valid metadata lookup failed.");
    Check(await resolver.TryResolveAsync(original) == expected && calls == 1, "Validated result was not cached.");
    Check(!client.DefaultRequestHeaders.Contains("x-api-key"), "API credential leaked into shared default headers.");
}
foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.Redirect })
{
    calls = 0;
    using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Response("unavailable", status)); }));
    var resolver = new CurseForgeNeoForgeCdnService.Resolver(client, () => "fixture-only");
    Check(await resolver.TryResolveAsync(original) == null, "Optional API failure must retain Maven fallback.");
    Check(await resolver.TryResolveAsync(original) == null && calls == 1, "Unavailable candidate was repeatedly queried.");
}
using (var client = new HttpClient(new Handler((_, _) => throw new Exception("No request expected."))))
{
    var resolver = new CurseForgeNeoForgeCdnService.Resolver(client, () => null);
    Check(await resolver.TryResolveAsync(original) == null, "Missing API key blocked Maven fallback.");
    Check(await resolver.TryResolveAsync(original + "?signature=private") == null, "Signed original sent to metadata service.");
}
using (var client = new HttpClient(new Handler(async (_, ct) =>
{
    await Task.Delay(Timeout.Infinite, ct); return Response("");
})))
{
    var resolver = new CurseForgeNeoForgeCdnService.Resolver(client, () => "fixture-only", TimeSpan.FromMilliseconds(25));
    Check(await resolver.TryResolveAsync(original) == null, "Metadata timeout must preserve Maven fallback.");
}
using (var cancel = new CancellationTokenSource())
using (var client = new HttpClient(new Handler((_, ct) =>
{
    cancel.Cancel(); return Task.FromCanceled<HttpResponseMessage>(ct);
})))
{
    var resolver = new CurseForgeNeoForgeCdnService.Resolver(client, () => "fixture-only");
    try { await resolver.TryResolveAsync(original, cancel.Token); throw new Exception("Caller cancellation swallowed."); }
    catch (OperationCanceledException) { checks++; }
}
calls = 0;
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using (var client = new HttpClient(new Handler(async (_, ct) =>
{
    Interlocked.Increment(ref calls); await release.Task.WaitAsync(ct); return Response(Json(Detail(Version)));
})))
{
    var resolver = new CurseForgeNeoForgeCdnService.Resolver(client, () => "fixture-only");
    var first = resolver.TryResolveAsync(original);
    var second = resolver.TryResolveAsync(original);
    release.SetResult();
    var results = await Task.WhenAll(first, second);
    Check(results.All(result => result == expected) && calls == 1, "Concurrent lookups duplicated authenticated requests.");
}
calls = 0;
using (var client = new HttpClient(new Handler((request, _) =>
{
    calls++;
    var version = request.RequestUri!.Segments.Last()["neoforge-".Length..];
    return Task.FromResult(Response(Json(Detail(version))));
})))
{
    var resolver = new CurseForgeNeoForgeCdnService.Resolver(client, () => "fixture-only");
    for (var i = 0; i < 65; i++) Check(await resolver.TryResolveAsync(Maven("21.1." + i)) == Cdn("21.1." + i), "Fixture version failed.");
    var before = calls;
    Check(await resolver.TryResolveAsync(Maven("21.1.0")) == Cdn("21.1.0") && calls == before + 1, "Cache exceeded its 64-entry bound.");
}
Console.WriteLine($"NeoForge CDN regression: {checks} checks passed (fake HTTP only).");

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
}
