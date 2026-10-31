using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using XCL2.App.Models;

namespace XCL2.App.Services;

public class BedrockClientDownloadService
{
    private const string IncompleteInstallationMarker = "xcl-install-incomplete.flag";
    public static bool IsClientInstallationComplete(string installDir)
        => !File.Exists(Path.Combine(installDir, IncompleteInstallationMarker)) && FindClientExe(installDir) != null;
    // ===== 新源：reversedcodes/minecraft-bedrock-meta-database（自动更新，含 1.26.x）=====
    private const string VersionDbUrl = "https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/bedrock/client/versions.json";

    // 注意：ghproxy.com / mirror.ghproxy.com 已于 2024 年停止对外服务，长期返回 404/超时，
    // 之前把它们排在候选列表里，"刷新列表"点一次要挨个等到超时才轮到后面能用的源，
    // 单次刷新经常整体超过用户耐心/上层调用的隐式超时，看起来就是"点刷新必失败"。
    // 现在换成仍然可用的加速域名，并把它们排在 jsdelivr 几个 CDN 后面、raw 前面。
    private static readonly string[] VersionDbMirrorUrls = new[]
    {
        "https://cdn.jsdelivr.net/gh/reversedcodes/minecraft-bedrock-meta-database@main/bedrock/client/versions.json",
        "https://fastly.jsdelivr.net/gh/reversedcodes/minecraft-bedrock-meta-database@main/bedrock/client/versions.json",
        "https://gcore.jsdelivr.net/gh/reversedcodes/minecraft-bedrock-meta-database@main/bedrock/client/versions.json",
        "https://ghfast.top/https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/bedrock/client/versions.json",
        "https://gh-proxy.com/https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/bedrock/client/versions.json",
        "https://ghproxy.net/https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/bedrock/client/versions.json",
    };

    // ===== 老源：mc-w10-versiondb（已停更，最高 1.21.x）=====
    private const string LegacyVersionDbUrl = "https://raw.githubusercontent.com/MCMrARM/mc-w10-versiondb/master/versions.json.min";

    private static readonly string[] LegacyVersionDbMirrorUrls = new[]
    {
        "https://cdn.jsdelivr.net/gh/MCMrARM/mc-w10-versiondb@master/versions.json.min",
        "https://fastly.jsdelivr.net/gh/MCMrARM/mc-w10-versiondb@master/versions.json.min",
        "https://gcore.jsdelivr.net/gh/MCMrARM/mc-w10-versiondb@master/versions.json.min",
        "https://ghfast.top/https://raw.githubusercontent.com/MCMrARM/mc-w10-versiondb/master/versions.json.min",
        "https://gh-proxy.com/https://raw.githubusercontent.com/MCMrARM/mc-w10-versiondb/master/versions.json.min",
        "https://ghproxy.net/https://raw.githubusercontent.com/MCMrARM/mc-w10-versiondb/master/versions.json.min",
    };

    private static string CachedVersionDbPath => Path.Combine(App.DataDir, "bedrock_versiondb_cache.json");

    public enum BedrockClientChannel { Stable, Preview }

    public sealed record BedrockVersionInfo(
        string Uuid,
        string Name,
        string? BaseVersion,
        DateTime? Date,
        BedrockClientChannel Channel,
        string? Url = null,
        bool DirectUrlAvailable = false)
    {
        public bool CanResolveDownload => DirectUrlAvailable || Guid.TryParse(Uuid, out _) ||
            Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
        public string DisplayName => CanResolveDownload ? Name : Name + "（下载索引待同步）";
    }

