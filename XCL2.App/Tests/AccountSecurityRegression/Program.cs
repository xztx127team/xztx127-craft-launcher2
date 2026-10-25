using System.Net;
using System.Text;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks++; }
using (var credentialHandler = AuthEndpointPolicy.CreateCredentialHandler())
    Check(!credentialHandler.AllowAutoRedirect, "Credential POST cannot auto-follow 307/308 to another origin");
var redirect = MicrosoftAuthService.NativeClientRedirectUri;
Check(AuthEndpointPolicy.MatchesRedirect(redirect + "?code=ok", redirect), "Exact callback accepts query");
foreach (var bad in new[] { redirect + ".evil?code=bad", redirect + "/child?code=bad", redirect.Replace("https:", "http:"), redirect.Replace("login.microsoftonline.com", "login.microsoftonline.com.evil"), redirect.Replace("https://", "https://name@"), redirect + "#code=bad" })
    Check(!AuthEndpointPolicy.MatchesRedirect(bad, redirect), "Callback rejects " + bad);
Check(AuthEndpointPolicy.BuildApiRootCandidates("https://skin.example/custom/root").First() == "https://skin.example/custom/root", "Arbitrary explicit root tried first");
Check(AuthEndpointPolicy.BuildApiRootCandidates("https://skin.example/custom/authserver/").First() == "https://skin.example/custom", "Authserver suffix removed once");
Check(AuthEndpointPolicy.BuildApiRootCandidates("authlib-injector:yggdrasil-server:https%3A%2F%2Flittleskin.cn%2Fapi%2Fyggdrasil").First() == "https://littleskin.cn/api/yggdrasil", "Authlib address decoded");
foreach (var bad in new[] { "file:///secret", "http://skin.example/api", "https://user:secret@skin.example", "https://skin.example/api?token=secret", "https://skin.example/api#x" })
    Check(!AuthEndpointPolicy.BuildApiRootCandidates(bad).Any(), "Unsafe credential endpoint rejected");
Check(AuthEndpointPolicy.BuildApiRootCandidates("http://localhost:25550/api").Any(), "Local test endpoint retained");

var secretMessage = new AuthStepException("test", "{\"access_token\":\"AT_SECRET\",\"RpsTicket\":\"RP_SECRET\",\"identityToken\":\"ID_SECRET\",\"device_code\":\"DC_SECRET\",\"code_verifier\":\"CV_SECRET\"}").Message;
foreach (var secret in new[] { "AT_SECRET", "RP_SECRET", "ID_SECRET", "DC_SECRET", "CV_SECRET" }) Check(!secretMessage.Contains(secret), "Authentication errors redact " + secret);
Check(MicrosoftAuthService.NextPollingInterval(5) == 10 && MicrosoftAuthService.NextPollingInterval(10) == 15, "Each slow_down adds five seconds");
Check(MicrosoftAuthService.NextPollingInterval(int.MaxValue) == int.MaxValue, "Polling delay cannot overflow");

HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
HttpResponseMessage SuccessfulChain(HttpRequestMessage req)
{
    var url = req.RequestUri!.AbsoluteUri;
    if (url.Contains("/oauth2/v2.0/token")) return Json(HttpStatusCode.OK, "{\"access_token\":\"MSA_SECRET_PREFIX12345\"}");
    if (url.Contains("user.auth.xboxlive.com")) return Json(HttpStatusCode.OK, "{\"Token\":\"xbox-token\"}");
    if (url.Contains("xsts.auth.xboxlive.com")) return Json(HttpStatusCode.OK, "{\"Token\":\"xsts-token\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"hash\"}]}}");
    if (url.Contains("login_with_xbox")) return Json(HttpStatusCode.OK, "{\"access_token\":\"minecraft-token\",\"expires_in\":3600}");
    return Json(HttpStatusCode.OK, "{\"id\":\"0123456789abcdef0123456789abcdef\",\"name\":\"Player\"}");
}
foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests, HttpStatusCode.Unauthorized })
{
    using var http = new HttpClient(new Handler(req => Json(status, "{}")));
    var service = new MicrosoftAuthService(http);
    Check(await service.RefreshAsync("old-refresh") == null, "Rejected refresh returns no account");
    Check(service.LastRefreshFailureReason == (status == HttpStatusCode.Unauthorized ? RefreshFailureReason.TokenInvalid : RefreshFailureReason.ServiceUnavailable), "Refresh classifies " + status);
}
foreach (var host in new[] { "user.auth.xboxlive.com", "xsts.auth.xboxlive.com", "api.minecraftservices.com" })
{
    using var http = new HttpClient(new Handler(req => req.RequestUri!.Host == host ? Json(HttpStatusCode.ServiceUnavailable, "{}") : SuccessfulChain(req)));
    var service = new MicrosoftAuthService(http);
    Check(await service.RefreshAsync("old-refresh") == null && service.LastRefreshFailureReason == RefreshFailureReason.ServiceUnavailable, "Later chain outage classified at " + host);
}
using (var http = new HttpClient(new Handler(SuccessfulChain)))
{
    var account = await new MicrosoftAuthService(http).RefreshAsync("old-refresh");
    Check(account?.MsRefreshToken == "old-refresh", "Omitted rotated refresh token retains old token");
    Check(account?.LastVerifiedAtUtc is not null && DateTime.UtcNow - account.LastVerifiedAtUtc < TimeSpan.FromSeconds(5), "Only successful full chain stamps verification");
}
using (var http = new HttpClient(new Handler(req => req.RequestUri!.Host == "user.auth.xboxlive.com" ? Json(HttpStatusCode.BadRequest, "{}") : SuccessfulChain(req))))
{
    try { await new MicrosoftAuthService(http).LoginWithAuthorizationCodeAsync("code", "verifier"); throw new Exception("Expected Xbox failure"); }
    catch (AuthStepException ex) { Check(!ex.Message.Contains("MSA_SECRET") && !ex.Message.Contains("访问令牌前"), "Xbox diagnostic never reveals token prefix"); }
}
using (var http = new HttpClient(new Handler(_ => throw new OperationCanceledException())))
using (var stop = new CancellationTokenSource())
{
    stop.Cancel();
    try { await new MicrosoftAuthService(http).RefreshAsync("old", stop.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { checks++; }
    try { await new AuthServerAuthService(http).DetectApiRootAsync("https://skin.example/custom", stop.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { checks++; }
}
using (var http = new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, "{\"meta\":{}}"))))
    Check(await new AuthServerAuthService(http).DetectApiRootAsync("https://skin.example/custom") == "https://skin.example/custom", "Actual detection preserves custom root");

foreach (var badEndpoint in new[] { "user.auth.xboxlive.com", "xsts.auth.xboxlive.com", "api.minecraftservices.com" })
{
    using var http = new HttpClient(new Handler(req => req.RequestUri!.Host == badEndpoint ? Json(HttpStatusCode.OK, "{}") : SuccessfulChain(req)));
    var service = new MicrosoftAuthService(http);
    Check(await service.RefreshAsync("old-refresh") == null && service.LastRefreshFailureReason == RefreshFailureReason.ServiceUnavailable, "Malformed successful response classified at " + badEndpoint);
}
var verified = DateTime.UtcNow.AddDays(-2);
var existing = new Account { Type = AccountType.Microsoft, AvatarPhotoPath = "avatar.png", CustomSkinPath = "skin.png", SkinType = OfflineSkinType.Custom, CustomSkinSlim = true, IsSelected = false, LastVerifiedAtUtc = verified };
var refreshed = new Account { Type = AccountType.Microsoft, IsSelected = true, LastVerifiedAtUtc = DateTime.UtcNow };
AccountUpdatePolicy.PreserveLocalPreferences(refreshed, existing);
Check(refreshed.AvatarPhotoPath == "avatar.png" && refreshed.CustomSkinPath == "skin.png" && refreshed.CustomSkinSlim && !refreshed.IsSelected, "Credential refresh retains local avatar/skin/selection");
existing.AvatarPhotoPath = null;
AccountUpdatePolicy.PreserveLocalPreferences(existing, existing);
Check(existing.AvatarPhotoPath == null && existing.LastVerifiedAtUtc == verified, "Local edits stay editable without stamping online verification");

foreach (var skin in Enum.GetValues<OfflineSkinType>())
    Check(!AccountAvatarCacheService.ShouldFetch(new Account { Type = AccountType.Offline, SkinType = skin, Username = "dream" }), "Offline skin never overwritten by same-name official account: " + skin);
Check(AccountAvatarCacheService.ShouldFetch(new Account { Type = AccountType.Microsoft }), "Official account still fetches");

var pixels = new byte[64 * 64 * 4];
var output = SkinAvatarPixels.Render(pixels, 64, 64, 8, true);
Check(Enumerable.Range(0, 64).All(i => output[i * 4 + 3] == 255 && output[i * 4] == 0), "Transparent blank skin renders opaque black");
var face = (8 * 64 + 8) * 4; pixels[face + 2] = 255;
var hat = (8 * 64 + 40) * 4; pixels[hat] = 255; pixels[hat + 3] = 128;
output = SkinAvatarPixels.Render(pixels, 64, 64, 8, true);
Check(output[0] == 128 && output[2] == 127 && output[3] == 255, "Half-alpha hat blends over opaque face");
var legacy = pixels[..(64 * 32 * 4)];
output = SkinAvatarPixels.Render(legacy, 64, 32, 8, true);
Check(output[0] == 128 && output[2] == 127, "Legacy 64x32 retains transparent hat layer");
for (var y = 0; y < 32; y++) for (var x = 32; x < 64; x++) legacy[(y * 64 + x) * 4 + 3] = 255;
output = SkinAvatarPixels.Render(legacy, 64, 32, 8, true);
Check(output[0] == 0 && output[2] == 255, "Legacy unused fully opaque hat area ignored");
try { SkinAvatarPixels.Render(new byte[16], 2, 2, 8, true); throw new Exception("Expected invalid skin"); } catch (ArgumentException) { checks++; }
try { SkinAvatarPixels.Render(pixels, 64, 64, 100000, true); throw new Exception("Expected output limit"); } catch (ArgumentOutOfRangeException) { checks++; }
Console.WriteLine($"AccountSecurityRegression: {checks} checks passed.");

sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(handler(request)); }
}
namespace XCL2.App { internal static class App { internal static string DataDir => Path.GetTempPath(); } }
