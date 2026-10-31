using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Require(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
static HttpResponseMessage Response(HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent("{\"data\":[]}") };
static Task NoWait(TimeSpan delay, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
const string Url = "https://api.curseforge.com/v1/mods/search?gameId=432";
const string Key = "fixture-api-key-not-a-credential";

using (var tls12 = LauncherHttpClientFactory.CreateTransport(true, false, DecompressionMethods.GZip | DecompressionMethods.Deflate, false))
using (var modern = LauncherHttpClientFactory.CreateTransport(false, true, DecompressionMethods.None, true))
{
    Require(tls12.SslProtocols == SslProtocols.Tls12 && modern.SslProtocols == SslProtocols.None, "TLS policy must preserve OS negotiation on modern systems");
    Require(tls12.ServerCertificateCustomValidationCallback == null && modern.ServerCertificateCustomValidationCallback == null, "Certificate validation must remain enabled");
    Require(!tls12.AllowAutoRedirect && !tls12.UseCookies, "API credentials must not follow redirects");
    Require(modern.AutomaticDecompression == DecompressionMethods.None, "File ranges/hashes require unmodified response bytes");
}
var profile = WindowsCompatibilityService.Evaluate(new Version(6, 1, 7601), WindowsCompatibilityMode.Auto);
var usedTls12 = false;
using (var http = new HttpClient(new LauncherHttpClientFactory.CompatibilityHandler(() => profile, tls12 =>
    new FixtureHandler((request, ct) =>
    {
        usedTls12 = tls12;
        Require(request.Version == HttpVersion.Version11 && request.VersionPolicy ==
            (profile.IsEnabled ? HttpVersionPolicy.RequestVersionExact : HttpVersionPolicy.RequestVersionOrLower),
            "Compatibility requests must use HTTP/1.1 exactly; disabling compatibility restores the client's policy");
        return Task.FromResult(Response());
    }))))
{
    using var win7Request = new HttpRequestMessage(HttpMethod.Get, Url) { Version = HttpVersion.Version30 };
    using var first = await http.SendAsync(win7Request);
    Require(usedTls12, "Windows 7 automatic mode must explicitly request TLS 1.2");
    profile = WindowsCompatibilityService.Evaluate(new Version(10, 0, 22621), WindowsCompatibilityMode.Enabled);
    using var second = await http.GetAsync(Url);
    Require(!usedTls12, "Manual compatibility on modern Windows must retain modern TLS support");
    profile = WindowsCompatibilityService.Evaluate(new Version(6, 1, 7601), WindowsCompatibilityMode.Disabled);
    using var third = await http.GetAsync(Url);
    Require(!usedTls12, "Existing clients must observe compatibility changes on the next request");
}
var invoked = 0;
profile = WindowsCompatibilityService.Evaluate(new Version(6, 0), WindowsCompatibilityMode.Auto);
using (var http = new HttpClient(new LauncherHttpClientFactory.CompatibilityHandler(() => profile, _ =>
    new FixtureHandler((_, _) => { invoked++; return Task.FromResult(Response()); }))))
{
    try { using var response = await http.GetAsync(Url); throw new Exception("Vista TLS1.2 was falsely reported as supported"); }
    catch (HttpRequestException ex) { Require(ex.Message.Contains("TLS 1.2") && invoked == 0, "Vista must report its missing TLS provider"); }
}
profile = WindowsCompatibilityService.Evaluate(new Version(6, 1, 7601), WindowsCompatibilityMode.Auto);
using (var http = new HttpClient(new LauncherHttpClientFactory.CompatibilityHandler(() => profile, _ =>
    new FixtureHandler((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture", new AuthenticationException("fixture handshake failure")))))))
{
    try { using var response = await http.GetAsync(Url); throw new Exception("TLS failure was ignored"); }
    catch (HttpRequestException ex) { Require(ex.Message.Contains("可信根证书") && LauncherHttpClientFactory.IsTlsFailure(ex), "Legacy TLS diagnostics must preserve the original exception"); }
}

// Each retry must build a fresh authenticated request, never retry credential or redirect failures.
var calls = 0;
var requests = new HashSet<HttpRequestMessage>();
using (var http = new HttpClient(new FixtureHandler((request, _) =>
{
    calls++; Require(requests.Add(request), "A disposed HTTP request was reused on retry");
    Require(request.Headers.GetValues("x-api-key").Single() == Key, "Missing API key");
    Require(request.RequestUri!.Host == "api.curseforge.com", "API key escaped the official host");
    return Task.FromResult(Response(calls < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
})))
{
    var result = await CurseForgeApiTransport.GetStringAsync(http, Url, Key, default, NoWait);
    Require(calls == 3 && result.Contains("data"), "Transient failures must have bounded retries");
    Require(!http.DefaultRequestHeaders.Contains("x-api-key"), "API key must be request-scoped, not attached to downloads");
}
foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Redirect, HttpStatusCode.NotFound })
{
    calls = 0;
    using var http = new HttpClient(new FixtureHandler((_, _) => { calls++; return Task.FromResult(Response(status)); }));
    try { await CurseForgeApiTransport.GetStringAsync(http, Url, Key, default, NoWait); throw new Exception("Expected API failure"); }
    catch (HttpRequestException ex) { Require(calls == 1 && ex.StatusCode == status && !ex.ToString().Contains(Key), "Permanent status must not be retried or leak key"); }
}
foreach (var badUrl in new[] { "https://cdn.example.org/file", "http://api.curseforge.com/v1/mods", "https://api.curseforge.com:8443/v1/mods", "https://user@api.curseforge.com/v1/mods" })
{
    calls = 0;
    using var http = new HttpClient(new FixtureHandler((_, _) => { calls++; return Task.FromResult(Response()); }));
    try { await CurseForgeApiTransport.GetStringAsync(http, badUrl, Key, default, NoWait); throw new Exception("Untrusted API URL accepted"); }
    catch (InvalidOperationException) { Require(calls == 0, "Credentials transmitted before endpoint validation"); }
}
calls = 0;
using (var http = new HttpClient(new FixtureHandler((_, _) =>
{
    calls++; var response = Response(HttpStatusCode.TooManyRequests);
    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120)); return Task.FromResult(response);
})))
{
    try { await CurseForgeApiTransport.GetStringAsync(http, Url, Key, default, NoWait); throw new Exception("Excessive rate limit accepted"); }
    catch (HttpRequestException) { Require(calls == 1, "Retry-After must not be bypassed"); }
}
calls = 0;
using (var source = new CancellationTokenSource())
using (var http = new HttpClient(new FixtureHandler((_, ct) =>
{
    calls++; source.Cancel(); return Task.FromCanceled<HttpResponseMessage>(ct);
})))
{
    try { await CurseForgeApiTransport.GetStringAsync(http, Url, Key, source.Token, NoWait); throw new Exception("User cancellation swallowed"); }
    catch (OperationCanceledException) { Require(calls == 1, "User cancellation must not be retried"); }
}
calls = 0;
using (var http = new HttpClient(new FixtureHandler((_, _) =>
{
    calls++; return Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture", new AuthenticationException("TLS")));
})))
{
    try { await CurseForgeApiTransport.GetStringAsync(http, Url, Key, default, NoWait); throw new Exception("TLS failure swallowed"); }
    catch (HttpRequestException) { Require(calls == 1, "TLS certificate failures must not trigger network retries"); }
}
var targetRoot = Path.Combine(Path.GetTempPath(), "xcl-resource-policy-" + Guid.NewGuid().ToString("N"));
Require(ResourceDownloadPath.ResolveChild(targetRoot, "中文-resource.jar") == Path.Combine(targetRoot, "中文-resource.jar"), "Valid resource filename rejected");
foreach (var name in new[] { "../escape.jar", @"..\escape.jar", "C:payload.jar", "NUL.jar", "", "..", "trailing.jar.", "nested/file.jar" })
{
    try { ResourceDownloadPath.ResolveChild(targetRoot, name); throw new Exception("Unsafe resource path accepted: " + name); }
    catch (InvalidDataException) { Require(true, "Unsafe remote filename rejected"); }
}
Console.WriteLine($"PASS LegacyNetworkRegression ({checks} checks; simulated HTTP/TLS failures, no external API credential or network required)");

sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
}
