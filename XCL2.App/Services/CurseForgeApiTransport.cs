using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace XCL2.App.Services;

/// <summary>Authenticated official API only; never forwards API keys to a CDN, mirror or redirect.</summary>
internal static class CurseForgeApiTransport
{
    internal static HttpClient CreateClient() => LauncherHttpClientFactory.Create(TimeSpan.FromSeconds(45),
        allowAutoRedirect: false, decompression: DecompressionMethods.GZip | DecompressionMethods.Deflate,
        useCookies: false);

    internal static async Task<string> GetStringAsync(HttpClient client, string url, string key, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("api.curseforge.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("CurseForge API 只允许官方 HTTPS 地址。");
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
            throw new InvalidOperationException("CurseForge API Key 为空或格式无效，请在设置中重新保存。");
        wait ??= static (delay, token) => Task.Delay(delay, token);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var delay = TimeSpan.FromMilliseconds(300 * (attempt + 1));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd("XCL2-Launcher/1.0");
                request.Headers.Add("x-api-key", key.Trim());
                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new HttpRequestException("CurseForge 拒绝了 API 访问，请检查 API Key 是否有效及是否具有访问权限；这不是 TLS 版本错误。", null, response.StatusCode);
                if ((int)response.StatusCode is >= 300 and < 400)
                    throw new HttpRequestException("CurseForge API 返回了重定向，已停止以保护 API Key。请稍后重试。", null, response.StatusCode);
                if (attempt < 2 && IsRetryable(response.StatusCode))
                {
                    var requestedDelay = response.Headers.RetryAfter?.Delta ??
                        (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.Zero);
                    // Do not retry earlier than the server permits or keep the UI waiting indefinitely.
                    if (requestedDelay > TimeSpan.FromSeconds(15))
                        throw new HttpRequestException("CurseForge 暂时限流或不可用，请稍后重试。", null, response.StatusCode);
                    if (requestedDelay > delay) delay = requestedDelay;
                }
                else response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex) when (attempt < 2 && ex.StatusCode == null && !LauncherHttpClientFactory.IsTlsFailure(ex))
            {
                // Only idempotent API reads are retried, with a new request every time.
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < 2)
            {
                // HttpClient timeout, not a user cancellation.
            }
            await wait(delay, ct).ConfigureAwait(false);
        }
    }

    private static bool IsRetryable(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}
