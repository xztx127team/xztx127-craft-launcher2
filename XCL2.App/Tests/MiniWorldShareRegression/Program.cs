using System.Net;
using System.Text;
using XCL2.App.Services;

var count = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
void Reject<T>(Action action, string name) where T : Exception
{ try { action(); } catch (T) { count++; return; } throw new Exception(name); }
async Task RejectAsync<T>(Func<Task> action, string name) where T : Exception
{ try { await action(); } catch (T) { count++; return; } throw new Exception(name); }
const string share = "https://mengzee.lanzoul.com/ihuZq3n1ez0h";
const string page = "<html><script>var isngis='';var kdns=1;var isngis='public-session-value';var cfg={url:'https://apifile.woozooo.com/ajaxfile.php?file=12345'};</script></html>";
const string response = "{\"zt\":1,\"dom\":\"https://developer4.lanrar.com\",\"url\":\"?publicDownloadToken\",\"inf\":\"community.7z\"}";
Check(MiniWorldPublicShareResolver.Supports(share), "verified public share supported");
Check(!MiniWorldPublicShareResolver.Supports("http://mengzee.lanzoul.com/ihuZq3n1ez0h") &&
    !MiniWorldPublicShareResolver.Supports("https://mengzee.lanzoul.com.evil.invalid/ihuZq3n1ez0h") &&
    !MiniWorldPublicShareResolver.Supports("https://user:pass@mengzee.lanzoul.com/ihuZq3n1ez0h"), "unverified origin and credentials rejected");
var parsed = MiniWorldPublicShareResolver.ParsePublicForm(page);
Check(parsed.Sign == "public-session-value" && parsed.Kd == "1" && parsed.Endpoint.EndsWith("file=12345"), "only published form fields parsed");
Reject<InvalidOperationException>(() => MiniWorldPublicShareResolver.ParsePublicForm("<html>Please complete captcha</html>"), "captcha page is not bypassed");
Reject<InvalidDataException>(() => MiniWorldPublicShareResolver.ParsePublicResponse(response.Replace("developer4.lanrar.com", "127.0.0.1"), share), "download server cannot redirect to loopback");
Reject<InvalidDataException>(() => MiniWorldPublicShareResolver.ParsePublicResponse(response.Replace("community.7z", "../escape.exe"), share), "download filename cannot escape directory");
Reject<InvalidOperationException>(() => MiniWorldPublicShareResolver.ParsePublicResponse("{\"zt\":3,\"inf\":\"captcha\"}", share), "verification response fails without retry");
var signature = new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c, 0, 4 };
Check(MiniWorldPublicShareResolver.IsArchiveOrExecutable(signature) &&
    !MiniWorldPublicShareResolver.IsArchiveOrExecutable(Encoding.UTF8.GetBytes("<html>")), "HTML cannot masquerade as a package");
Check(MiniWorldPublicShareResolver.IsBinaryContinuation(new byte[] { 0x82, 0, 0x45, 0x90, 0xf1, 0x14, 0x55, 0x34 }) &&
    !MiniWorldPublicShareResolver.IsBinaryContinuation(Encoding.UTF8.GetBytes("<html>challenge</html>")) &&
    !MiniWorldPublicShareResolver.IsBinaryContinuation(Encoding.UTF8.GetBytes("{\"error\":\"challenge\"}")), "continuation volumes accept binary and reject verification text");
var handler = new PublicShareHandler(page, response, signature);
using (var http = new HttpClient(handler))
{
    var result = await MiniWorldPublicShareResolver.ResolveAsync(http, share, "1234", default);
    Check(result.Url.StartsWith("https://developer4.lanrar.com/file/?") && result.FileName == "community.7z", "published download returned after package header probe");
    Check(handler.Count == 3 && handler.Form?.Contains("p=1234") == true, "one normal password submission and no hidden retries");
}
var blocked = new PublicShareHandler(page, response, Encoding.UTF8.GetBytes("<html><script>challenge</script></html>"));
using (var http = new HttpClient(blocked))
    await RejectAsync<InvalidOperationException>(() => MiniWorldPublicShareResolver.ResolveAsync(http, share, "1234", default), "HTML challenge stops the download");
Check(blocked.Count == 3, "challenge does not trigger repeated validation requests");
Console.WriteLine($"MiniWorld share regression: {count} checks passed");

sealed class PublicShareHandler(string page, string json, byte[] package) : HttpMessageHandler
{
    public int Count { get; private set; }
    public string? Form { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Count++;
        HttpContent content;
        if (Count == 1) content = new StringContent(page, Encoding.UTF8, "text/html");
        else if (Count == 2) { Form = await request.Content!.ReadAsStringAsync(ct); content = new StringContent(json, Encoding.UTF8, "application/json"); }
        else if (Count == 3) content = new ByteArrayContent(package);
        else throw new InvalidOperationException("Unexpected retry");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
    }
}
