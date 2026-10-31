using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

public sealed record MiniWorldResolvedShare(string Url, string FileName);

/// <summary>
/// 提交维护者公开分享页的正常提取码表单。仅支持已核实的蓝奏分享来源；
/// 不登录，不执行网站脚本，不计算验证 Cookie，不处理验证码。
/// 每个分享链接对应一个文件，多分卷由清单调用方逐条解析和下载。
/// </summary>
public static class MiniWorldPublicShareResolver
{
    public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XCL2/2.2";
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(150);

    public static bool Supports(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("mengzee.lanzoul.com", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        Regex.IsMatch(uri.AbsolutePath, @"^/i[A-Za-z0-9]+$", RegexOptions.CultureInvariant, PatternTimeout);

    public static async Task<MiniWorldResolvedShare> ResolveAsync(string url, string? extractionCode, CancellationToken ct = default)
    {
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            
            AllowAutoRedirect = false,
            UseCookies = false
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        return await ResolveAsync(http, url, extractionCode, ct);
    }

    internal static async Task<MiniWorldResolvedShare> ResolveAsync(HttpClient http, string url, string? extractionCode, CancellationToken ct)
    {
        if (!Supports(url)) throw new ArgumentException("当前仅支持来源清单中的公开蓝奏单文件分享链接。", nameof(url));
        if (string.IsNullOrWhiteSpace(extractionCode) || extractionCode.Length > 128 || extractionCode.Any(char.IsControl))
            throw new InvalidOperationException("来源未提供有效提取码，请打开来源分享页查看。");
        using var get = new HttpRequestMessage(HttpMethod.Get, url);
        get.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        using var pageResponse = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
        pageResponse.EnsureSuccessStatusCode();
        var page = await ReadBoundedTextAsync(pageResponse, 256 * 1024, ct);
        var form = ParsePublicForm(page);
        using var post = new HttpRequestMessage(HttpMethod.Post, form.Endpoint);
        post.Headers.Referrer = new Uri(url);
        post.Headers.TryAddWithoutValidation("Origin", "https://mengzee.lanzoul.com");
        post.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        post.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        { ["action"] = "downprocess", ["sign"] = form.Sign, ["kd"] = form.Kd, ["p"] = extractionCode.Trim() });
        using var response = await http.SendAsync(post, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var json = await ReadBoundedTextAsync(response, 64 * 1024, ct);
        var result = ParsePublicResponse(json, url);
        // 已知分享平台会把反机器人页面以 HTTP 200 返回。先验证文件头，避免把它当压缩包排队下载。
        using var probe = new HttpRequestMessage(HttpMethod.Get, result.Url);
        probe.Headers.Range = new RangeHeaderValue(0, 511);
        probe.Headers.Referrer = new Uri(url);
        probe.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        using var probeResponse = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
        if (probeResponse.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            throw new InvalidOperationException("分享来源要求浏览器跳转或验证。请打开来源分享页完成正常下载。");
        probeResponse.EnsureSuccessStatusCode();
        var media = probeResponse.Content.Headers.ContentType?.MediaType ?? "";
        if (media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || media.Contains("json", StringComparison.OrdinalIgnoreCase) || media.Contains("xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("分享来源返回了验证网页，请打开来源分享页完成正常下载。");
        await using var stream = await probeResponse.Content.ReadAsStreamAsync(ct);
        var header = new byte[512];
        var read = 0;
        while (read < header.Length)
        {
            var count = await stream.ReadAsync(header.AsMemory(read), ct);
            if (count == 0) break;
            read += count;
        }
        var continuation = result.FileName.EndsWith(".7z.002", StringComparison.OrdinalIgnoreCase) || result.FileName.EndsWith(".zip.002", StringComparison.OrdinalIgnoreCase);
        if (!(continuation ? IsBinaryContinuation(header.AsSpan(0, read)) : IsArchiveOrExecutable(header.AsSpan(0, read))))
            throw new InvalidOperationException("分享来源返回了验证网页，尚未取得游戏文件。请打开来源分享页完成验证；启动器不会跳过验证码或网站验证。");
        return result;
    }

    internal static (string Endpoint, string Sign, string Kd) ParsePublicForm(string page)
    {
        if (string.IsNullOrEmpty(page) || page.Length > 256 * 1024) throw new InvalidDataException("分享页内容无效。");
        var signs = Regex.Matches(page, @"var\s+isngis\s*=\s*'([^']*)'", RegexOptions.CultureInvariant, PatternTimeout);
        var sign = signs.Cast<Match>().Select(match => match.Groups[1].Value).LastOrDefault(value => value.Length > 0);
        var endpoint = Regex.Match(page, @"url\s*:\s*'(https://apifile\.woozooo\.com/ajaxfile\.php\?file=[0-9]+)'", RegexOptions.CultureInvariant, PatternTimeout).Groups[1].Value;
        var kd = Regex.Match(page, @"var\s+kdns\s*=\s*([0-9]+)", RegexOptions.CultureInvariant, PatternTimeout).Groups[1].Value;
        if (string.IsNullOrEmpty(sign) || sign.Length > 4096 || string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(kd))
            throw new InvalidOperationException("分享页需要浏览器验证或格式已更改，请打开来源分享页下载。");
        return (endpoint, sign, kd);
    }

    internal static MiniWorldResolvedShare ParsePublicResponse(string json, string sourcePage)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("zt", out var state) || state.ToString() != "1")
            throw new InvalidOperationException("分享提取码未通过或来源要求验证码，请打开来源分享页确认。启动器不会自动重试验证。");
        var domain = root.TryGetProperty("dom", out var host) ? host.GetString() : null;
        var path = root.TryGetProperty("url", out var link) ? link.GetString() : null;
        var name = root.TryGetProperty("inf", out var file) ? file.GetString() : null;
        if (!Uri.TryCreate(domain, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps || !origin.IsDefaultPort ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            !(origin.Host.Equals("lanrar.com", StringComparison.OrdinalIgnoreCase) || origin.Host.EndsWith(".lanrar.com", StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrEmpty(path) || path.Length > 16384 || path.Any(char.IsControl) || path.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("分享来源返回了未经支持的下载地址，请使用来源分享页。");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name.Any(ch => char.IsControl(ch) || "<>:\"/\\|?*".Contains(ch)) || name is "." or "..")
            throw new InvalidDataException("分享来源返回的文件名无效。");
        var download = origin.GetLeftPart(UriPartial.Authority) + "/file/" + path;
        if (!Uri.TryCreate(download, UriKind.Absolute, out var resolved) || resolved.Host != origin.Host)
            throw new InvalidDataException("分享来源返回的下载地址无效。");
        return new MiniWorldResolvedShare(resolved.AbsoluteUri, name);
    }

    internal static bool IsArchiveOrExecutable(ReadOnlySpan<byte> bytes) =>
        (bytes.Length >= 6 && bytes[..6].SequenceEqual(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c })) ||
        (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4b &&
            ((bytes[2] == 3 && bytes[3] == 4) || (bytes[2] == 5 && bytes[3] == 6) || (bytes[2] == 7 && bytes[3] == 8))) ||
        (bytes.Length >= 2 && bytes[0] == 0x4d && bytes[1] == 0x5a);

    /// <summary>后续分卷没有独立魔数。排除文本/HTML/JSON验证响应后只接受二进制前缀；完整性仍由解压器验证。</summary>
    internal static bool IsBinaryContinuation(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8) return false;
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (text.StartsWith("<", StringComparison.Ordinal) || text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal)) return false;
        foreach (var value in bytes) if (value == 0 || value >= 0x80 || value is > 0 and < 0x09 or > 0x0d and < 0x20) return true;
        return false;
    }

    private static async Task<string> ReadBoundedTextAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
            throw new InvalidDataException("来源响应过大，请打开来源页下载。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (output.Length + count > maxBytes) throw new InvalidDataException("来源响应过大，请打开来源页下载。");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
