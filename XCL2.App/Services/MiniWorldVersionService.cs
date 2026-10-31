using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XCL2.App.Services;

public sealed record MiniWorldShare(string Url, string ExtractionCode);
public sealed record MiniWorldSourceProbe(bool Available, string Message);
public sealed record MiniWorldVersion(string Id, string SourceId, string Version, string Name, string Publisher,
    string SourcePageUrl, string DownloadUrl, string FileName, string Description, string? PublishedDate,
    IReadOnlyList<MiniWorldShare> PublicShares)
{
    public IReadOnlyList<string> Mirrors { get; init; } = Array.Empty<string>();
    public string Sha256 { get; init; } = "";
    public string CdkRedeemUrl { get; init; } = "";
    // User-supplied/redeemed links may be single-use. Validate the actual download, not a preliminary GET.
    public bool SkipDownloadProbe { get; init; }
    public string DateLabel => string.IsNullOrWhiteSpace(PublishedDate) ? "发布日期：来源未提供" : "发布日期：" + PublishedDate;
    public string ClientKey => MiniWorldCatalog.ClientKey(SourceId, Id);
    public string DownloadDirectoryKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ClientKey)))[..20].ToLowerInvariant();
    public string QueueName => "迷你世界 · " + Name + " · " + Version + " [" + DownloadDirectoryKey[..8] + "]";
    public override string ToString() => Name + "  ·  " + Version;
}

