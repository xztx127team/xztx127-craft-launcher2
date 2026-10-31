using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace XCL2.App.Services;

public sealed record MiniWorldCdkDownload(string Url, string FileName, string Sha256, IReadOnlyList<string> Mirrors);

/// <summary>只对用户选择的发布者接口兑换；不缓存 CDK，不自动重试一次性兑换，不跟随提交重定向。</summary>
public sealed class MiniWorldCdkService
{
    private const int MaxResponseBytes = 64 * 1024;
    private readonly HttpClient _http;
    public static MiniWorldCdkService Shared { get; } = new(new HttpClient(
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) });
    public MiniWorldCdkService(HttpClient http) => _http = http;

    public async Task<MiniWorldCdkDownload> RedeemAsync(string endpoint, string code, string versionId,
        string fallbackFileName, CancellationToken cancellationToken = default)
    {
        if (!MiniWorldVersionService.IsWebAddress(endpoint)) throw new InvalidDataException("兑换接口必须为有效的 HTTPS 地址。");
        code = code.Trim();
        if (code.Length is < 1 or > 512 || code.Any(char.IsControl)) throw new InvalidDataException("请输入有效 CDK（最多 512 个字符）。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { cdk = code, versionId }), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("兑换接口返回重定向，请使用发布者提供的最终接口；未将 CDK 转发到新地址。");
        if (!response.IsSuccessStatusCode) throw new InvalidDataException($"CDK 兑换失败（HTTP {(int)response.StatusCode}）。请核实兑换码、有效期与接口权限。");
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException("兑换响应过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (output.Length + count > MaxResponseBytes) throw new InvalidDataException("兑换响应过大。");
            output.Write(buffer, 0, count);
        }
        return ParseResponse(Encoding.UTF8.GetString(output.ToArray()), fallbackFileName);
    }

    public static MiniWorldCdkDownload ParseResponse(string json, string fallbackFileName)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxResponseBytes) throw new InvalidDataException("兑换响应过大。");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
        var data = document.RootElement;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var nested)) data = nested;
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("兑换接口需返回包含 url 与 fileName 的 JSON 对象。");
        string Field(string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
        var url = Field("url"); if (url.Length == 0) url = Field("downloadUrl");
        var fileName = Field("fileName"); if (fileName.Length == 0) fileName = fallbackFileName.Trim();
        if (fileName.Length == 0 && MiniWorldVersionService.IsWebAddress(url)) fileName = Path.GetFileName(new Uri(url).LocalPath);
        if (!MiniWorldVersionService.IsWebAddress(url) || !MiniWorldVersionService.IsSupportedFileName(fileName))
            throw new InvalidDataException("兑换结果不是有效的 HTTPS 文件直链，或保存文件名不受支持。");
        var sha256 = Field("sha256");
        if (sha256.Length != 0 && (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))) throw new InvalidDataException("兑换响应的 SHA-256 格式错误。");
        var mirrors = new List<string>();
        if (data.TryGetProperty("mirrors", out var values))
        {
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 16) throw new InvalidDataException("兑换响应的镜像列表无效。");
            foreach (var item in values.EnumerateArray())
            {
                var mirror = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (!MiniWorldVersionService.IsWebAddress(mirror)) throw new InvalidDataException("镜像必须为有效 HTTPS 地址。");
                mirrors.Add(mirror!);
            }
        }
        return new(url, fileName, sha256, mirrors);
    }
}
