using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// MC百科基岩目录 → 条目明确关联的上游 → GitHub / CurseForge 官方文件 API。
/// 不借用 Java 项目，不构造 CDN 地址，不解析广告短链，不把网页当作下载文件。
/// </summary>
public sealed class BedrockMarketplaceService : IDisposable
{
    private readonly HttpClient _http;
    private readonly CurseForgeKeyService _keys;
    private readonly Dictionary<string, (DateTimeOffset Time, BedrockMarketSearchResult Result)> _searchCache = new();
    private readonly Dictionary<int, (DateTimeOffset Time, IReadOnlyList<Uri> Sources)> _projectSourceCache = new();
    private readonly Dictionary<int, Task<IReadOnlyList<Uri>>> _projectSourceRequests = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private int _disposed;
    private const int MaxResponseBytes = 8 * 1024 * 1024;

    public BedrockMarketplaceService(CurseForgeKeyService? keyService = null)
    {
        _keys = keyService ?? new CurseForgeKeyService();
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("XCL2-Launcher/1.0 (+https://github.com/xztx127-craft/xcl2)");
    }

    public async Task<BedrockMarketSearchResult> SearchAsync(string query, int page = 1, CancellationToken ct = default)
        => await SearchAsync(new BedrockMarketSearchOptions { Query = query, Page = page }, ct);

    public async Task<BedrockMarketSearchResult> SearchAsync(BedrockMarketSearchOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var url = BedrockMarketplaceParser.BuildSearchUrl(options);
        lock (_searchCache)
        {
            if (_searchCache.TryGetValue(url, out var cached) && DateTimeOffset.UtcNow - cached.Time < TimeSpan.FromMinutes(2)) return cached.Result;
        }
        var html = await GetTextAsync(new Uri(url), null, ct);
        var result = BedrockMarketplaceParser.ParseSearch(html, options.Category);
        lock (_searchCache)
        {
            if (_searchCache.Count >= 32) _searchCache.Clear();
            _searchCache[url] = (DateTimeOffset.UtcNow, result);
        }
        return result;
    }

    public async Task<List<BedrockMarketPublicationPage>> GetPublicationPagesAsync(BedrockMarketProject project,
        CancellationToken ct = default)
        => BedrockMarketplaceParser.PublicationPages(await GetProjectSourcesAsync(project, ct));