    public string VersionListSource { get; private set; } = "本地版本索引";
    private string _networkVersionListSource = "在线版本索引";

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XCL2-Launcher/1.0");
        return http;
    }

    // ==================== 版本列表获取 ====================

    /// <summary>
    /// 获取基岩版历史版本列表。
    /// 完成本次网络刷新后返回列表；失败时回到缓存／内置索引。调用方可先展示本地列表。
    /// </summary>
    public async Task<List<BedrockVersionInfo>> GetVersionListAsync(
        BedrockClientChannel channel = BedrockClientChannel.Stable,
        CancellationToken ct = default)
        => await RefreshVersionListAsync(channel, ct);

    /// <summary>
    /// 强制从网络刷新版本列表（用户点"刷新列表"时调用）。
    /// </summary>
    public async Task<List<BedrockVersionInfo>> RefreshVersionListAsync(
        BedrockClientChannel channel = BedrockClientChannel.Stable,
        CancellationToken ct = default)
    {
        try
        {
            var fresh = await FetchVersionListFromNetworkAsync(ct);
            if (fresh.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var selected = FilterAndSort(fresh, channel);
                if (selected.Count == 0) return GetLocalFallbackVersionList(channel);
                SaveCachedVersions(fresh);
                VersionListSource = _networkVersionListSource;
                return selected;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 任何网络/JSON/镜像异常都不能让版本下拉框变空：下面无条件走本地兜底。
        }

        return GetLocalFallbackVersionList(channel);
    }

    /// <summary>
    /// 完全不访问网络的本地版本列表兜底。优先使用上次成功获取后写入的数据缓存；
    /// 缓存不存在/损坏时使用程序内置版本表。UI 的自动加载和“刷新列表”失败路径都可以
    /// 调该方法，保证断网、GitHub/CDN 不可用时仍然能选择一个本地已知版本。
    /// </summary>
    public List<BedrockVersionInfo> GetLocalFallbackVersionList(
        BedrockClientChannel channel = BedrockClientChannel.Stable)
    {
        var fallback = LoadCachedVersions();
        VersionListSource = "本地缓存（离线）";
        if (!fallback.Any(version => version.Channel == channel))
        {
            fallback = GetBuiltinVersions();
            VersionListSource = "内置版本索引（离线）";
        }
        return FilterAndSort(fallback, channel);
    }

    private static List<BedrockVersionInfo> FilterAndSort(List<BedrockVersionInfo> all, BedrockClientChannel channel)
        => FromCatalog(BedrockVersionCatalog.Normalize(ToCatalog(all.Where(version => version.Channel == channel))));

    private static List<BedrockVersionInfo> FromCatalog(IEnumerable<BedrockCatalogEntry> values)
        => values.Select(value => new BedrockVersionInfo(value.Uuid, value.Name, value.BaseVersion, value.Date,
            value.Preview ? BedrockClientChannel.Preview : BedrockClientChannel.Stable, value.Url, value.DirectUrlAvailable)).ToList();
    private static IEnumerable<BedrockCatalogEntry> ToCatalog(IEnumerable<BedrockVersionInfo> values)
        => values.Select(value => new BedrockCatalogEntry(value.Uuid, value.Name, value.BaseVersion, value.Date,
            value.Channel == BedrockClientChannel.Preview, value.Url, value.DirectUrlAvailable));

    // ==================== 网络获取 ====================

    private async Task<List<BedrockVersionInfo>> FetchVersionListFromNetworkAsync(CancellationToken ct)
    {
        // New and legacy indexes run concurrently. A slow legacy host must not postpone the
        // current GDK list by seven sequential mirror timeouts.
        var primaryTask = FetchJsonWithMirrorsAsync(VersionDbUrl, VersionDbMirrorUrls, ct,
            json => BedrockVersionCatalog.ParseMetadata(json).Count > 0);
        var legacyTask = FetchJsonWithMirrorsAsync(LegacyVersionDbUrl, LegacyVersionDbMirrorUrls, ct,
            json => BedrockVersionCatalog.ParseLegacy(json).Count > 0);
        await Task.WhenAll(primaryTask, legacyTask);
        ct.ThrowIfCancellationRequested();
        var primary = await primaryTask;
        var legacy = await legacyTask;
        _networkVersionListSource = primary == null ? "旧版在线索引（主索引暂不可用）"
            : legacy == null ? "在线主索引（旧版 Store 索引暂不可用）" : "在线版本索引";
        var versions = primary == null ? new List<BedrockVersionInfo>() : ParseReversedMeta(primary);
        var old = legacy == null ? new List<BedrockVersionInfo>() : ParseVersionDbMin(legacy);
        if (versions.Count == 0) return old;
        MergeLegacyVersions(versions, old);
        return FilterAndSort(versions, BedrockClientChannel.Stable)
            .Concat(FilterAndSort(versions, BedrockClientChannel.Preview)).ToList();
    }

    private static async Task<string?> FetchJsonWithMirrorsAsync(string primary, string[] mirrors,
        CancellationToken ct, Func<string, bool> valid)
    {
        ct.ThrowIfCancellationRequested();
        using var requests = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requests.CancelAfter(TimeSpan.FromSeconds(10));
        // Only the actual upstream and its established CDN hosts are used for the index race.
        // Arbitrary proxy error pages must never be accepted as a successful JSON index.
        var pending = new[] { primary }.Concat(mirrors.Take(3)).Select(async url =>
        {
            try
            {
                using var http = CreateHttp();
                var json = await http.GetStringAsync(url, requests.Token);
                return valid(json) ? json : null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException) { return null; }
        }).ToList();
        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                var json = await completed;
                if (json != null) return json;
            }
            ct.ThrowIfCancellationRequested();
            return null;
        }
        finally
        {
            requests.Cancel();
            // Observe all losing requests after cancellation; no abandoned failing tasks.
            try { await Task.WhenAll(pending); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
    }

    // ==================== 解析 ====================
    private static List<BedrockVersionInfo> ParseVersionDb(string json)
        => FromCatalog(BedrockVersionCatalog.ParseCache(json));
    private static List<BedrockVersionInfo> ParseReversedMeta(string json)
        => FromCatalog(BedrockVersionCatalog.ParseMetadata(json));
    private static List<BedrockVersionInfo> ParseVersionDbMin(string json)
        => FromCatalog(BedrockVersionCatalog.ParseLegacy(json));

    private static void MergeLegacyVersions(List<BedrockVersionInfo> target, List<BedrockVersionInfo> legacy)
    {
        var merged = BedrockVersionCatalog.MergeMetadataAndLegacy(ToCatalog(target), ToCatalog(legacy));
        target.Clear();
        target.AddRange(FromCatalog(merged));
    }

    // ==================== 内置回退列表 ====================

    /// <summary>
    /// 所有网络源失败且没有可用缓存时，读取已核对真实上游数据的索引快照。
    /// </summary>
    private static List<BedrockVersionInfo> GetBuiltinVersions()
    {
        // Snapshot of the real upstream indexes, including only their actual IDs. No guessed
        // UUID or fabricated release date is used when the network is unavailable.
        using var stream = typeof(BedrockClientDownloadService).Assembly.GetManifestResourceStream(
            "XCL2.App.Resources.Data.BedrockClientVersionsFallback.json");
        if (stream == null) return new();
        using var reader = new StreamReader(stream);
        return ParseVersionDb(reader.ReadToEnd());
    }

    // ==================== 本地缓存 ====================

    private static List<BedrockVersionInfo> LoadCachedVersions()
    {
        try
        {
            if (!File.Exists(CachedVersionDbPath)) return new List<BedrockVersionInfo>();
            var json = File.ReadAllText(CachedVersionDbPath);
            return ParseVersionDb(json);
        }
        catch { return new List<BedrockVersionInfo>(); }
    }

    private static void SaveCachedVersions(List<BedrockVersionInfo> versions)
    {
        try
        {
            Directory.CreateDirectory(App.DataDir);
            var json = BedrockVersionCatalog.SerializeCache(ToCatalog(versions));
            var temporary = CachedVersionDbPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json);
                File.Move(temporary, CachedVersionDbPath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch { /* 缓存失败不阻断 */ }
    }

    // ==================== 下载 ====================

    public async Task<string> DownloadClientAsync(BedrockVersionInfo version, string targetDir,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // 0. 已经装过同版本（解压目录里能找到可执行文件）：直接复用，避免重复下载
        var extractDir = Path.Combine(targetDir, "extracted");
        if (IsClientInstallationComplete(targetDir))
        {
            progress?.Report(new ProgressInfo($"已安装 {version.Name}，无需重复下载", 1, 1, targetDir));
            return extractDir;
        }

        // 1~3. 拿到安装包文件。多源回退只发生在"下载本身失败"（连不上/文件损坏）时；
        //      一旦某个源下载成功，就固定用它安装，绝不因为后面解压之类的问题再重新下载。
        var filePath = await ResolveAndDownloadPackageAsync(version, targetDir, progress, ct);
        ct.ThrowIfCancellationRequested();

        // 下载完成 → 立即开始安装（解压）。这一步只安装、不联网、不重新下载。
        progress?.Report(new ProgressInfo("正在解压", 0, 1, ""));
        try
        {
            // 不先删旧目录：游戏正在运行/杀软扫描时旧 extracted 目录可能被占用，
            // 删除失败以前会整条路回退到"换源重新下载"。overwriteFiles 直接覆盖同名文件，
            // 旧版本残留的额外文件不影响运行，重下才真正浪费流量和时间。
            Directory.CreateDirectory(extractDir);
            File.WriteAllText(Path.Combine(targetDir, IncompleteInstallationMarker), version.Name);

            if (BedrockDecode.GdkPackageExtractor.IsMsixvcPackage(filePath))

                await BedrockDecode.BedrockExtractWorkerProcess.RunAsync(filePath, extractDir, version.Channel, progress, ct);
            else
                ExtractAppxPackage(filePath, extractDir, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"基岩版客户端 {version.Name} 已下载完成，但解压安装失败：{ex.Message}。" +
                "安装包已保存在本地，重试时会直接复用，不会重新下载。", ex);
        }

        ct.ThrowIfCancellationRequested();
        File.Delete(Path.Combine(targetDir, IncompleteInstallationMarker));
        progress?.Report(new ProgressInfo(Loc.T("Str_Common_Finish", "完成"), 1, 1, version.Name));
        return extractDir;
    }

    private static void ExtractAppxPackage(string packagePath, string extractDir, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(packagePath);

        var failures = new List<string>();

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            // 目录条目（entry.Name 为空，只有 FullName 以 / 结尾）：appx 里目录本身通常不会
            // 单独出现，但保险起见处理一下，避免下面按文件处理时创建出一个同名"文件"。
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            // appx 内部路径分隔符固定是 '/'，Windows 上要换成 '\' 才能正确拼目录。
            var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var destPath = Path.GetFullPath(Path.Combine(extractDir, relativePath));

            // 防止 zip 条目里出现 "../" 之类的路径穿越，写到 extractDir 之外。
            if (!destPath.StartsWith(Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{entry.FullName}：非法路径（疑似路径穿越），已跳过");
                continue;
            }

            try
            {
                ExtractSingleEntry(entry, destPath, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failures.Add($"{entry.FullName}：{ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            // 逐条目解压里失败的都是极少数（多半是同一类原因反复出现），只挑前几条列出来，
            // 避免异常信息本身长到没法看。
            var shown = string.Join("；", failures.Take(5));
            var more = failures.Count > 5 ? $"，其余 {failures.Count - 5} 个条目省略" : "";
            throw new IOException($"{failures.Count} 个文件解压失败：{shown}{more}");
        }
    }

    /// <summary>解压单个 zip 条目到目标路径，用长路径前缀绕开 Windows 260 字符 MAX_PATH 限制。</summary>
    private static void ExtractSingleEntry(ZipArchiveEntry entry, string destPath, CancellationToken ct)
    {
        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            CreateDirectoryLongPath(destDir);

        var writablePath = ToLongPathIfNeeded(destPath);

        using var entryStream = entry.Open();
        using var fileStream = new FileStream(writablePath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int count;
            while ((count = entryStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                fileStream.Write(buffer, 0, count);
            }
            ct.ThrowIfCancellationRequested();
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }

        try
        {
            File.SetLastWriteTime(writablePath, entry.LastWriteTime.LocalDateTime);
        }
        catch
        {
            // 时间戳设置失败不影响文件内容本身，不需要让整个条目解压失败。
        }
    }

    /// <summary>递归创建目录，超长路径时加 "\\?\" 前缀绕开 MAX_PATH。</summary>
    private static void CreateDirectoryLongPath(string dir)
    {
        var p = ToLongPathIfNeeded(dir);
        Directory.CreateDirectory(p);
    }

    /// <summary>
    /// 路径超过安全阈值时加上 "\\?\" 长路径前缀（仅本地绝对路径有效，UNC 路径要用
    /// "\\?\UNC\" 前缀，这里的安装目录不会是网络路径，不用额外处理那种情况）。
    /// 阈值取 240 而不是刚好 260，是给文件名本身、以及 Windows API 内部可能附加的
    /// 后缀留出余量，避免"差几个字符"卡在边界上。
    /// </summary>
    private static string ToLongPathIfNeeded(string path)
    {
        if (path.Length < 240 || path.StartsWith(@"\\?\"))
            return path;

        return @"\\?\" + path;
    }

    /// <summary>
    /// 解析下载链接并下载安装包到 version_save（多源回退仅在下载失败时发生；
    /// 成功拿到完好的安装包后立即返回，不在这里做解压等安装动作）。
    /// </summary>
    private async Task<string> ResolveAndDownloadPackageAsync(BedrockVersionInfo version, string targetDir,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        // 1. 带直链的（缓存/内置列表里已有）直接下载
        if (!string.IsNullOrEmpty(version.Url))
            return await DownloadFromUrlAsync(EnumerateCdnMirrors(version.Url), version.Name, targetDir, progress, ct);

        // 2. 新版（reversedcodes 列表，1.26.x 等 GDK 通道版本）：
        //    先拉该版本的 GDK 元数据拿微软官方直链。1.26.x 起官方给的全是 .msixvc
        //    （GDK/XVD 容器，Xbox/Game Pass 云安装格式）——以前启动器没有解码器，
        //    这类链接下了也无法解压，只能跳过并弹窗报错；现在已内置 MSIXVC 解码器
        //    （Services/BedrockDecode/，BedrockBoot 同款），.msixvc 可以直接下载并解压。
        //    优先级：Microsoft Store FE3 API 换到的直链是真正的 .appx（zip 结构），
        //    优先试这条（能拿到就按 UWP 方式安装注册）；GDK 元数据里的 .msixvc 链接
        //    作为补充候选，全部 .msixvc 时也能正常安装。
        if (version.DirectUrlAvailable)
        {
            var urls = new List<string>();

            // FE3（Microsoft Store）优先：给出来的是真正可解压的 .appx。
            var fe3Url = Guid.TryParse(version.Uuid, out _) ? await ResolveDownloadUrlAsync(version.Uuid, ct) : null;
            if (!string.IsNullOrEmpty(fe3Url))
                urls.AddRange(EnumerateCdnMirrors(fe3Url));

            // GDK 元数据链接作为补充：.msixvc 由内置解码器处理，全部参与候选。
            var gdkUrls = await ResolveGdkDownloadUrlsAsync(version, ct);
            foreach (var url in gdkUrls)
                urls.AddRange(EnumerateCdnMirrors(url));

            var distinct = urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count > 0)
            {
                // 日志冗余：记下候选来源构成，方便排查"为什么走的是哪条路"
                var msixvcCount = distinct.Count(u => u.Contains(".msixvc", StringComparison.OrdinalIgnoreCase));
                LauncherLogService.AppendLine(
                    $"[Bedrock下载] 版本 {version.Name} 共解析出 {distinct.Count} 个官方直链，" +
                    $"其中 {msixvcCount} 个为 .msixvc（GDK）格式，将由内置 MSIXVC 解码器解压安装。");
                try
                {
                    return await DownloadFromUrlAsync(distinct, version.Name, targetDir, progress, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "获取该版本的微软官方下载链接失败，所有镜像源均不可用。\n" +
                        $"详细信息：{ex.Message}", ex);
                }
            }

            throw new InvalidOperationException("获取该版本的微软官方下载链接失败，所有镜像源均不可用。");
        }

        // 3. 老版本：Microsoft Store FE3 换直链
        var downloadUrl = await ResolveDownloadUrlAsync(version.Uuid, ct);
        if (!string.IsNullOrEmpty(downloadUrl))
            return await DownloadFromUrlAsync(EnumerateCdnMirrors(downloadUrl), version.Name, targetDir, progress, ct);

        throw new InvalidOperationException(
            "无法获取该版本的下载链接。基岩版 Windows 客户端由 Microsoft Store 分发，\n" +
            "请确保网络可以访问微软更新服务器，或尝试从 Microsoft Store 直接安装。");
    }

    /// <summary>
    /// 拉取 reversedcodes 单版本 GDK 元数据（bedrock/client/{release|preview}/gdk/{version}.json），
    /// 解析出微软官方直链（binaries.arch.x64.urls[]，assets1.xboxlive.com / .cn）。
    /// </summary>
    private async Task<List<string>> ResolveGdkDownloadUrlsAsync(BedrockVersionInfo version, CancellationToken ct)
    {
        foreach (var url in GdkMetaCandidateUrls(version.Channel, version.Name))
        {
            try
            {
                using var http = CreateHttp();
                http.Timeout = TimeSpan.FromSeconds(12);
                var json = await http.GetStringAsync(url, ct);
                var parsed = ParseGdkMeta(json);
                if (parsed.Count > 0) return parsed;
            }
            catch
            {
                // 试下一个镜像
            }
        }
        return new List<string>();
    }

    private static IEnumerable<string> GdkMetaCandidateUrls(BedrockClientChannel channel, string versionName)
    {
        var rel = channel == BedrockClientChannel.Preview ? "preview" : "release";
        var name = System.Text.RegularExpressions.Regex.Match(versionName, @"\d+(?:\.\d+){1,3}").Value;
        var path = $"bedrock/client/{rel}/gdk/{Uri.EscapeDataString(name)}.json";
        yield return $"https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/{path}";
        yield return $"https://cdn.jsdelivr.net/gh/reversedcodes/minecraft-bedrock-meta-database@main/{path}";
        yield return $"https://fastly.jsdelivr.net/gh/reversedcodes/minecraft-bedrock-meta-database@main/{path}";
        yield return $"https://gcore.jsdelivr.net/gh/reversedcodes/minecraft-bedrock-meta-database@main/{path}";
        yield return $"https://ghp.ci/https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/{path}";
        yield return $"https://ghproxy.com/https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/{path}";
        yield return $"https://mirror.ghproxy.com/https://raw.githubusercontent.com/reversedcodes/minecraft-bedrock-meta-database/main/{path}";
    }

    private static List<string> ParseGdkMeta(string json)
    {
        var urls = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("binaries", out var binaries)) return urls;
            if (!binaries.TryGetProperty("arch", out var arch)) return urls;
            if (!arch.TryGetProperty("x64", out var x64)) return urls;
            if (!x64.TryGetProperty("urls", out var arr) || arr.ValueKind != JsonValueKind.Array) return urls;

            foreach (var u in arr.EnumerateArray())
            {
                var s = u.GetString();
                if (string.IsNullOrWhiteSpace(s)) continue;
                // 微软 CDN 元数据给的是 http 直链。同一份文件保留两个候选：
                //   1) 升级成 https 的版本（默认优先尝试，避免明文传输被劫持）；
                //   2) 原始 http 版本（兜底：部分 CDN 边缘节点实际并未启用 TLS，
                //      强升 https 会直接 SSL 握手失败——本地上报的"所有镜像源均不可用，
                //      The SSL connection could not be established"就是这么来的。
                //      此时退回 http 原链能正常下动；下载物是微软签名安装包，
                //      安装时系统会校验签名，安全性风险可控）。
                var https = s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    ? "https://" + s["http://".Length..]
                    : s;
                urls.Add(https);
                if (!string.Equals(https, s, StringComparison.OrdinalIgnoreCase)) urls.Add(s);
            }
        }
        catch { /* 解析失败返回空 */ }
        return urls;
    }

    /// <summary>通过 Microsoft Store FE3 公开 API 换取下载直链。</summary>
    private async Task<string?> ResolveDownloadUrlAsync(string versionUuid, CancellationToken ct)
    {
        try
        {
            using var http = CreateHttp();
            var soapBody = string.Format(@"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Header>
    <h:ClientVersion xmlns:h=""http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService"">1.0</h:ClientVersion>
  </s:Header>
  <s:Body>
    <GetExtendedUpdateInfo2 xmlns=""http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService"">
      <updateIDs>
        <UpdateIdentity>
          <UpdateID>{0}</UpdateID>
          <RevisionNumber>1</RevisionNumber>
        </UpdateIdentity>
      </updateIDs>
      <infoTypes>
        <UpdateInfoType>Xml</UpdateInfoType>
      </infoTypes>
      <deviceAttributes>E:BranchThreshold=1;BranchReadinessLevel=0;CurrentBranch=rs4_release;IsWindowsInsider=0</deviceAttributes>
    </GetExtendedUpdateInfo2>
  </s:Body>
</s:Envelope>", versionUuid);

            var content = new StringContent(soapBody, Encoding.UTF8, "application/soap+xml");
            var resp = await http.PostAsync(
                "https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx",
                content, ct);

            if (!resp.IsSuccessStatusCode) return null;

            var xml = await resp.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(xml);

            var anyUrls = doc.Descendants()
                .Where(e => e.Name.LocalName == "Url")
                .Select(e => e.Value)
                .Where(v => !string.IsNullOrEmpty(v) && v.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return anyUrls.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("通过 FE3 API 获取基岩版下载链接失败", ex);
            return null;
        }
    }

    // ===== 游戏包多下载源（多 CDN 镜像回退）=====
    // 微软 CDN 同一个资源有多台域名（assets1/2、xvcf1/2、d1/d2 × .com/.cn），
    // 直连某个域名失败（地区网络/CDN 边缘抽风）时换一台域名重下，大幅提高成功率。
    // 这一套直接移植自 BedrockBoot 的 SourceList.GameFileDownloadSource。
    private static readonly string[] GameCdnMirrorHosts =
    {
        "assets1.xboxlive.cn",
        "assets2.xboxlive.cn",
        "assets1.xboxlive.com",
        "assets2.xboxlive.com",
        "xvcf1.xboxlive.com",
        "xvcf2.xboxlive.com",
        "d1.xboxlive.cn",
        "d2.xboxlive.cn",
        "d1.xboxlive.com",
        "d2.xboxlive.com",
    };

    /// <summary>把微软 CDN 的下载地址展开成"原始地址 + 各镜像域名"的一串候选。
    /// 非微软 CDN 的地址原样返回（只一个）。</summary>
    private static IEnumerable<string> EnumerateCdnMirrors(string url)
    {
        yield return url;

        Uri uri;
        try { uri = new Uri(url); }
        catch { yield break; }

        var host = uri.Host;
        var isMsCdn = host.EndsWith(".xboxlive.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".xboxlive.cn", StringComparison.OrdinalIgnoreCase);
        if (!isMsCdn) yield break;

        var router = uri.PathAndQuery;
        foreach (var mirror in GameCdnMirrorHosts)
        {
            if (string.Equals(mirror, host, StringComparison.OrdinalIgnoreCase)) continue;
            yield return $"{uri.Scheme}://{mirror}{router}";
        }
    }

    /// <summary>从一串候选 URL 里挑一个能用的下载安装包（多源回退：某个源失败自动换下一个）。
    /// 只负责把完好的安装包拿到本地 version_save 并登记全局缓存，不做解压等安装动作——</summary>
    private async Task<string> DownloadFromUrlAsync(IEnumerable<string> urls, string versionName, string targetDir,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);

        var urlList = urls.ToList();
        var firstUrl = urlList.FirstOrDefault();
        var ext = string.IsNullOrEmpty(firstUrl) ? ".appx" : Path.GetExtension(new Uri(firstUrl).AbsolutePath);
        if (string.IsNullOrEmpty(ext)) ext = ".appx";
        var fileName = $"Minecraft-{versionName}{ext}";

        // 安装包缓存放在目标目录 version_save 下：同一版本再次安装时直接复用，
        // 不重新下载（首次下载完成后校验完整性，损坏就删除重下）。
        var versionSaveDir = Path.Combine(targetDir, "version_save");
        Directory.CreateDirectory(versionSaveDir);
        var filePath = Path.Combine(versionSaveDir, fileName);

        if (!IsValidPackage(filePath))
        {
            // 全局缓存索引：其他目录里已缓存过同一版本的安装包，直接复用，不再重复下载
            var cachedEntry = GamePackageCacheIndex.Find(versionName, "client");
            if (cachedEntry != null && IsValidPackage(cachedEntry.FilePath))
            {
                try
                {
                    File.Copy(cachedEntry.FilePath, filePath, overwrite: true);
                    progress?.Report(new ProgressInfo($"使用全局缓存的安装包 {versionName}（{cachedEntry.FilePath}）", 1, 1, ""));
                }
                catch
                {
                    try { File.Delete(filePath); } catch { }
                }
            }
        }

        if (!IsValidPackage(filePath, out var initialReason))
        {
            LauncherLogService.AppendLine($"[Bedrock下载] 目标文件当前不可复用：{initialReason}，开始按镜像列表下载（共 {urlList.Count} 个候选源）");
            Exception? lastError = null;
            foreach (var url in urlList)
            {
                try
                {
                    progress?.Report(new ProgressInfo($"正在下载基岩版客户端 {versionName}", 0, 1, url));
                    LauncherLogService.AppendLine($"[Bedrock下载] 尝试源：{url}");

                    try { File.Delete(filePath); } catch { }

                    await DownloadToFileAsync(url, filePath, versionName, progress, ct);

                    // 下载完先验证是不是完好的安装包（zip 或 MSIXVC/XVD）：网上下到一半/被劫持
                    // 成 HTML 都会在这里暴露，直接删掉换下一个源，全失败再报错，避免后面"解压失败"。
                    if (IsValidPackage(filePath, out var checkReason))
                    {
                        LauncherLogService.AppendLine($"[Bedrock下载] 校验通过（{checkReason}），使用此源：{url}");
                        break;
                    }
                    LauncherLogService.AppendLine($"[Bedrock下载]   -> {url} 被判定无效，原因：{checkReason}");
                    try { File.Delete(filePath); } catch { }
                    lastError = new InvalidOperationException($"从 {url} 下载的文件不完整或无效：{checkReason}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LauncherLogService.AppendLine($"[Bedrock下载]   -> {url} 下载抛出异常：{ex.GetType().Name}: {ex.Message}");
                    lastError = ex;
                }
            }

            if (!IsValidPackage(filePath, out var finalReason))
            {
                try { File.Delete(filePath); } catch { }
                LauncherLogService.AppendLine($"[Bedrock下载] 所有源均失败，最终原因：{finalReason}");
                throw new InvalidOperationException(
                    $"基岩版客户端 {versionName} 所有下载源均失败，请检查网络后重试。" +
                    (lastError != null ? $"\n详细信息：{lastError.Message}" : ""));
            }

            // 登记到全局缓存索引：以后在别的目录安装同一版本直接复用
            GamePackageCacheIndex.Register(versionName, "client", filePath, GamePackageCacheIndex.ComputeMd5(filePath));
        }
        else
        {
            progress?.Report(new ProgressInfo($"使用已下载的安装包 {versionName}", 1, 1, ""));
        }

        return filePath;
    }

    /// <summary>从一个 URL 下载文件到本地（带进度上报）。</summary>
    private static async Task DownloadToFileAsync(string url, string filePath, string versionName,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XCL2-Launcher/1.0");

        // 保留当前请求的UA与微软CDN候选，使用统一传输的分片、空闲超时、总限速及原子提交。
        // 签名URL/认证请求不会被转发到公共镜像；外层继续验证ZIP/MSIXVC包完整性。
        await DownloadEndpoints.DownloadFileWithFallbackAsync(http, url, preferMirror: false, filePath,
            $"基岩版客户端 {versionName} 下载失败", progress, ct).ConfigureAwait(false);
    }

    /// <summary>校验文件是否为完好的安装包：MSIXVC（GDK）走魔数+头解析校验，
    /// 其余（.appx/.zip 等）走 zip 校验。下载后的完整性与换源判断统一走这里，
    /// 两个格式共用一个入口。</summary>
    private static bool IsValidPackage(string path) => IsValidPackage(path, out _);

    private static bool IsValidPackage(string path, out string reason)
    {
        if (BedrockDecode.GdkPackageExtractor.IsMsixvcPackage(path))
            return BedrockDecode.GdkPackageExtractor.ValidateMsixvc(path, out reason);
        return IsValidZip(path, out reason);
    }

    /// <summary>校验文件是否为完好的 zip 压缩包。
    /// 刚下载完的大文件（几百 MB 的客户端安装包）落地瞬间经常被杀毒软件/Windows Defender
    /// 实时扫描短暂锁住，此时读文件会抛 IOException/UnauthorizedAccessException——
    /// 以前这里逮到异常就直接判"文件损坏"，导致好端端下载完成的包被删掉重新下载一遍
    /// （用户看到的"进度条从 0 重新开始"就是这么来的，跟网络/CDN 完全无关）。
    /// 现在对"文件被占用"这类瞬时性异常做几次短暂重试，真正打不开（几百毫秒后依然
    /// 拿不到读句柄，或者压缩包结构本身就是坏的）才判定为无效。</summary>
    private static bool IsValidZip(string path) => IsValidZip(path, out _);

    /// <summary>校验版本，额外把"为什么判定无效"的具体原因带出来（写日志用，
    /// 之前排查"重复下载"时缺的就是这一段——之前只知道"判定无效了"，
    /// 不知道是文件被占用、大小不对、还是压缩包结构本身就是坏的。</summary>
    private static bool IsValidZip(string path, out string reason)
    {
        if (!File.Exists(path)) { reason = "文件不存在"; return false; }

        var fileLen = new FileInfo(path).Length;
        if (fileLen == 0) { reason = "文件大小为 0"; return false; }

        const int maxAttempts = 5;
        Exception? lastEx = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var archive = ZipFile.OpenRead(path);
                var count = archive.Entries.Count;
                if (count > 0) { reason = "有效"; return true; }
                reason = "压缩包内没有条目（entries=0），文件大小 " + fileLen + " 字节";
                return false;
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                // 大概率是杀毒软件/索引服务正在扫描刚落地的文件，短暂占用句柄——
                // 稍等一下再试，不要立刻当成"下载损坏"。
                lastEx = ex;
                Thread.Sleep(400);
            }
            catch (UnauthorizedAccessException ex) when (attempt < maxAttempts)
            {
                lastEx = ex;
                Thread.Sleep(400);
            }
            catch (Exception ex)
            {
                // 压缩包结构本身就是坏的（比如下载到一半被截断、或返回了 HTML 错误页），
                // 这种才是真的需要换源重下的情况。
                reason = $"打开压缩包失败：{ex.GetType().Name}: {ex.Message}（文件大小 {fileLen} 字节）";
                return false;
            }
        }
        reason = $"多次重试后仍无法打开（可能一直被占用）：{lastEx?.GetType().Name}: {lastEx?.Message}（文件大小 {fileLen} 字节）";
        return false;
    }

    /// <summary>在安装目录里找 Minecraft 客户端可执行文件（含 extracted 子目录）。</summary>
    public static string? FindClientExe(string installDir)
    {
        if (!Directory.Exists(installDir)) return null;

        var candidates = new[]
        {
            Path.Combine(installDir, "Minecraft.Windows.exe"),
            Path.Combine(installDir, "Minecraft.Windows", "Minecraft.Windows.exe"),
            Path.Combine(installDir, "extracted", "Minecraft.Windows.exe"),
            Path.Combine(installDir, "extracted", "Minecraft.Windows", "Minecraft.Windows.exe"),
        };
        foreach (var exe in candidates)
            if (File.Exists(exe)) return exe;

        var files = Directory.GetFiles(installDir, "*.exe", SearchOption.AllDirectories);
        return files.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).Contains("Minecraft", StringComparison.OrdinalIgnoreCase));
    }

    // ==================== 启动 ====================

    /// <summary>
    /// 启动已下载的基岩版客户端。启动前自动补全运行库（VC++ 2015-2022 x64 等支持库），
    /// 避免因缺运行库直接闪退。
    /// </summary>
    public static async Task<Process?> LaunchClientAsync(string installDir,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!BedrockLaunchService.IsOsSupported)
            throw new PlatformNotSupportedException(BedrockLaunchService.UnsupportedOsMessage);
        if (!Directory.Exists(installDir))
            throw new InvalidOperationException($"安装目录不存在：{installDir}");
        if (File.Exists(Path.Combine(installDir, IncompleteInstallationMarker)))
            throw new InvalidOperationException("该客户端安装尚未完成，请在下载管理中继续或重试安装后再启动。");

        var exe = FindClientExe(installDir);
        if (exe == null)
            throw new InvalidOperationException(
                $"在目录 {installDir} 中找不到 Minecraft.Windows.exe。" +
                "请确认客户端已正确下载并解压。");

        // 自动补全支持库：客户端（GDK/UWP）运行依赖 VC++ 运行库等框架包
        await BedrockContentService.EnsureSupportLibrariesInstalledAsync(progress, ct);
        ct.ThrowIfCancellationRequested();

        var manifestDir = Path.GetDirectoryName(exe)!;
        // AppxManifest.xml 一般在解压根目录，不一定跟 exe 同级，往上找一层兜底
        var manifestSearchRoot = BedrockPackageRegistrationHelper.FindAppxManifest(manifestDir) != null
            ? manifestDir
            : installDir;

        if (BedrockPackageRegistrationHelper.FindAppxManifest(manifestSearchRoot) != null)
        {
            try
            {
                progress?.Report(new ProgressInfo("正在注册基岩版客户端到系统（首次启动该版本需要这一步）", 0, 1, ""));
                await BedrockPackageRegistrationHelper.RegisterAndLaunchAsync(manifestSearchRoot,
                    new Progress<(int Percent, string State)>(p =>
                        progress?.Report(new ProgressInfo($"正在注册：{p.State}", p.Percent, 100, ""))), ct);
                return null; // 通过 shell:AppsFolder 启动，拿不到 Process 句柄，跟 BedrockLaunchService.Launch 行为一致
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // 注册这条路失败：明确告诉用户这是降级，不要让用户以为游戏"正常装好了"
                progress?.Report(new ProgressInfo(
                    $"注册应用包失败（{ex.Message}），将尝试直接启动可执行文件——" +
                    "这种方式下存档/账号登录/多人联机等依赖应用包身份的功能可能无法正常使用。",
                    0, 1, ""));
            }
        }
        else
        {
            progress?.Report(new ProgressInfo(
                "未找到 AppxManifest.xml，无法把该客户端注册为系统应用，将尝试直接启动可执行文件——" +
                "这种方式下存档/账号登录/多人联机等依赖应用包身份的功能可能无法正常使用。",
                0, 1, ""));
        }

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = true,
        };
        ct.ThrowIfCancellationRequested();
        return Process.Start(psi);
    }

    /// <summary>旧的同步签名保留（内部等异步完成）。</summary>
    public static Process? LaunchClient(string installDir)
        => LaunchClientAsync(installDir).GetAwaiter().GetResult();

    private static readonly TimeSpan EarlyExitGrace = TimeSpan.FromSeconds(8);

    public static void MonitorLaunchedProcess(Process? proc, Action<string>? onLikelyEarlyExit)
    {
        if (proc == null) return; 

        var startedAt = DateTime.UtcNow;
        var handled = 0; // 用 Interlocked 做一次性门闩，防止 Exited 事件 + 下面的同步兜底重复处理/重复 Dispose

        void HandleExit()
        {
            if (Interlocked.Exchange(ref handled, 1) != 0) return;
            try
            {
                var elapsed = DateTime.UtcNow - startedAt;
                if (elapsed < EarlyExitGrace)
                {
                    onLikelyEarlyExit?.Invoke(
                        $"基岩版客户端进程在启动后 {elapsed.TotalSeconds:F1} 秒内就退出了，" +
                        "很可能是刚启动就闪退（常见原因：缺少运行库、显卡驱动过旧、" +
                        "杀毒软件拦截），而不是正常关闭游戏。");
                }
            }
            finally
            {
                // 事件处理完成后释放，防止 Process 对象常驻内存/占用句柄——
                // 这是原来 4 处调用点丢弃返回值不 Dispose 造成的那部分泄漏的根本修复点。
                proc.Dispose();
            }
        }

        try
        {
            proc.EnableRaisingEvents = true;
        }
        catch
        {
            // 极少数情况下（进程已经退出/权限问题）设置该属性本身会抛异常，
            // 此时直接释放句柄，不再尝试监控，避免半途而废地占着句柄。
            proc.Dispose();
            return;
        }

        proc.Exited += (_, _) => HandleExit();

        // 极端时序：进程在我们订阅 Exited 之前就已经退出，Exited 事件在部分系统上可能
        // 不会再补发。补一次同步检查兜底（HandleExit 内部的门闩保证不会跟 Exited 事件重复处理）。
        if (proc.HasExited)
            HandleExit();
    }
}
