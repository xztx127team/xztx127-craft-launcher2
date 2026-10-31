using System.Net;
using System.Text;
using System.Text.Json;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
async Task Reject<T>(Func<Task> action, string label) where T : Exception
{ try { await action(); } catch (T) { checks++; return; } throw new Exception(label); }
const string valid = """{"data":{"url":"https://downloads.example.test/game.7z","fileName":"game.7z","mirrors":["https://mirror.example.test/game.7z"]}}""";
var parsed = MiniWorldCdkService.ParseResponse(valid, "fallback.exe");
Check(parsed.FileName == "game.7z" && parsed.Mirrors.Count == 1, "response filename takes precedence and mirrors survive");
foreach (var invalid in new[] {
    valid.Replace("https://downloads.example.test", "file:///tmp"),
    valid.Replace("https://downloads.example.test", "https://user:secret@downloads.example.test"),
    valid.Replace("\"fileName\":\"game.7z\"", "\"fileName\":\"../escape.exe\""),
    valid.Replace("\"fileName\":\"game.7z\"", "\"fileName\":\"CON.exe\""),
    valid.Replace("https://mirror.example.test", "http://mirror.example.test"),
    valid.Replace("\"mirrors\":", "\"sha256\":\"invalid\",\"mirrors\":"),
    "[]" })
    await Reject<System.IO.InvalidDataException>(() => Task.Run(() => MiniWorldCdkService.ParseResponse(invalid, "game.7z")), "invalid redemption must be rejected");
await Reject<System.IO.InvalidDataException>(() => Task.Run(() => MiniWorldCdkService.ParseResponse(new string('x', 65537), "game.7z")), "response limit applies before parsing");
var source = new MiniWorldSource("publisher", "Publisher", "community", "", "https://publisher.example.test/");
var cdkOnly = MiniWorldVersionService.ParseIndex("""[{"id":"v1","name":"Test","version":"1","fileName":"game.7z","cdkRedeemUrl":"https://publisher.example.test/redeem"}]""", source).Single();
Check(cdkOnly.DownloadUrl == "" && cdkOnly.CdkRedeemUrl.EndsWith("/redeem"), "CDK-only catalog entries remain selectable without fake direct URLs");
Check(!(await MiniWorldVersionService.ProbeAsync(cdkOnly)).Available, "CDK-only entries report redemption requirement without an HTTP probe");
var requests = 0;
using var http = new HttpClient(new FakeHandler(async (request, cancellation) =>
{
    requests++;
    Check(request.Method == HttpMethod.Post && request.RequestUri!.Query.Length == 0, "CDK uses POST and is absent from the URL");
    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
    Check(body.RootElement.GetProperty("cdk").GetString() == "TEST-CODE"
        && body.RootElement.GetProperty("versionId").GetString() == "v1", "only selected CDK and version are submitted");
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(valid, Encoding.UTF8, "application/json") };
}));
var service = new MiniWorldCdkService(http);
Check((await service.RedeemAsync(cdkOnly.CdkRedeemUrl, "TEST-CODE", "v1", "fallback.exe")).FileName == "game.7z", "redemption completes using the production HTTP path");
Check(requests == 1, "a one-time CDK is submitted once without retries");
await Reject<System.IO.InvalidDataException>(() => service.RedeemAsync(cdkOnly.CdkRedeemUrl, "bad\ncode", "v1", "game.7z"), "control characters rejected before any HTTP call");
Check(requests == 1, "input failure does not submit a code");
foreach (var status in new[] { HttpStatusCode.Redirect, HttpStatusCode.Forbidden })
{
    using var blocked = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
    await Reject<System.IO.InvalidDataException>(() => new MiniWorldCdkService(blocked).RedeemAsync(cdkOnly.CdkRedeemUrl, "CODE", "v1", "game.7z"), "redirects and failed authorization are visible failures");
}
using var slow = new HttpClient(new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); }));
using var cancel = new CancellationTokenSource(); cancel.Cancel();
await Reject<OperationCanceledException>(() => new MiniWorldCdkService(slow).RedeemAsync(cdkOnly.CdkRedeemUrl, "CODE", "v1", "game.7z", cancel.Token), "closing/cancellation stops redemption");
Console.WriteLine($"PASS: {checks} MiniWorld CDK regression checks (no live services).");

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