    private async Task<IReadOnlyList<Uri>> GetProjectSourcesAsync(BedrockMarketProject project, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (project.Id <= 0) throw new ArgumentException("无效的 MC百科项目。", nameof(project));
        Task<IReadOnlyList<Uri>> request;
        lock (_projectSourceCache)
        {
            if (_projectSourceCache.TryGetValue(project.Id, out var cached) &&
                DateTimeOffset.UtcNow - cached.Time < TimeSpan.FromMinutes(2)) return cached.Sources;
            if (!_projectSourceRequests.TryGetValue(project.Id, out request!))
            {
                request = ReadProjectSourcesAsync(project.Id, _lifetimeCancellation.Token);
                _projectSourceRequests[project.Id] = request;
                _ = request.ContinueWith(completed =>
                {
                    _ = completed.Exception; // All callers may have canceled their own wait.
                    lock (_projectSourceCache) _projectSourceRequests.Remove(project.Id);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        // A card action and the detail pane share one lookup; canceling one wait does not cancel the other.
        return await request.WaitAsync(ct);
    }

    private async Task<IReadOnlyList<Uri>> ReadProjectSourcesAsync(int projectId, CancellationToken ct)
    {
        // 由数字 ID 构造地址，绝不对模型里的任意 PageUrl 发请求。
        var html = await GetTextAsync(new Uri($"https://www.mcmod.cn/class/{projectId}.html"), null, ct);
        var sources = BedrockMarketplaceParser.ParseProjectSources(html);
        ct.ThrowIfCancellationRequested();
        lock (_projectSourceCache) _projectSourceCache[projectId] = (DateTimeOffset.UtcNow, sources);
        return sources;
    }

    public async Task<BedrockMarketFileResult> GetFilesAsync(BedrockMarketProject project, CancellationToken ct = default)
    {
        var sources = await GetProjectSourcesAsync(project, ct);
        var files = new List<BedrockMarketFile>();
        var notices = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (BedrockMarketplaceParser.TryGithubRepository(source, out var owner, out var repository))
            {
                if (!seen.Add("github:" + owner + "/" + repository)) continue;
                try
                {
                    var found = await GetGithubFilesAsync(owner, repository, ct);
                    files.AddRange(found);
                    if (found.Count == 0) notices.Add("关联的 GitHub 仓库没有发布可下载的 .mcpack、.mcaddon 或内容压缩包。");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or OperationCanceledException)
                { notices.Add("GitHub 文件列表暂时不可用：" + FriendlyError(ex)); }
            }
            else if (BedrockMarketplaceParser.TryCurseForgeProject(source, out var slug))
            {
                if (!seen.Add("curseforge:" + slug)) continue;
                try
                {
                    var result = await GetCurseForgeFilesAsync(source, slug, ct);
                    files.AddRange(result.Files);
                    if (result.Notice.Length > 0) notices.Add(result.Notice);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or OperationCanceledException)
                { notices.Add("CurseForge 文件列表暂时不可用：" + FriendlyError(ex)); }
            }
            if (seen.Count >= 6)
            {
                notices.Add("已读取前 6 个官方关联项目。");
                break;
            }
        }
        if (seen.Count == 0)
            notices.Add("此条目尚未提供可通过官方 API 下载的 GitHub 或 CurseForge 基岩版项目；当前无法在启动器内下载。");
        return new BedrockMarketFileResult
        {
            Files = files.DistinctBy(f => f.DownloadUrl).OrderByDescending(f => f.PublishedAt).ToList(),
            Notice = string.Join("\n", notices.Distinct())
        };
    }

    private async Task<List<BedrockMarketFile>> GetGithubFilesAsync(string owner, string repository, CancellationToken ct)
    {
        // 近 100 次发布足以覆盖版本选择；只枚举 release assets，不把源代码 zip 当作插件。
        var json = await GetTextAsync(new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases?per_page=100"), null, ct);
        return BedrockMarketplaceParser.ParseGithubReleases(json);
    }

    private async Task<BedrockMarketFileResult> GetCurseForgeFilesAsync(Uri source, string slug, CancellationToken ct)
    {
        var key = _keys.TryGetKey();
        if (string.IsNullOrWhiteSpace(key))
            return new BedrockMarketFileResult { Notice = "这个项目的 CurseForge 下载需要在设置中配置有效的 CurseForge API Key。" };

        const string api = "https://api.curseforge.com/v1";
        using var search = JsonDocument.Parse(await GetTextAsync(new Uri($"{api}/mods/search?gameId={BedrockMarketplaceParser.CurseForgeBedrockGameId}&slug={Uri.EscapeDataString(slug)}&pageSize=50"), key, ct));
        if (!search.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("CurseForge 返回了无法识别的项目列表。");
        JsonElement? matched = null;
        foreach (var item in data.EnumerateArray())
        {
            if (!BedrockMarketplaceParser.MatchesCurseForgeProject(item, source, slug)) continue;
            matched = item; break;
        }
        if (matched is not JsonElement project)
            return new BedrockMarketFileResult { Notice = "CurseForge API 没有返回与百科链接完全对应的基岩版项目，已停止匹配下载。" };
        if ((project.TryGetProperty("allowModDistribution", out var distribution) && distribution.ValueKind == JsonValueKind.False) ||
            (project.TryGetProperty("isAvailable", out var available) && available.ValueKind == JsonValueKind.False))
            return new BedrockMarketFileResult { Notice = "该 CurseForge 项目未开放第三方分发，无法在启动器内下载。" };

        var modId = BedrockMarketplaceParser.Long(project, "id");
        if (modId <= 0) throw new InvalidDataException("CurseForge 项目编号无效。");
        using var response = JsonDocument.Parse(await GetTextAsync(new Uri($"{api}/mods/{modId}/files?pageSize=50&index=0"), key, ct));
        if (!response.RootElement.TryGetProperty("data", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("CurseForge 返回了无法识别的文件列表。");
        var files = new List<BedrockMarketFile>();
        var unavailable = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            if (BedrockMarketplaceParser.Long(entry, "gameId") != BedrockMarketplaceParser.CurseForgeBedrockGameId ||
                BedrockMarketplaceParser.Long(entry, "modId") != modId) continue;
            var name = BedrockMarketplaceParser.String(entry, "fileName");
            if (!BedrockMarketplaceParser.IsSupportedFileName(name)) continue;
            var downloadUrl = BedrockMarketplaceParser.String(entry, "downloadUrl");
            if (!BedrockMarketplaceParser.IsOfficialFileUrl(downloadUrl, "CurseForge") ||
                (entry.TryGetProperty("isAvailable", out var fileAvailable) && fileAvailable.ValueKind == JsonValueKind.False))
            { unavailable++; continue; }
            string? sha1 = null, md5 = null;
            if (entry.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Array)
                foreach (var hash in hashes.EnumerateArray())
                {
                    if (BedrockMarketplaceParser.Long(hash, "algo") == 1) sha1 = BedrockMarketplaceParser.String(hash, "value");
                    if (BedrockMarketplaceParser.Long(hash, "algo") == 2) md5 = BedrockMarketplaceParser.String(hash, "value");
                }
            var versions = entry.TryGetProperty("gameVersions", out var gameVersions) && gameVersions.ValueKind == JsonValueKind.Array
                ? string.Join(", ", gameVersions.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())) : "";
            files.Add(new BedrockMarketFile
            {
                Name = name, Version = BedrockMarketplaceParser.String(entry, "displayName"), DownloadUrl = downloadUrl,
                Source = "CurseForge", Size = BedrockMarketplaceParser.Long(entry, "fileLength"), Sha1 = sha1, Md5 = md5,
                PublishedAt = BedrockMarketplaceParser.Date(entry, "fileDate"), GameVersions = versions
            });
        }
        var notice = files.Count == 0 ? "这个 CurseForge 项目没有可在启动器内直接下载的基岩版内容文件。" : "";
        if (unavailable > 0) notice += $" {unavailable} 个文件未开放官方第三方下载地址。";
        if (response.RootElement.TryGetProperty("pagination", out var pagination) && BedrockMarketplaceParser.Long(pagination, "totalCount") > 50)
            notice += " 当前列出最近 50 个文件。";
        return new BedrockMarketFileResult { Files = files, Notice = notice.Trim() };
    }

    private async Task<string> GetTextAsync(Uri uri, string? curseForgeKey, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        ct = timeout.Token;
        var originalHost = uri.Host;
        for (var redirects = 0; redirects < 4; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (curseForgeKey != null)
            {
                if (uri.Host != "api.curseforge.com") throw new InvalidOperationException("CurseForge 密钥只能发送到官方 API。");
                request.Headers.TryAddWithoutValidation("x-api-key", curseForgeKey);
            }
            request.Headers.TryAddWithoutValidation("Accept", uri.Host.StartsWith("api.", StringComparison.Ordinal) ? "application/json" : "text/html");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                var next = location == null ? null : location.IsAbsoluteUri ? location : new Uri(uri, location);
                // 重命名 GitHub 仓库会在 api.github.com 内跳转；密钥请求不跟随任何跳转。
                if (curseForgeKey != null || next == null || next.Scheme != Uri.UriSchemeHttps || next.Host != originalHost || !next.IsDefaultPort || next.UserInfo.Length > 0)
                    throw new InvalidDataException("数据源返回了不受支持的跳转，请稍后重试。");
                uri = next;
                continue;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden)
                throw new HttpRequestException("数据源限制了本次请求，请稍后重试；CurseForge 来源也请检查 API Key。", null, response.StatusCode);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException("数据源返回内容过大。");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(chunk.AsMemory(), ct)) > 0)
            {
                if (buffer.Length + count > MaxResponseBytes) throw new InvalidDataException("数据源返回内容过大。");
                buffer.Write(chunk, 0, count);
            }
            return Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
        }
        throw new InvalidDataException("数据源跳转次数过多。");
    }

    private static string FriendlyError(Exception ex) => ex is OperationCanceledException ? "请求超时，请重试。" : ex.Message;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetimeCancellation.Cancel();
        _http.Dispose();
        _lifetimeCancellation.Dispose();
    }
}
