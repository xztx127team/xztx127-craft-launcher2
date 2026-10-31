using System.Net;
using System.Net.Http;
using System.Security.Authentication;

namespace XCL2.App.Services;

/// <summary>
/// Shared transport policy for launcher downloads. Compatibility changes apply to the next
/// request, even for long-lived services. TLS is provided by the operating system: this is
/// not a replacement for Schannel, its certificate store, or a supported .NET runtime.
/// </summary>
public static class LauncherHttpClientFactory
{
    public static HttpClient Create(TimeSpan? timeout = null, bool allowAutoRedirect = true,
        DecompressionMethods decompression = DecompressionMethods.None, bool useCookies = true)
        => new(new CompatibilityHandler(() => WindowsCompatibilityService.Current,
            tls12 => CreateTransport(tls12, allowAutoRedirect, decompression, useCookies)))
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(100)
        };

    internal static HttpClientHandler CreateTransport(bool tls12, bool allowAutoRedirect,
        DecompressionMethods decompression, bool useCookies) => new()
    {
        AllowAutoRedirect = allowAutoRedirect,
        AutomaticDecompression = decompression,
        UseCookies = useCookies,
        // Windows 7 commonly leaves TLS 1.2 disabled by default. Select it explicitly in
        // the handler that .NET 8 actually uses; ServicePointManager does not configure it.
        // Modern systems retain OS negotiation, including TLS 1.3 where supported.
        SslProtocols = tls12 ? SslProtocols.Tls12 : SslProtocols.None
        // Do not supply a certificate-validation callback or weaken certificate checks.
    };

    internal sealed class CompatibilityHandler : HttpMessageHandler
    {
        private readonly Func<WindowsCompatibilityProfile> _profile;
        private readonly Lazy<HttpMessageInvoker> _system;
        private readonly Lazy<HttpMessageInvoker> _tls12;

        internal CompatibilityHandler(Func<WindowsCompatibilityProfile> profile,
            Func<bool, HttpMessageHandler> transport)
        {
            _profile = profile;
            _system = new(() => new HttpMessageInvoker(transport(false), disposeHandler: true));
            _tls12 = new(() => new HttpMessageInvoker(transport(true), disposeHandler: true));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var profile = _profile();
            if (profile.IsEnabled)
            {
                request.Version = HttpVersion.Version11;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                request.Headers.ExpectContinue = false;
            }
            var tls12 = profile.IsEnabled && profile.IsLegacyWindows;
            if (tls12 && profile.OsVersion < new Version(6, 1) && request.RequestUri?.Scheme == Uri.UriSchemeHttps)
                throw new HttpRequestException("此系统的 Schannel 不支持安全下载所需的 TLS 1.2。兼容模式无法补装 TLS 或 .NET 运行时，请使用受支持的 Windows 系统。",
                    new AuthenticationException("The operating system has no supported TLS 1.2 provider."));
            try
            {
                return await (tls12 ? _tls12 : _system).Value.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (tls12 && IsTlsFailure(ex))
            {
                throw new HttpRequestException(LegacyTlsMessage, ex, ex.StatusCode);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_system.IsValueCreated) _system.Value.Dispose();
                if (_tls12.IsValueCreated) _tls12.Value.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal const string LegacyTlsMessage = "无法建立安全连接：兼容模式已使用 HTTP/1.1 和 TLS 1.2。请检查系统时间、可信根证书及 Windows 7 SP1 的 TLS/系统更新；若服务器要求系统不支持的密码套件，需升级系统。未关闭证书校验。";

    internal static bool IsTlsFailure(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current is AuthenticationException ||
                current is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }) return true;
        return false;
    }
}
