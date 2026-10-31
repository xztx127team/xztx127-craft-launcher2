using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using XCL2.App.Models;

namespace XCL2.App.Services;

public class BedrockContentService
{
    /// <summary>
    /// 基岩版数据根目录。Minecraft for Windows 把用户数据放在 UWP 应用的 LocalState 下，
    /// 该路径对普通用户是**可读写**的（跟 WindowsApps 那种受保护目录不同），
    /// 所以导入世界/附加包不需要管理员权限，也不需要碰任何 Store 私有 API。
    /// </summary>
    public static string ComMojangDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages", "Microsoft.MinecraftUWP_8wekyb3d8bbwe",
        "LocalState", "games", "com.mojang");

    public static bool IsBedrockDataPresent => Directory.Exists(ComMojangDir);

    public enum BedrockContentKind { World, ResourcePack, BehaviorPack, Addon, WorldTemplate, Unknown }

    /// <summary>一次导入的结果。</summary>
    public sealed record ImportResult(List<string> Installed, List<string> Failed);

    /// <summary>按扩展名判断类型。.mcpack 内部还要读 manifest 才能确定是资源包还是行为包，
    /// 那一步在 ImportPackAsync 里做，这里只做粗分类。</summary>
    public static BedrockContentKind ClassifyByExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mcworld" => BedrockContentKind.World,
            ".mcaddon" => BedrockContentKind.Addon,
            ".mcpack" => BedrockContentKind.ResourcePack,   // 待 manifest 细分
            ".mctemplate" => BedrockContentKind.WorldTemplate,
            _ => BedrockContentKind.Unknown,
        };

    /// <summary>
    /// 批量导入基岩版内容。
    /// 基岩版没装（com.mojang 目录不存在）时直接抛出带说明的异常，
    /// 而不是自己建一个目录——那样游戏根本读不到，用户还以为装好了。
    /// </summary>
    public ImportResult ImportMany(IEnumerable<string> paths, IProgress<string>? progress = null)
    {
        if (!IsBedrockDataPresent)
            throw new InvalidOperationException(
                "没有找到基岩版的数据目录，说明这台电脑上还没有安装 Minecraft for Windows（基岩版）。\n" +
                "请先从 Microsoft Store 安装基岩版并至少启动一次（首次启动才会创建数据目录），再来导入内容。");

        var installed = new List<string>();
        var failed = new List<string>();

        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            try
            {
                switch (ClassifyByExtension(path))
                {
                    case BedrockContentKind.World:
                        ImportWorld(path);
                        installed.Add($"{name} → 世界存档");
                        break;
                    case BedrockContentKind.WorldTemplate:
                        ExtractIntoNewSubdir(path, Path.Combine(ComMojangDir, "world_templates"));
                        installed.Add($"{name} → 世界模板");
                        break;
                    case BedrockContentKind.Addon:
                        var n = ImportAddon(path, progress);
                        installed.Add($"{name} → 附加包（含 {n} 个子包）");
                        break;
                    case BedrockContentKind.ResourcePack:
                        var kind = ImportPack(path);
                        installed.Add($"{name} → {(kind == BedrockContentKind.BehaviorPack ? "行为包" : "资源包")}");
                        break;
                    default:
                        failed.Add($"{name}（不是基岩版能识别的文件类型）");
                        break;
                }
                progress?.Report($"已导入 {name}");
            }
            catch (Exception ex)
            {
                failed.Add($"{name}（{ex.Message}）");
            }
        }

        return new ImportResult(installed, failed);
    }

    private static void ImportWorld(string mcworldPath)
        => ExtractIntoNewSubdir(mcworldPath, Path.Combine(ComMojangDir, "minecraftWorlds"));

    /// <summary>
    /// .mcpack：读内部 manifest.json 的 modules[].type 判断是资源包还是行为包。
    /// type 为 "data" / "script" 的是行为包，"resources" 的是资源包。
    /// 读不出来时按资源包处理（更常见，且装错了用户在游戏里一眼能看出来并手动挪走）。
    /// </summary>
    private static BedrockContentKind ImportPack(string mcpackPath)
    {
        var kind = BedrockContentKind.ResourcePack;
        try
        {
            using var archive = ZipFile.OpenRead(mcpackPath);
            var manifest = archive.Entries.FirstOrDefault(e =>
                e.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
            if (manifest != null)
            {
                var text = ArchiveImportSafety.ReadManifest(manifest);
                if (Regex.IsMatch(text, "\"type\"\\s*:\\s*\"(data|script)\"", RegexOptions.IgnoreCase))
                    kind = BedrockContentKind.BehaviorPack;
            }
        }
        catch { /* 读不出就按资源包 */ }

        var target = kind == BedrockContentKind.BehaviorPack
            ? Path.Combine(ComMojangDir, "development_behavior_packs")
            : Path.Combine(ComMojangDir, "development_resource_packs");

        ExtractIntoNewSubdir(mcpackPath, target);
        return kind;
    }

    /// <summary>
    /// .mcaddon 是一个"包的容器"：里面装着若干 .mcpack，或者直接是若干带 manifest.json 的子目录。
    /// 先解到临时目录，再把里面每个包各自按类型装好。
    /// </summary>
    private int ImportAddon(string mcaddonPath, IProgress<string>? progress)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"xcl2_mcaddon_{Guid.NewGuid():N}");
        var count = 0;
        try
        {
            ContentArchiveInstaller.ExtractNew(mcaddonPath, tmp);

            // 情况 A：里面是若干 .mcpack 文件
            foreach (var pack in Directory.GetFiles(tmp, "*.mcpack", SearchOption.AllDirectories))
            {
                ImportPack(pack);
                count++;
                progress?.Report($"已装入子包 {Path.GetFileName(pack)}");
            }

            // 情况 B：里面直接是若干带 manifest.json 的子目录
            if (count == 0)
            {
                foreach (var manifest in Directory.GetFiles(tmp, "manifest.json", SearchOption.AllDirectories))
                {
                    var packDir = Path.GetDirectoryName(manifest)!;
                    var text = File.ReadAllText(manifest);
                    var isBehavior = Regex.IsMatch(text, "\"type\"\\s*:\\s*\"(data|script)\"", RegexOptions.IgnoreCase);
                    var target = Path.Combine(ComMojangDir,
                        isBehavior ? "development_behavior_packs" : "development_resource_packs",
                        UniqueDirName(Path.Combine(ComMojangDir,
                            isBehavior ? "development_behavior_packs" : "development_resource_packs"),
                            Path.GetFileName(packDir)));
                    CopyDir(packDir, target);
                    count++;
                }
            }

            if (count == 0)
                throw new InvalidOperationException("该 .mcaddon 里没有找到任何有效的资源包/行为包。");

            return count;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    /// <summary>把 zip 解到目标父目录下一个以文件名命名的新子目录（重名自动加序号）。
    /// 带路径穿越防护，挡住 zip 里构造的 "../" 条目。</summary>
    private static void ExtractIntoNewSubdir(string zipPath, string parentDir)
    {
        ArchiveImportSafety.CheckLinks(parentDir);
        var name = UniqueDirName(parentDir, Path.GetFileNameWithoutExtension(zipPath));
        var target = ArchiveImportSafety.Resolve(parentDir, name);
        ContentArchiveInstaller.ExtractNew(zipPath, target);
    }

    private static string UniqueDirName(string parent, string desired)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) desired = desired.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(desired)) desired = "imported";
        var name = desired;
        var i = 2;
        while (Path.Exists(ArchiveImportSafety.Resolve(parent, name)))
        {
            name = $"{desired} ({i})"; i++;
        }
        return name;
    }

    private static void CopyDir(string src, string dst)
    {
        ArchiveImportSafety.CheckLinks(src);
        ArchiveImportSafety.CheckLinks(dst);
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
        {
            ArchiveImportSafety.CheckLinks(f);
            var destination = ArchiveImportSafety.Resolve(dst, Path.GetFileName(f));
            File.Copy(f, destination, overwrite: true);
        }
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    private const string BdsLinksApiUrl = "https://net-secondary.web.minecraft-services.net/api/v1.0/download/links";
    private const string BdsPageUrl = "https://www.minecraft.net/en-us/download/server/bedrock";

    private static readonly Regex BdsLinkPattern = new(
        @"https://[^\s""']*bin-win/bedrock-server-[\d.]+\.zip",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BdsPreviewLinkPattern = new(
        @"https://[^\s""']*bin-win-preview/bedrock-server-[\d.]+\.zip",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ===== BDS 历史版本归档：Bedrock-OSS/BDS-Versions =====
    // 除了官方接口只给的最新版，这里能拿到全部历史版本的直链与 SHA1（windows/{version}.json）。
    private const string BdsVersionsListUrl = "https://cdn.jsdelivr.net/gh/Bedrock-OSS/BDS-Versions@main/versions.json";

    private static readonly string[] BdsVersionsListMirrorUrls = new[]
    {
        "https://fastly.jsdelivr.net/gh/Bedrock-OSS/BDS-Versions@main/versions.json",
        "https://gcore.jsdelivr.net/gh/Bedrock-OSS/BDS-Versions@main/versions.json",
        "https://raw.githubusercontent.com/Bedrock-OSS/BDS-Versions/main/versions.json",
        "https://ghp.ci/https://raw.githubusercontent.com/Bedrock-OSS/BDS-Versions/main/versions.json",
        "https://ghproxy.com/https://raw.githubusercontent.com/Bedrock-OSS/BDS-Versions/main/versions.json",
        "https://mirror.ghproxy.com/https://raw.githubusercontent.com/Bedrock-OSS/BDS-Versions/main/versions.json",
    };

    private static string BdsVersionMetaUrl(string version) => $"https://cdn.jsdelivr.net/gh/Bedrock-OSS/BDS-Versions@main/windows/{version}.json";

    private static IEnumerable<string> BdsVersionMetaMirrorUrls(string version)
    {
        yield return $"https://fastly.jsdelivr.net/gh/Bedrock-OSS/BDS-Versions@main/windows/{version}.json";
        yield return $"https://gcore.jsdelivr.net/gh/Bedrock-OSS/BDS-Versions@main/windows/{version}.json";
        yield return $"https://raw.githubusercontent.com/Bedrock-OSS/BDS-Versions/main/windows/{version}.json";
        yield return $"https://ghp.ci/https://raw.githubusercontent.com/Bedrock-OSS/BDS-Versions/main/windows/{version}.json";
        yield return $"https://ghproxy.com/https://raw.githubusercontent.com/Bedrock-OSS/BDS-Versions/main/windows/{version}.json";
        yield return $"https://mirror.ghproxy.com/https://raw.githubusercontent.com/Bedrock-OSS/BDS-Versions/main/windows/{version}.json";
    }

    private static async Task<string?> FetchJsonWithMirrorsAsync(string primary, IEnumerable<string> mirrors, CancellationToken ct)
    {
        foreach (var url in new[] { primary }.Concat(mirrors))
        {
            try
            {
                using var http = CreateHttp();
                var json = await http.GetStringAsync(url, ct);
                if (!string.IsNullOrWhiteSpace(json)) return json;
            }
            catch
            {
                // 试下一个源
            }
        }
        return null;
    }

    /// <summary>拉取 BDS 历史版本归档里的版本列表（归档是升序：老→新，这里反转成新→老）。</summary>
    public async Task<List<string>> GetDedicatedServerVersionsAsync(BdsChannel channel, CancellationToken ct = default)
    {
        var json = await FetchJsonWithMirrorsAsync(BdsVersionsListUrl, BdsVersionsListMirrorUrls, ct);
        if (json == null) return new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            // versions.json 结构：{ "linux": {"stable":..., "preview":..., "versions":[...], "preview_versions":[...]}, "windows": {...} }
            if (!doc.RootElement.TryGetProperty("windows", out var win)) return new List<string>();
            var prop = channel == BdsChannel.Preview ? "preview_versions" : "versions";
            if (!win.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return new List<string>();

            var list = arr.EnumerateArray()
                .Select(v => v.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Cast<string>()
                .ToList();
            list.Reverse();
            return list;
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// 按指定版本号解析 BDS 下载直链：优先归档的单版本元数据（windows/{version}.json，
    /// 内含官方 download_url + SHA1），归档没收录时按官方 CDN 路径规则直接拼。
    /// </summary>
    public async Task<BdsInfo> ResolveDedicatedServerVersionAsync(string version, BdsChannel channel, CancellationToken ct = default)
    {
        var meta = await FetchJsonWithMirrorsAsync(BdsVersionMetaUrl(version), BdsVersionMetaMirrorUrls(version), ct);
        if (meta != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(meta);
                if (doc.RootElement.TryGetProperty("download_url", out var du))
                {
                    var url = du.GetString();
                    if (!string.IsNullOrEmpty(url))
                        return new BdsInfo(url, version, channel);
                }
            }
            catch { /* 元数据格式异常就按 CDN 规则兜底 */ }
        }

        var cdnDir = channel == BdsChannel.Preview ? "bin-win-preview" : "bin-win";
        return new BdsInfo($"https://www.minecraft.net/bedrockdedicatedserver/{cdnDir}/bedrock-server-{version}.zip", version, channel);
    }

    public sealed record BdsInfo(string Url, string Version, BdsChannel Channel);

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XCL2-Launcher/1.0");
        return http;
    }

    /// <summary>抓当前 BDS（指定渠道）的下载直链和版本号。</summary>
    public async Task<BdsInfo> ResolveDedicatedServerUrlAsync(BdsChannel channel = BdsChannel.Stable, CancellationToken ct = default)
    {
        // 优先走官方 JSON 接口。
        try
        {
            using var http = CreateHttp();
            var json = await http.GetStringAsync(BdsLinksApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var wantType = channel == BdsChannel.Preview ? "serverBedrockPreviewWindows" : "serverBedrockWindows";
            if (doc.RootElement.TryGetProperty("result", out var result) &&
                result.TryGetProperty("links", out var links))
            {
                foreach (var link in links.EnumerateArray())
                {
                    if (link.TryGetProperty("downloadType", out var dt) &&
                        string.Equals(dt.GetString(), wantType, StringComparison.OrdinalIgnoreCase) &&
                        link.TryGetProperty("downloadUrl", out var du))
                    {
                        var apiUrl = du.GetString();
                        if (!string.IsNullOrEmpty(apiUrl))
                        {
                            var apiVer = Regex.Match(apiUrl, @"bedrock-server-([\d.]+)\.zip").Groups[1].Value;
                            return new BdsInfo(apiUrl, string.IsNullOrEmpty(apiVer) ? "未知" : apiVer, channel);
                        }
                    }
                }
            }
        }
        catch
        {
            // 接口这次不通就落到下面的 HTML 兜底，不在这里直接失败。
        }

        // 兜底：从下载页正则抓（如果官网哪天又改回服务端渲染，这条路还能用）。
        using var htmlHttp = CreateHttp();
        var html = await htmlHttp.GetStringAsync(BdsPageUrl, ct);
        var pattern = channel == BdsChannel.Preview ? BdsPreviewLinkPattern : BdsLinkPattern;
        var m = pattern.Match(html);
        if (!m.Success)
            throw new InvalidOperationException(
                "没能找到基岩版服务端的下载链接（官方接口和网页兜底都没拿到）。\n" +
                "可能是官方接口临时不可用，或者当前网络访问不到 minecraft-services.net / minecraft.net。" +
                "可以稍后重试，或手动去官网下载。");

        var url = m.Value;
        var ver = Regex.Match(url, @"bedrock-server-([\d.]+)\.zip").Groups[1].Value;
        return new BdsInfo(url, string.IsNullOrEmpty(ver) ? "未知" : ver, channel);
    }

    /// <summary>
    /// 下载并解压 BDS 到指定目录。targetDir 完全由调用方决定（工具箱页面里由用户选择的
    /// 文件夹，或者 AppConfig.BedrockServerDefaultDownloadDir 里保存的默认下载文件夹），
    /// 本方法不会自己拼一个"默认位置"。
    /// </summary>
    public async Task<string> DownloadDedicatedServerAsync(string targetDir, BdsChannel channel,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default)
    {
        progress?.Report(new ProgressInfo("正在查询服务端版本", 0, 1, ""));
        var info = await ResolveDedicatedServerUrlAsync(channel, ct);
        return await DownloadDedicatedServerCoreAsync(targetDir, info, progress, ct);
    }

    /// <summary>
    /// 按指定版本号下载 BDS（走 BDS-Versions 归档拿直链）；version 为空时回退到最新版路径。
    /// </summary>
    public async Task<string> DownloadDedicatedServerAsync(string targetDir, BdsChannel channel,
        string? version, IProgress<ProgressInfo>? progress, CancellationToken ct = default)
    {
        progress?.Report(new ProgressInfo("正在查询服务端版本", 0, 1, ""));
        var info = string.IsNullOrWhiteSpace(version)
            ? await ResolveDedicatedServerUrlAsync(channel, ct)
            : await ResolveDedicatedServerVersionAsync(version, channel, ct);
        return await DownloadDedicatedServerCoreAsync(targetDir, info, progress, ct);
    }

    /// <summary>旧签名保留（默认正式版），避免其它调用点跟着改。</summary>
    public Task<string> DownloadDedicatedServerAsync(string targetDir,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default)
        => DownloadDedicatedServerAsync(targetDir, BdsChannel.Stable, progress, ct);

    private async Task<string> DownloadDedicatedServerCoreAsync(string targetDir, BdsInfo info,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {

        ArchiveImportSafety.CheckLinks(targetDir);
        Directory.CreateDirectory(targetDir);

        // 安装包缓存放到目标目录下的 version_save（不占系统临时目录，随实例走，
        // 下次重装同版本直接复用，不再重复下载）。
        var versionSaveDir = ArchiveImportSafety.Resolve(targetDir, "version_save");
        Directory.CreateDirectory(versionSaveDir);
        var zipPath = ArchiveImportSafety.Resolve(versionSaveDir, $"bedrock-server-{info.Version}.zip");

        if (IsValidZip(zipPath))
        {
            // 同一版本已下载且完好：直接用缓存，跳过重复下载
            progress?.Report(new ProgressInfo($"使用已下载的安装包 {info.Version}", 1, 1, ""));
        }
        else
        {
            // 全局缓存索引：其他目录里已缓存过同一版本的服务端包，直接复用，不再重复下载
            var cachedEntry = GamePackageCacheIndex.Find(info.Version, "server");
            if (cachedEntry != null && IsValidZip(cachedEntry.FilePath))
            {
                try
                {
                    File.Copy(cachedEntry.FilePath, zipPath, overwrite: true);
                    progress?.Report(new ProgressInfo($"使用全局缓存的安装包 {info.Version}（{cachedEntry.FilePath}）", 1, 1, ""));
                }
                catch
                {
                    try { File.Delete(zipPath); } catch { }
                }
            }
        }

        if (!IsValidZip(zipPath))
        {
            progress?.Report(new ProgressInfo($"正在下载基岩版服务端 {info.Version}", 0, 1, ""));

            try { File.Delete(zipPath); } catch { }

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XCL2-Launcher/1.0");

                using var resp = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);

                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0)
                        progress?.Report(new ProgressInfo($"正在下载基岩版服务端 {info.Version}",
                            (int)(done / 1024), (int)(total / 1024), $"{done / 1048576} MB / {total / 1048576} MB"));
                }
            }

            // 下载完成后校验压缩包完整性；损坏的包会导致"解压失败"，这里直接删掉报错，
            // 下次重试会重新下载而不是反复卡在解压。
            if (!IsValidZip(zipPath))
            {
                try { File.Delete(zipPath); } catch { }
                throw new InvalidOperationException(
                    $"基岩版服务端 {info.Version} 下载不完整或包已损坏，已自动清理，请重试。");
            }
        }

        progress?.Report(new ProgressInfo("正在解压", 0, 1, targetDir));

        // 登记到全局缓存索引：以后在别的目录安装同一版本直接复用
        if (IsValidZip(zipPath))
            GamePackageCacheIndex.Register(info.Version, "server", zipPath, GamePackageCacheIndex.ComputeMd5(zipPath));

        // BDS 的 zip 顶层就是 bedrock_server.exe + 若干目录，直接解到目标目录即可。
        // 注意 overwriteFiles: true 会覆盖 server.properties / allowlist.json 这类配置文件，
        // 所以这里对已存在的配置文件做保护——升级服务端时不该把用户的配置冲掉。
        var protectedFiles = new[] { "server.properties", "allowlist.json", "permissions.json", "whitelist.json" };
        ContentArchiveInstaller.Merge(zipPath, targetDir,
            (entry, dest) => protectedFiles.Contains(entry.Name, StringComparer.OrdinalIgnoreCase) && File.Exists(dest), ct);

        try { File.Delete(zipPath); } catch { }

        progress?.Report(new ProgressInfo(Loc.T("Str_Common_Finish", "完成"), 1, 1, info.Version));
        return info.Version;
    }

    /// <summary>
    /// 启动一个已下载好的基岩版服务端实例。bedrock_server.exe 本身就是一个控制台程序，
    /// 直接起进程、不接管它的控制台（不像 Java 版那样走 ServerProcessManager 接管
    /// stdin/stdout 做进程内控制台窗口）——BDS 自己弹出的控制台窗口就是标准用法，
    /// 保持该行为对熟悉"手动跑 bedrock_server.exe"的用户来说最不意外。
    /// 启动前自动补全运行库（bedrock_server.exe 依赖 VC++ 2015-2022 x64，缺了会闪退）。
    /// </summary>
    public static async Task<Process?> LaunchDedicatedServerAsync(string installDir,
        IProgress<ProgressInfo>? progress = null)
    {
        await EnsureSupportLibrariesInstalledAsync(progress);

        var exe = Path.Combine(installDir, "bedrock_server.exe");
        if (!File.Exists(exe))
            throw new InvalidOperationException(
                $"在该目录下没有找到 bedrock_server.exe：\n{installDir}\n" +
                "可能是安装目录选错了，或者该实例还没下载完成。");

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = installDir,   // bedrock_server.exe 按相对路径读 server.properties/世界数据，必须在这里起
            UseShellExecute = true,
        };
        var proc = Process.Start(psi);
        return proc ?? throw new InvalidOperationException("启动基岩版服务端进程失败。");
    }

    /// <summary>旧的同步签名保留（内部等异步完成），避免其它调用点跟着改。</summary>
    public static Process? LaunchDedicatedServer(string installDir)
        => LaunchDedicatedServerAsync(installDir).GetAwaiter().GetResult();

    /// <summary>校验一个文件是否是完好可读的 zip 压缩包。</summary>
    public static bool IsValidZip(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    // ==================== 支持库自动补全（自动安装前置依赖） ====================

    private const string VcRedistUrl = "https://aka.ms/vc14/vc_redist.x64.exe";

    /// <summary>本次进程是否已检查过 UWP 前置依赖（PowerShell 查询较慢，只查一次）。</summary>
    private static bool _uwpDependenciesChecked;

    /// <summary>
    /// 基岩版客户端（UWP/GDK）运行所需的前置 UWP 框架包（从 BedrockBoot 的
    /// UwpDependencyChecker 原样移植）。缺了这些，下载下来的客户端启动时会直接闪退。
    /// </summary>
    private static readonly List<(string Name, string? MinVersion)> UwpDependencies = new()
    {
        ("Microsoft.VCLibs.140.00", "14.0.33519.0"),
        ("Microsoft.NET.Native.Runtime.1.4", null),
        ("Microsoft.NET.Native.Runtime.2.2", "2.2.28604.0"),
        ("Microsoft.VCLibs.140.00.UWPDesktop", null),
        ("Microsoft.Services.Store.Engagement", null),
        ("Microsoft.NET.Native.Framework.1.3", null),
        ("Microsoft.NET.Native.Framework.2.2", "2.2.29512.0"),
        ("Microsoft.GamingServices", "33.108.12001.0"),
    };

    private static readonly string[] VcRedistDisplayNames =
    {
        "Microsoft Visual C++ 2015-2022 Redistributable (x64)",
        "Microsoft Visual C++ 2015-2022 (x64)",
        "Microsoft Visual C++ v14 Redistributable (x64)",
    };

    /// <summary>检查 VC++ 2015-2022 x64 运行库是否已安装（读卸载注册表）。</summary>
    public static bool IsVcRuntimeInstalled()
    {
        var registryPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        foreach (var basePath in registryPaths)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(basePath);
                if (key == null) continue;
                foreach (var subName in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(subName);
                    var displayName = sub?.GetValue("DisplayName") as string;
                    if (string.IsNullOrEmpty(displayName)) continue;
                    foreach (var pattern in VcRedistDisplayNames)
                        if (displayName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                            return true;
                }
            }
            catch { /* 注册表访问失败按未安装处理 */ }
        }
        return false;
    }

    /// <summary>本次进程是否已经跑完过一次完整的支持库检查/安装流程。
    /// 调用方（下载完成后自动启动 等）经常会跟"下载前预装运行库"的调用挨在一起，
    /// 不加该标记会导致同一次下载流程里把 VC++ 运行库又重新下载安装一遍
    /// （UAC 被取消/安装被杀软拦截等情况下 IsVcRuntimeInstalled 仍返回 false，
    /// 第二次调用会真的把 vc_redist.x64.exe 再下一遍——这就是"重复下载"的来源）。</summary>
    private static bool _supportLibrariesEnsured;
    private static readonly SemaphoreSlim _supportLibrariesLock = new(1, 1);

    /// <summary>检查并补全基岩版运行所需的支持库：VC++ 2015-2022 x64 + UWP 框架包
    /// （GamingServices / VCLibs / NET.Native 等，缺了客户端会闪退）。
    /// 同一次进程内只会真正执行一次：后续调用（比如下载完成后自动启动时再次触发）
    /// 直接跳过，不会重新下载/重新安装。</summary>
    public static async Task EnsureSupportLibrariesInstalledAsync(IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_supportLibrariesEnsured)
        {
            progress?.Report(new ProgressInfo("运行库就绪", 1, 1, ""));
            return;
        }

        await RunRuntimeTaskAsync("补全基岩版运行库", progress, ct, EnsureSupportLibrariesCoreAsync);
    }

    private static async Task RunRuntimeTaskAsync(string name, IProgress<ProgressInfo>? progress,
        CancellationToken ct, Func<IProgress<ProgressInfo>?, CancellationToken, Task> work)
    {
        var queue = DownloadQueueService.Instance;
        if (queue.CurrentItem is { } current)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, current.Cts.Token);
            var queueProgress = current.CreateProgress();
            var combined = new Progress<ProgressInfo>(info =>
            {
                queueProgress.Report(info);
                progress?.Report(info);
            });
            await work(combined, linked.Token);
            return;
        }
        await queue.RunAndWaitAsync(name, async (item, token) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
            var queueProgress = item.CreateProgress();
            var combined = new Progress<ProgressInfo>(info =>
            {
                queueProgress.Report(info);
                progress?.Report(info);
            });
            await work(combined, linked.Token);
            return true;
        }, ct);
    }

    private static async Task EnsureSupportLibrariesCoreAsync(IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        // 用锁而不是简单判断标记：避免"下载前预装"和"下载完自动启动前再次确保"
        // 这两次调用时间上挨得很近时，第二次在第一次还没写完标记前就抢跑，
        // 结果又并发下载一次 vc_redist.x64.exe。
        await _supportLibrariesLock.WaitAsync(ct);
        try
        {
            if (_supportLibrariesEnsured)
            {
                progress?.Report(new ProgressInfo("运行库就绪", 1, 1, ""));
                return;
            }

            progress?.Report(new ProgressInfo("检查运行库", 0, 1, ""));

            if (!IsVcRuntimeInstalled())
            {
                progress?.Report(new ProgressInfo("正在自动安装 VC++ 2015-2022 运行库（缺失）", 0, 1, ""));

                var tmpFile = Path.Combine(Path.GetTempPath(), $"vc_redist_{Guid.NewGuid():N}.x64.exe");
                try
                {
                    using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
                    {
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 XCL2-Launcher/1.0");
                        using var resp = await http.GetAsync(VcRedistUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                        resp.EnsureSuccessStatusCode();
                        await using var src = await resp.Content.ReadAsStreamAsync(ct);
                        await using var dst = File.Create(tmpFile);
                        await src.CopyToAsync(dst, ct);
                    }

                    var psi = new ProcessStartInfo(tmpFile)
                    {
                        Arguments = "/install /quiet /norestart",
                        UseShellExecute = true,
                        Verb = "runas",          // 安装运行库需要管理员权限
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };
                    ct.ThrowIfCancellationRequested();
                    async Task<int> InstallAsync()
                    {
                        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 VC++ 运行库安装程序。");
                        await proc.WaitForExitAsync();
                        return proc.ExitCode;
                    }
                    var item = DownloadQueueService.Instance.CurrentItem;
                    var exitCode = item != null ? await item.RunInstallerPhaseAsync(InstallAsync) : await InstallAsync();
                    ct.ThrowIfCancellationRequested();
                    if (exitCode is not (0 or 1638 or 3010))
                        throw new InvalidOperationException($"VC++ 运行库安装失败（退出码 {exitCode}）。");
                    progress?.Report(new ProgressInfo(exitCode == 3010 ? "VC++ 运行库已安装，需要重启计算机" : "VC++ 运行库安装完成", 1, 1, ""));
                    if (exitCode == 3010 && item != null) item.CompletionText = "运行库已安装，需要重启计算机";
                }
                catch (OperationCanceledException) { throw; }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    throw new DownloadUserCanceledException("未授予管理员权限，VC++ 运行库安装已取消。", ex);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"自动安装 VC++ 运行库失败：{ex.Message}，可手动下载 vc_redist.x64.exe 安装", ex);
                }
                finally
                {
                    try { File.Delete(tmpFile); } catch { }
                }
            }

            // UWP 前置框架包：只查一次（PowerShell 查询慢），缺了就自动下载安装
            if (!_uwpDependenciesChecked)
            {
                await InstallMissingUwpDependenciesCoreAsync(progress, ct);
                _uwpDependenciesChecked = true;
            }

            progress?.Report(new ProgressInfo("运行库就绪", 1, 1, ""));
            _supportLibrariesEnsured = true;
        }
        finally
        {
            _supportLibrariesLock.Release();
        }
    }

    // ==================== UWP 前置依赖（移植自 BedrockBoot） ====================

    /// <summary>列出缺失的 UWP 前置框架包（PowerShell 查询已安装 Appx 包，逐个比对版本）。</summary>
    public static async Task<List<(string Name, string? MinVersion)>> GetMissingUwpDependenciesAsync(CancellationToken ct = default)
    {
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (exitCode, output) = await RunPowershellAsync(
                "-NoProfile -ExecutionPolicy Bypass -Command \"Get-AppxPackage | Select-Object Name, Version | ConvertTo-Csv -NoTypeInformation\"", ct);
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
                ParseAppxPackageCsv(output, installed);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 查询失败按全部缺失处理 */ }

        var missing = new List<(string, string?)>();
        foreach (var (name, minVersion) in UwpDependencies)
        {
            if (!installed.TryGetValue(name, out var installedVersion))
            {
                missing.Add((name, minVersion));
            }
            else if (!string.IsNullOrEmpty(minVersion) && CompareUwpVersions(installedVersion, minVersion) < 0)
            {
                missing.Add((name, minVersion));
            }
        }
        return missing;
    }

    /// <summary>自动下载并安装缺失的 UWP 前置框架包（rg-adguard 换直链 → 下载 → Add-AppxPackage）。</summary>
    public static Task InstallMissingUwpDependenciesAsync(IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default) =>
        RunRuntimeTaskAsync("补全 UWP 前置框架包", progress, ct, InstallMissingUwpDependenciesCoreAsync);

    private static async Task InstallMissingUwpDependenciesCoreAsync(IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var missing = await GetMissingUwpDependenciesAsync(ct);
        if (missing.Count == 0) return;

        progress?.Report(new ProgressInfo($"发现 {missing.Count} 个缺失的 UWP 前置框架包，开始自动安装", 0, 1, ""));

        var failures = new List<string>();
        foreach (var (name, minVersion) in missing)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                progress?.Report(new ProgressInfo($"正在获取 {name} 下载链接", 0, 1, ""));

                var url = await ResolveUwpPackageUrlAsync(name, minVersion, ct);
                if (string.IsNullOrEmpty(url))
                {
                    throw new InvalidOperationException($"{name} 未找到可用的下载地址。");
                }

                var tmpFile = Path.Combine(Path.GetTempPath(), $"{name}_{Guid.NewGuid():N}.appx");
                try
                {
                    progress?.Report(new ProgressInfo($"正在下载并安装 {name}", 0, 1, ""));

                    using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
                    {
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 XCL2-Launcher/1.0");
                        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                        resp.EnsureSuccessStatusCode();
                        await using var src = await resp.Content.ReadAsStreamAsync(ct);
                        await using var dst = File.Create(tmpFile);
                        await src.CopyToAsync(dst, ct);
                    }

                    var (exitCode, error) = await RunPowershellAsync($"-NoProfile -Command \"Add-AppxPackage -Path '{tmpFile.Replace("'", "''")}'\"", ct, isInstaller: true);
                    if (exitCode != 0)
                        throw new InvalidOperationException($"{name} 安装失败：{error.Trim()}");
                    else
                        progress?.Report(new ProgressInfo($"{name} 安装完成", 1, 1, ""));
                }
                finally
                {
                    try { File.Delete(tmpFile); } catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ErrorPresenter.LogFallback($"自动安装 UWP 前置依赖 {name} 失败", ex);
                progress?.Report(new ProgressInfo($"{name} 安装失败：{ex.Message}", 1, 1, ""));
                failures.Add(name + "：" + ex.Message);
            }
        }
        if (failures.Count > 0) throw new InvalidOperationException("部分 UWP 前置框架包补全失败：" + string.Join("；", failures));
    }

    /// <summary>
    /// 从 store.rg-adguard.net 获取 UWP 框架包的下载直链（与 BedrockBoot 的
    /// UwpFileUrl 相同：按 PackageFamilyName 查，挑 x64/neutral 里版本最高的 appx）。
    /// </summary>
    public static async Task<string?> ResolveUwpPackageUrlAsync(string packageName, string? minVersion, CancellationToken ct = default)
    {
        var pfn = $"{packageName}_8wekyb3d8bbwe";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("Origin", "https://store.rg-adguard.net");
        client.DefaultRequestHeaders.Add("Referer", "https://store.rg-adguard.net/");

        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("type", "PackageFamilyName"),
            new KeyValuePair<string, string>("url", pfn),
            new KeyValuePair<string, string>("ring", "RP"),
            new KeyValuePair<string, string>("lang", "en-US"),
        });

        using var response = await client.PostAsync("https://store.rg-adguard.net/api/GetFiles", content, ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);

        var regex = new Regex(@"<a\s+href=""([^""]+)""[^>]*>([^<]+\.(?:appx|appxbundle|msixbundle|msix))</a>",
            RegexOptions.IgnoreCase);
        var matches = regex.Matches(html);

        string? bestUrl = null;
        string? bestVersion = null;

        foreach (Match match in matches)
        {
            var url = match.Groups[1].Value;
            var fileName = match.Groups[2].Value.ToLowerInvariant();

            // 只选择 x64 或 neutral 架构
            if (!fileName.Contains("x64") && !fileName.Contains("neutral"))
                continue;

            var versionMatch = Regex.Match(fileName, @"(\d+\.\d+\.\d+\.\d+)");
            var version = versionMatch.Success ? versionMatch.Groups[1].Value : null;

            // 选择最高版本
            if (bestUrl == null || CompareUwpVersions(version, bestVersion) > 0)
            {
                bestUrl = url;
                bestVersion = version;
            }
        }

        // 检查版本要求
        if (!string.IsNullOrEmpty(minVersion) && CompareUwpVersions(bestVersion, minVersion) < 0)
            return null;

        return bestUrl;
    }

    private static int CompareUwpVersions(string? v1, string? v2)
    {
        if (v1 == v2) return 0;
        if (v1 == null) return -1;
        if (v2 == null) return 1;

        var p1 = v1.Split('.').Select(int.Parse).ToArray();
        var p2 = v2.Split('.').Select(int.Parse).ToArray();

        for (int i = 0; i < Math.Max(p1.Length, p2.Length); i++)
        {
            int n1 = i < p1.Length ? p1[i] : 0;
            int n2 = i < p2.Length ? p2[i] : 0;
            if (n1 != n2) return n1.CompareTo(n2);
        }
        return 0;
    }

    private static void ParseAppxPackageCsv(string csvOutput, Dictionary<string, string> installedPackages)
    {
        var lines = csvOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return;

        // 跳过表头
        var startIndex = 0;
        if (lines[0].Contains("Name") && lines[0].Contains("Version"))
            startIndex = 1;

        for (var i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = ParseCsvLine(line);
            if (parts.Count >= 2)
            {
                var name = parts[0].Trim('"');
                var version = parts[1].Trim('"');
                if (string.IsNullOrEmpty(name)) continue;

                // 同包名保留最高版本
                if (installedPackages.TryGetValue(name, out var existingVersion))
                {
                    if (CompareUwpVersions(version, existingVersion) > 0)
                        installedPackages[name] = version;
                }
                else
                {
                    installedPackages[name] = version;
                }
            }
        }
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result;
    }

    private static async Task<(int ExitCode, string Output)> RunPowershellAsync(string arguments,
        CancellationToken ct = default, bool isInstaller = false)
    {
        ct.ThrowIfCancellationRequested();
        var item = DownloadQueueService.Instance.CurrentItem;
        return isInstaller && item != null
            ? await item.RunInstallerPhaseAsync(RunProcessAsync)
            : await RunProcessAsync();

        async Task<(int ExitCode, string Output)> RunProcessAsync()
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return (1, "无法启动 PowerShell");

            // 两路同时读取，避免大量 stderr 填满管道后互相等待。
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(isInstaller ? CancellationToken.None : ct);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                await process.WaitForExitAsync();
                await Task.WhenAll(output, error);
                throw;
            }
            await Task.WhenAll(output, error);
            ct.ThrowIfCancellationRequested();
            return (process.ExitCode, (await output) + (await error));
        }
    }
}
