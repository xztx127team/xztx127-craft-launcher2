using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>无网络的来源解析。只接受明确的基岩平台证据与上游公开下载资产。</summary>
public static class BedrockMarketplaceParser
{
    // 2026-10 核对 CurseForge 官方 minecraft-bedrock 页面中的 game.id。
    public const int CurseForgeBedrockGameId = 78022;
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public static string BuildSearchUrl(string query, int page)
        => BuildSearchUrl(new BedrockMarketSearchOptions { Query = query, Page = page });

    public static string BuildSearchUrl(BedrockMarketSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Page < 1 || options.Page > 10000) throw new ArgumentOutOfRangeException(nameof(options.Page));
        var query = (options.Query ?? "").Trim();
        if (query.Length > 255) throw new ArgumentException("搜索文字最多 255 个字符。", nameof(options.Query));
        if (options.Category != 0 && CategoryLabel(options.Category).Length == 0)
            throw new ArgumentOutOfRangeException(nameof(options.Category));
        if (options.Status is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(options.Status));
        var sort = options.Sort switch
        {
            BedrockMarketSort.Default => "",
            BedrockMarketSort.Newest => "createtime",
            BedrockMarketSort.RecentlyUpdated => "lastedittime",
            _ => throw new ArgumentOutOfRangeException(nameof(options.Sort))
        };
        // 2026-10 核对基岩目录自身的链接和表单：platform/category/status/sort/key/nlp。
        // 此目录没有提供基岩游戏版本筛选，不能把 Java 的 mcver 或推测的类型参数套在这里。
        var url = new StringBuilder($"https://www.mcmod.cn/modlist.html?platform=2&page={options.Page}");
        if (query.Length > 0) url.Append("&key=").Append(Uri.EscapeDataString(query));
        if (options.Category != 0) url.Append("&category=").Append(options.Category);
        if (options.Status != 0) url.Append("&status=").Append(options.Status);
        if (sort.Length > 0) url.Append("&sort=").Append(sort);
        if (options.FuzzySearch && query.Length > 0) url.Append("&nlp=ngram");
        return url.ToString();
    }

    public static string CategoryLabel(int category) => category switch
    {
        1 => "科技", 2 => "魔法", 3 => "冒险", 4 => "农业", 5 => "装饰",
        23 => "实用", 24 => "辅助", 21 => "魔改", 7 => "前置",
        _ => ""
    };

    public static BedrockMarketSearchResult ParseSearch(string html, int category = 0)
    {
        if (!Regex.IsMatch(html, "<input\\b[^>]*name=[\"']platform[\"'][^>]*value=[\"']2[\"']", Options, RegexTimeout))
            throw new InvalidDataException("MC百科没有返回基岩版目录，请稍后重试。");
        var pagination = Match(html, @"当前\s*(\d+)\s*/\s*(\d+)\s*页[，,]\s*共计\s*(\d+)\s*条");
        var blocks = Regex.Matches(html, "<div\\b[^>]*class=[\"']modlist-block[\"'][^>]*>", Options, RegexTimeout);
        var projects = new List<BedrockMarketProject>();
        var seen = new HashSet<int>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var start = blocks[i].Index;
            var end = i + 1 < blocks.Count ? blocks[i + 1].Index : html.Length;
            var block = html[start..end];
            var title = Match(block, "<p\\b[^>]*class=[\"']name[\"'][^>]*>\\s*<a\\b([^>]*)>(.*?)</a>");
            var href = Attribute(title.Groups[1].Value, "href");
            var idMatch = Match(href, @"^(?:https://www\.mcmod\.cn)?/class/(\d+)\.html$");
            if (!idMatch.Success || !int.TryParse(idMatch.Groups[1].Value, out var id) || !seen.Add(id)) continue;
            var name = Text(title.Groups[2].Value);
            if (name.Length == 0) continue;
            var english = Match(block, "<p\\b[^>]*class=[\"']ename[\"'][^>]*>(.*?)</p>");
            var summary = Match(block, "<a\\b[^>]*class=[\"']intro-content[\"'][^>]*>(.*?)</a>");
            // 只取 cover，不能误取物品小图或最后一张卡之后的站点页脚 Logo。
            var cover = Match(block, "<div\\b[^>]*class=[\"'][^\"']*\\bcover\\b[^\"']*[\"'][^>]*>(.*?)</div>");
            var image = Match(cover.Groups[1].Value, @"<img\b([^>]*)>");
            var imageUrl = ParseIconUrl(image.Groups[1].Value);
            projects.Add(new BedrockMarketProject
            {
                Id = id, Name = name, EnglishName = Text(english.Groups[1].Value),
                Summary = Text(summary.Groups[1].Value), IconUrl = imageUrl?.AbsoluteUri ?? "",
                PageUrl = $"https://www.mcmod.cn/class/{id}.html",
                CategoryLabel = CategoryLabel(category) is { Length: > 0 } label ? label : "基岩版附加内容"
            });
        }
        // 站点单页和无结果时都可能不输出分页。仍要求真实目录容器和合法卡片，
        // 不能把验证码/改版页误报为“没有内容”，也不能把小范围搜索误报为改版。
        var empty = Regex.IsMatch(Text(html), @"(没有找到|暂无符合|未找到|没有符合|共计\s*0\s*条)", Options, RegexTimeout);
        var hasDirectory = Regex.IsMatch(html, "<div\\b[^>]*class=[\"'][^\"']*\\bmodlist-list-frame\\b[^\"']*[\"']", Options, RegexTimeout);
        if ((!pagination.Success && !empty && (!hasDirectory || projects.Count == 0)) || (blocks.Count > 0 && projects.Count == 0))
            throw new InvalidDataException("MC百科目录结构发生变化，暂时无法解析，请稍后重试。");
        var total = pagination.Success ? int.Parse(pagination.Groups[3].Value) : projects.Count;
        if (total > 0 && projects.Count == 0)
            throw new InvalidDataException("MC百科目录内容不完整，请稍后重试。");
        return new BedrockMarketSearchResult
        {
            Projects = projects, Page = pagination.Success ? Math.Max(1, int.Parse(pagination.Groups[1].Value)) : 1,
            TotalPages = pagination.Success ? Math.Max(1, int.Parse(pagination.Groups[2].Value)) : 1, TotalCount = total
        };
    }

    private static Uri? ParseIconUrl(string imageAttributes)
    {
        // 兼容服务器直出图和懒加载图；data-src 有效时优先于占位 src。
        foreach (var attribute in new[] { "data-src", "data-original", "src" })
        {
            var value = Attribute(imageAttributes, attribute);
            if (value.StartsWith("/", StringComparison.Ordinal) && !value.StartsWith("//", StringComparison.Ordinal))
                value = "https://www.mcmod.cn" + value;
            var uri = NormalizeHttps(value);
            if (uri == null || (uri.Host != "i.mcmod.cn" && uri.Host != "www.mcmod.cn")) continue;
            if (!uri.AbsolutePath.StartsWith("/class/cover/", StringComparison.OrdinalIgnoreCase)) continue;
            return uri;
        }
        return null;
    }

    public static IReadOnlyList<Uri> ParseProjectSources(string html)
    {
        // 限定支持平台字段；正文提到基岩版或顶部导航链接不算证据。
        var platform = Match(html, @"<li\b[^>]*>\s*支持平台\s*[:：](.*?)</li>");
        if (!Regex.IsMatch(platform.Groups[1].Value, @"href=[""'][^""']*modlist\.html\?platform=2[""']", Options, RegexTimeout))
            throw new InvalidDataException("这个项目未明确标记支持基岩版，已停止读取下载文件。");
        var result = new List<Uri>();
        foreach (Match group in Regex.Matches(html, "<ul\\b[^>]*class=[\"'][^\"']*common-link-icon-frame[^\"']*[\"'][^>]*>(.*?)</ul>", Options, RegexTimeout))
        {
            foreach (Match anchor in Regex.Matches(group.Groups[1].Value, @"<a\b([^>]*)>", Options, RegexTimeout))
            {
                var url = DecodeSourceLink(Attribute(anchor.Groups[1].Value, "href"));
                if (url != null && !result.Contains(url)) result.Add(url);
            }
        }
        return result;
    }

    public static Uri? DecodeSourceLink(string value)
    {
        var uri = NormalizeHttps(value);
        if (uri == null) return null;
        if (uri.Host.Equals("link.mcmod.cn", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/target/", StringComparison.Ordinal))
        {
            try
            {
                var encoded = Uri.UnescapeDataString(uri.AbsolutePath[8..]);
                uri = NormalizeHttps(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            }
            catch (FormatException) { return null; }
        }
        return uri;
    }

    public static bool TryGithubRepository(Uri uri, out string owner, out string repository)
    {
        owner = repository = "";
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com") return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !Regex.IsMatch(segments[0], @"^[A-Za-z0-9_.-]+$", Options, RegexTimeout) ||
            !Regex.IsMatch(segments[1], @"^[A-Za-z0-9_.-]+$", Options, RegexTimeout)) return false;
        owner = segments[0]; repository = segments[1];
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repository = repository[..^4];
        return owner is not "." and not ".." && repository is not "" and not "." and not "..";
    }

    public static bool TryCurseForgeProject(Uri uri, out string slug)
    {
        slug = "";
        if (uri.Scheme != Uri.UriSchemeHttps || (uri.Host != "www.curseforge.com" && uri.Host != "curseforge.com")) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0] != "minecraft-bedrock" ||
            (parts[1] != "addons" && parts[1] != "texture-packs" && parts[1] != "scripts")) return false;
        slug = parts[2];
        return Regex.IsMatch(slug, @"^[A-Za-z0-9_-]+$", Options, RegexTimeout);
    }

    public static bool MatchesCurseForgeProject(JsonElement item, Uri source, string slug)
    {
        if (Long(item, "gameId") != CurseForgeBedrockGameId ||
            !String(item, "slug").Equals(slug, StringComparison.OrdinalIgnoreCase) ||
            !item.TryGetProperty("links", out var links)) return false;
        var url = NormalizeHttps(String(links, "websiteUrl"));
        return url != null && TryCurseForgeProject(url, out _) && TryCurseForgeProject(source, out _) &&
            ProjectPath(url).Equals(ProjectPath(source), StringComparison.OrdinalIgnoreCase);
    }

    public static List<BedrockMarketPublicationPage> PublicationPages(IEnumerable<Uri> sources)
    {
        var result = new List<BedrockMarketPublicationPage>();
        foreach (var source in sources)
        {
            if (!source.IsAbsoluteUri) continue;
            var kind = TryGithubRepository(source, out _, out _) ? "GitHub"
                : TryCurseForgeProject(source, out _) ? "CurseForge" : source.Host;
            var page = PublicationPageUrl(source.AbsoluteUri, kind);
            if (page.Length > 0 && !result.Any(item => item.Url == page)) result.Add(new(kind, page));
        }
        return result.OrderBy(page => page.Source == "GitHub" ? 0 : page.Source == "CurseForge" ? 1
            : page.Source is "mcpedl.com" or "www.mcpedl.com" ? 2 : 3).ToList();
    }

    /// <summary>只接受明确关联的 HTTPS 网页，不把资产、CDN 或下载动作交给浏览器。</summary>
    public static string PublicationPageUrl(string value, string source)
    {
        var uri = NormalizeHttps(value);
        if (uri == null || IsDownloadPage(uri)) return "";
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (source == "GitHub" && TryGithubRepository(uri, out _, out _) &&
            (parts.Length == 2 || parts.Length == 3 && parts[2] == "releases" ||
             parts.Length >= 5 && parts[2] == "releases" && parts[3] == "tag"))
            return uri.AbsoluteUri;
        if (source == "CurseForge" && TryCurseForgeProject(uri, out _) &&
            (parts.Length == 3 || parts.Length == 4 && parts[3] == "files" ||
             parts.Length == 5 && parts[3] == "files" && long.TryParse(parts[4], out var id) && id > 0))
            return uri.AbsoluteUri;
        // Other author sites (for example MCPEDL) come only from the explicit associated-link
        // section. Preserve their supplied URL; never synthesize a release path from a slug.
        return source == uri.Host && uri.Host != "github.com" &&
            uri.Host is not "www.curseforge.com" and not "curseforge.com" ? uri.AbsoluteUri : "";
    }

    private static bool IsDownloadPage(Uri uri)
    {
        var host = uri.Host;
        if (host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase) || host == "forgecdn.net" ||
            host is "codeload.github.com" or "api.github.com" or "api.curseforge.com" ||
            host.StartsWith("cdn.", StringComparison.OrdinalIgnoreCase) || host.StartsWith("cdn-", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("download.", StringComparison.OrdinalIgnoreCase) || host.StartsWith("downloads.", StringComparison.OrdinalIgnoreCase))
            return true;
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
        if (host == "drive.google.com" && path.Equals("/uc", StringComparison.OrdinalIgnoreCase)) return true;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".mcpack" or ".mcaddon" or ".mcworld" or ".mctemplate" or ".zip" or ".rar" or ".7z" or
            ".tar" or ".gz" or ".tgz" or ".jar" or ".exe" or ".msi" or ".msix" or ".msixbundle" or
            ".appx" or ".appxbundle" or ".apk" or ".pdf" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg")
            return true;
        if (path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part.Equals("download", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("download.php", StringComparison.OrdinalIgnoreCase)) || path.EndsWith("/dl", StringComparison.OrdinalIgnoreCase)) return true;
        return uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Any(part =>
        {
            var parameter = part.Split('=', 2);
            var key = Uri.UnescapeDataString(parameter[0]);
            return key.Equals("download", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("export", StringComparison.OrdinalIgnoreCase) && parameter.Length == 2 &&
                Uri.UnescapeDataString(parameter[1]).Equals("download", StringComparison.OrdinalIgnoreCase);
        });
    }

    public static List<BedrockMarketFile> ParseGithubReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GitHub 返回了无法识别的发布列表。");
        var files = new List<BedrockMarketFile>();
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (Bool(release, "draft") || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = String(asset, "name");
                var url = String(asset, "browser_download_url");
                if (!IsSupportedFileName(name) || !IsOfficialFileUrl(url, "GitHub")) continue;
                var digest = String(asset, "digest");
                files.Add(new BedrockMarketFile
                {
                    Name = name, Version = String(release, "tag_name") + (Bool(release, "prerelease") ? "（预览版）" : ""),
                    DownloadUrl = url, Source = "GitHub", Size = Long(asset, "size"), PublishedAt = Date(release, "published_at"),
                    ReleasePageUrl = PublicationPageUrl(String(release, "html_url"), "GitHub"),
                    Sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null
                });
            }
        }
        return files;
    }

    public static bool IsSupportedFileName(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".mcpack", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mcaddon", StringComparison.OrdinalIgnoreCase) || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOfficialFileUrl(string url, string source)
    {
        var uri = NormalizeHttps(url);
        if (uri == null) return false;
        return source switch
        {
            "GitHub" => uri.Host == "github.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase),
            "CurseForge" => uri.Host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    internal static string String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    internal static long Long(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    internal static bool Bool(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    internal static DateTimeOffset? Date(JsonElement element, string name) => DateTimeOffset.TryParse(String(element, name), out var date) ? date : null;
    internal static Uri? NormalizeHttps(string value)
    {
        value = WebUtility.HtmlDecode(value).Trim();
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort ? uri : null;
    }
    private static Match Match(string value, string pattern) => Regex.Match(value, pattern, Options, RegexTimeout);
    private static string ProjectPath(Uri uri) => string.Join('/', uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(3));
    private static string Attribute(string attributes, string name) => WebUtility.HtmlDecode(Match(attributes, @"(?:^|\s)" + Regex.Escape(name) + "\\s*=\\s*[\"']([^\"']*)[\"']").Groups[1].Value);
    private static string Text(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ", Options, RegexTimeout)), @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
}