/// <summary>只读取发布者公开目录；版本号、社区修订号和日期分别保留，不推断未知发布日期。</summary>
public static class MiniWorldVersionService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private const int MaxIndexBytes = 2 * 1024 * 1024;

    public static async Task<IReadOnlyList<MiniWorldVersion>> LoadAsync(MiniWorldSource source, CancellationToken ct = default)
    {
        if (source.VersionIndexUrl == null)
            return source.DirectDownloadUrl == null || source.DownloadFileName == null ? Array.Empty<MiniWorldVersion>() : new[]
            {
                new MiniWorldVersion(source.Id == "cbl-launcher" ? "1.1.3" : "latest", source.Id,
                    source.Id == "cbl-launcher" ? "1.1.3" : "最新（来源未标版本号）", source.Name,
                    source.Id == "cbl-launcher" ? "CBL / 松纸工作室" : source.Kind, source.HomeUrl,
                    source.DirectDownloadUrl, source.DownloadFileName, source.Description,
                    source.Id == "cbl-launcher" ? "2026-07-20" : null, Array.Empty<MiniWorldShare>())
            };
        using var response = await Http.GetAsync(source.VersionIndexUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxIndexBytes) throw new InvalidDataException("来源版本目录过大，无法读取。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var bytes = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + count > MaxIndexBytes) throw new InvalidDataException("来源版本目录过大，无法读取。");
            buffer.Write(bytes, 0, count);
        }
        return ParseIndex(Encoding.UTF8.GetString(buffer.ToArray()), source);
    }

    /// <summary>兼容 CBL 目录和开放 JSON 目录；镜像只用于同一版本同一文件。</summary>
    public static IReadOnlyList<MiniWorldVersion> ParseIndex(string json, MiniWorldSource source)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxIndexBytes) throw new InvalidDataException("版本目录不得超过 2 MB。");
        using var document = JsonDocument.Parse(json.TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 20 });
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("versions", out var versions)) root = versions;
        if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException("目录应为版本数组或包含 versions 数组的对象。");
        if (root.EnumerateArray().Any(entry => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("游戏ID", out _)))
            return ParseCblIndex(root.GetRawText(), source);
        if (root.GetArrayLength() > 500) throw new InvalidDataException("每个来源最多导入 500 个版本。");
        var result = new List<MiniWorldVersion>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException("版本条目必须是对象。");
            string Field(string key) => entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
            var id = Field("id"); var name = Field("name"); var version = Field("version"); var url = Field("url");
            var fileName = Field("fileName"); var page = Field("sourcePage"); var hash = Field("sha256");
            var cdk = Field("cdkRedeemUrl");
            if (cdk.Length > 0 && !IsWebAddress(cdk)) throw new InvalidDataException("cdkRedeemUrl 必须为 HTTPS 兑换接口。");
            if (page.Length == 0) page = source.HomeUrl;
            if (id.Length is 0 or > 160 || name.Length is 0 or > 100 || version.Length is 0 or > 100 || !seen.Add(id) ||
                (!IsWebAddress(url) && !(url.Length == 0 && cdk.Length > 0)) || !IsWebAddress(page)) throw new InvalidDataException("版本的 id、name、version 或 https 来源地址无效，或 id 重复。");
            if (fileName.Length == 0 && IsWebAddress(url)) fileName = Path.GetFileName(new Uri(url).LocalPath);
            if (!IsSupportedFileName(fileName)) throw new InvalidDataException("版本文件名必须为有效的 EXE / ZIP / 7Z 文件名。");
            if (hash.Length != 0 && (hash.Length != 64 || !hash.All(Uri.IsHexDigit))) throw new InvalidDataException("sha256 应为 64 位十六进制值，或留空。");
            var mirrors = new List<string>();
            if (entry.TryGetProperty("mirrors", out var mirrorList))
            {
                if (mirrorList.ValueKind != JsonValueKind.Array || mirrorList.GetArrayLength() > 16) throw new InvalidDataException("mirrors 应为至多 16 个 https 地址组成的数组。");
                foreach (var mirror in mirrorList.EnumerateArray())
                {
                    var value = mirror.ValueKind == JsonValueKind.String ? mirror.GetString()?.Trim() : null;
                    if (!IsWebAddress(value)) throw new InvalidDataException("镜像地址必须是 https 文件直链。");
                    if (value != url && !mirrors.Contains(value!, StringComparer.Ordinal)) mirrors.Add(value!);
                }
            }
            var date = Field("publishedDate");
            if (date.Length > 0 && !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new InvalidDataException("publishedDate 应为 yyyy-MM-dd，或留空。");
            var description = Field("description");
            result.Add(new MiniWorldVersion(id, source.Id, version, name, Field("publisher") is { Length: > 0 } publisher ? publisher : source.Name,
                page, url, fileName, description[..Math.Min(description.Length, 12000)], date.Length > 0 ? date : null, Array.Empty<MiniWorldShare>())
                { Mirrors = mirrors, Sha256 = hash, CdkRedeemUrl = cdk });
        }
        return result;
    }

    public static IReadOnlyList<MiniWorldVersion> ParseCblIndex(string json, MiniWorldSource source)
    {
        using var document = JsonDocument.Parse(json.TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 20 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("来源版本目录格式已改变，请打开来源页查看。");
        var result = new List<MiniWorldVersion>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.EnumerateArray().Take(500))
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            string Field(string key) => entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
            var id = Field("游戏ID"); var name = Field("游戏名称"); var version = Field("版本号"); var url = Field("游戏包体");
            if (id.Length == 0 || name.Length == 0 || version.Length == 0 || !IsWebAddress(url) || !seen.Add(id)) continue;
            var revision = Field("修改版本号");
            if (source.Id == "cbl-community" && revision.Length > 0 && revision != "0") version += " · 社区修订 " + revision;
            var description = Field("简介");
            if (entry.TryGetProperty("详细介绍", out var details) && details.ValueKind == JsonValueKind.Array)
                description += "\n\n" + string.Join("\n\n", details.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).Take(12));
            var related = Field("相关链接");
            var date = Field("发布日期");
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) date = "";
            var fileName = IsSupportedFileName(Path.GetFileName(new Uri(url).LocalPath)) ? Path.GetFileName(new Uri(url).LocalPath) : "";
            var shares = ParsePublicShares(Field("游戏包体2"));
            var publisher = source.Id == "cbl-history" ? "CBL 社区历史目录 / 松纸工作室"
                : description.Contains("星辉阁", StringComparison.Ordinal) ? "星辉阁工作室（CBL 公开目录收录）"
                : name.Contains("松纸", StringComparison.Ordinal) ? "松纸工作室（CBL 公开目录收录）" : "CBL 社区目录（维护者见来源页）";
            result.Add(new MiniWorldVersion(id, source.Id, version, name, publisher,
                IsWebAddress(related) ? related : source.HomeUrl, url, fileName,
                description[..Math.Min(description.Length, 12000)], date.Length > 0 ? date : null, shares));
        }
        return result;
    }

    public static IReadOnlyList<MiniWorldShare> ParsePublicShares(string value)
    {
        var result = new List<MiniWorldShare>();
        foreach (var item in value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(12))
        {
            var parts = item.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0 && IsWebAddress(parts[0])) result.Add(new MiniWorldShare(parts[0], parts.Length > 1 ? parts[1] : ""));
        }
        return result;
    }

    public static async Task<MiniWorldSourceProbe> ProbeAsync(MiniWorldVersion version, CancellationToken ct = default)
    {
        if (version.DownloadUrl.Length == 0 && version.CdkRedeemUrl.Length > 0) return new(false, "此版本需要发布者的有效 CDK，请点击 CDK / 直链下载。");
        if (version.FileName.Length == 0) return new(false, "主来源为网盘分享页，需要在网页完成下载操作。");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, version.DownloadUrl);
            request.Headers.Range = new RangeHeaderValue(0, 511);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return new(false, $"主链接暂不可用（HTTP {(int)response.StatusCode}）。可尝试公开备用分享页。");
            var media = response.Content.Headers.ContentType?.MediaType ?? "";
            if (media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || media.Contains("json", StringComparison.OrdinalIgnoreCase) || media.Contains("xml", StringComparison.OrdinalIgnoreCase)) return new(false, "主链接返回了网页或人机验证，需打开来源页完成下载。");
            await using var stream = await response.Content.ReadAsStreamAsync(ct); var head = new byte[512];
            var count = await stream.ReadAtLeastAsync(head, 8, throwOnEndOfStream: false, cancellationToken: ct);
            if (count < 2 || !HasExpectedPrefix(version.FileName, head.AsSpan(0, count))) return new(false, "主链接未返回预期文件格式，请打开来源页或备用分享页。");
            return new(true, "主链接已返回预期文件头；下载后仍会检查完整传输与文件格式。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "检查来源超时，可重试或打开来源页。"); }
        catch (HttpRequestException ex) { return new(false, "暂时无法连接主来源：" + ex.Message); }
    }

    private static bool HasExpectedPrefix(string path, ReadOnlySpan<byte> prefix)
    {
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".002")) return MiniWorldPublicShareResolver.IsBinaryContinuation(prefix);
        if (lower.EndsWith(".exe")) return prefix.Length >= 2 && prefix[0] == 0x4d && prefix[1] == 0x5a;
        if (lower.EndsWith(".7z") || lower.EndsWith(".7z.001")) return prefix.Length >= 6 && prefix[..6].SequenceEqual(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c });
        if (lower.EndsWith(".zip") || lower.EndsWith(".zip.001")) return prefix.Length >= 4 && prefix[0] == 0x50 && prefix[1] == 0x4b && (prefix[2] is 0x03 or 0x05 or 0x07);
        return false;
    }

    public static bool IsWebAddress(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) && !value!.Any(char.IsControl);

    public static bool IsSupportedFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 || value.Any(char.IsControl) || value != Path.GetFileName(value) ||
            value.EndsWith(' ') || value.EndsWith('.') || value.IndexOfAny(new[] { '/', '\\', ':', '<', '>', '"', '|', '?', '*' }) >= 0) return false;
        var stem = value.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9')) return false;
        var lower = value.ToLowerInvariant();
        return lower.EndsWith(".exe") || lower.EndsWith(".zip") || lower.EndsWith(".7z") || lower.EndsWith(".7z.001") || lower.EndsWith(".7z.002") || lower.EndsWith(".zip.001") || lower.EndsWith(".zip.002");
    }

    public static void ValidateDownloadedPackage(string path, string? expectedFileName = null)
    {
        var lower = (expectedFileName ?? path).ToLowerInvariant();
        if (lower.EndsWith(".exe")) { MiniWorldCatalog.ValidateDownloadedExecutable(path); return; }
        using var stream = File.OpenRead(path); var prefix = new byte[512]; var length = stream.Read(prefix);
        if (length == 0) throw new InvalidDataException("下载文件为空，请刷新来源目录后重试。");
        // 第二分卷不具有独立文件头；只下载不执行，用户需与第一卷放在同一目录手动解压。
        if (lower.EndsWith(".002") && MiniWorldPublicShareResolver.IsBinaryContinuation(prefix.AsSpan(0, length))) return;
        if (HasExpectedPrefix(lower, prefix.AsSpan(0, length))) return;
        throw new InvalidDataException("下载内容不是预期的程序或压缩包，可能是上游分享页或失效链接；请使用该版本的备用分享页。");
    }
}
