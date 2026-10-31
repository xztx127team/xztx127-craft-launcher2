using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

public class ClientLoaderInstallService : IDisposable
{
    /// <summary>安装过程中一确定将要写入的版本 ID 就触发，让下载队列能提前拦住对该版本的启动。</summary>
    public event Action<string>? InstallationTargetResolved;

    private static string? ResolveVersionFile(string dir, string preferredBaseName, string extension)
    {
        var exact = Path.Combine(dir, $"{preferredBaseName}.{extension}");
        if (File.Exists(exact)) return exact;
        if (!Directory.Exists(dir)) return null;
        var matches = Directory.GetFiles(dir, $"*.{extension}");
        return matches.Length == 1 ? matches[0] : null;
    }

    private const string FabricMetaBase = "https://meta.fabricmc.net/v2";

    private async Task<VersionDetail> GetVanillaDetailAsync(string mcVersion, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        VersionInheritanceResolver.ValidateId(mcVersion);
        progress?.Report(new ProgressInfo("读取完整原版版本信息", 0, 1, mcVersion));
        var manifest = await _vanillaDownloader.GetVersionManifestAsync(ct);
        var entry = manifest.Versions.FirstOrDefault(version => version.Id == mcVersion)
            ?? throw new InvalidDataException($"在版本清单中找不到 Minecraft {mcVersion}，无法补全加载器依赖。");
        var json = await DownloadEndpoints.GetStringWithFallbackAsync(_http, entry.Url, _preferMirror,
            $"无法获取 Minecraft {mcVersion} 的完整版本信息。", ct);
        var detail = JsonSerializer.Deserialize<VersionDetail>(json)
            ?? throw new InvalidDataException("原版版本信息为空。");
        if (detail.Id != mcVersion || !string.IsNullOrWhiteSpace(detail.InheritsFrom) || !string.IsNullOrWhiteSpace(detail.Jar))
            throw new InvalidDataException("原版版本信息与所选 Minecraft 版本不一致。");
        return detail;
    }

    private async Task InstallStandaloneLoaderProfileAsync(string minecraftDir, string versionId, string mcVersion,
        string loader, string loaderVersion, VersionDetail loaderProfile, IProgress<ProgressInfo>? progress,
        CancellationToken ct, string? architecture = null, string? existingJsonPath = null)
    {
        VersionInheritanceResolver.ValidateId(versionId);
        var versionDir = ResourceDownloadPath.ResolveChild(Path.Combine(minecraftDir, "versions"), versionId);
        var vanilla = await GetVanillaDetailAsync(mcVersion, progress, ct);
        var complete = StandaloneLoaderProfile.Compose(minecraftDir, versionId, mcVersion, loader, loaderVersion, vanilla, loaderProfile);
        var plan = StandaloneLoaderProfile.CreateRepairPlan(minecraftDir, complete, architecture);
        // Run every verification stage even when a client JAR already exists: a JAR alone does not
        // prove that the original libraries, natives, assets or logging configuration are installed.
        await _vanillaDownloader.DownloadClientJarOnlyAsync(minecraftDir, plan.Client, progress, ct,
            targetVersionId: plan.ClientVersionId, targetJarBaseName: plan.ClientJarBaseName);
        await _vanillaDownloader.DownloadLibrariesOnlyAsync(minecraftDir, plan.Libraries, progress, ct, architecture);
        await _vanillaDownloader.DownloadAssetsOnlyAsync(minecraftDir, plan.Assets, progress, ct);
        ct.ThrowIfCancellationRequested();
        var jsonPath = existingJsonPath ?? ResourceDownloadPath.ResolveChild(versionDir, $"{versionId}.json");
        var json = JsonSerializer.Serialize(complete, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        // Publish only a complete profile. Failure/cancellation leaves the old profile available
        // for a retry, rather than publishing a launchable-looking JSON with missing dependencies.
        await DownloadTargetLock.WriteTextAsync(jsonPath, json, ct);
    }

    public static bool NeedsStandaloneProfileRepair(string minecraftDir, string versionId)
    {
        VersionInheritanceResolver.ValidateId(versionId);
        var directory = ResourceDownloadPath.ResolveChild(Path.Combine(minecraftDir, "versions"), versionId);
        var jsonPath = FolderService.ResolveVersionJson(directory, versionId);
        if (jsonPath == null) return false;
        ArchiveImportSafety.CheckLinks(jsonPath);
        var profile = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(jsonPath));
        return profile != null && StandaloneLoaderProfile.GetRepairTarget(profile) != null;
    }

    /// <summary>Repairs the old flattened Fabric/Quilt profile defect without renaming the instance or changing its loader version.</summary>
    public async Task<bool> RepairIncompleteStandaloneProfileAsync(string minecraftDir, string versionId,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default, string? architecture = null)
    {
        VersionInheritanceResolver.ValidateId(versionId);
        var directory = ResourceDownloadPath.ResolveChild(Path.Combine(minecraftDir, "versions"), versionId);
        var jsonPath = FolderService.ResolveVersionJson(directory, versionId);
        if (jsonPath == null) return false;
        ArchiveImportSafety.CheckLinks(jsonPath);
        var profile = JsonSerializer.Deserialize<VersionDetail>(await File.ReadAllTextAsync(jsonPath, ct));
        var target = profile == null ? null : StandaloneLoaderProfile.GetRepairTarget(profile);
        if (target == null) return false;
        InstallationTargetResolved?.Invoke(versionId);
        progress?.Report(new ProgressInfo("补全旧加载器实例的原版依赖", 0, 1, versionId));
        await InstallStandaloneLoaderProfileAsync(minecraftDir, versionId, target.MinecraftVersion,
            target.Loader, target.LoaderVersion, profile!, progress, ct, architecture, jsonPath);
        ct.ThrowIfCancellationRequested();
        return true;
    }

    private static VersionDetail ReadForgeInstallerProfile(string installerPath)
    {
        using var archive = ZipFile.OpenRead(installerPath);
        var entry = archive.GetEntry("version.json") ?? archive.GetEntry("install_profile.json")
            ?? throw new InvalidDataException("安装器缺少客户端版本信息，无法安全确定安装目标。");
        if (entry.Length > 2 * 1024 * 1024) throw new InvalidDataException("安装器版本信息过大。");
        using var document = JsonDocument.Parse(ArchiveImportSafety.ReadManifest(entry));
        var root = document.RootElement;
        if (root.TryGetProperty("versionInfo", out var legacy)) root = legacy;
        var detail = root.Deserialize<VersionDetail>()
            ?? throw new InvalidDataException("安装器版本信息为空。");
        VersionInheritanceResolver.ValidateId(detail.Id);
        if (!string.IsNullOrWhiteSpace(detail.InheritsFrom)) VersionInheritanceResolver.ValidateId(detail.InheritsFrom);
        return detail;
    }

    private async Task CompleteInheritedInstallationAsync(string minecraftDir, string versionId,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var current = versionId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrWhiteSpace(current))
        {
            VersionInheritanceResolver.ValidateId(current);
            if (!visited.Add(current) || visited.Count > 64)
                throw new InvalidDataException("加载器版本继承链循环或过长。");
            InstallationTargetResolved?.Invoke(current);
            var directory = ResourceDownloadPath.ResolveChild(Path.Combine(minecraftDir, "versions"), current);
            var jsonPath = FolderService.ResolveVersionJson(directory, current);
            VersionDetail detail;
            if (jsonPath == null)
            {
                if (current == versionId) throw new InvalidDataException("安装器没有生成预期的客户端版本信息。");
                detail = await GetVanillaDetailAsync(current, progress, ct);
                jsonPath = ResourceDownloadPath.ResolveChild(directory, $"{current}.json");
                await DownloadTargetLock.WriteTextAsync(jsonPath, JsonSerializer.Serialize(detail), ct);
            }
            else
            {
                ArchiveImportSafety.CheckLinks(jsonPath);
                detail = JsonSerializer.Deserialize<VersionDetail>(await File.ReadAllTextAsync(jsonPath, ct))
                    ?? throw new InvalidDataException("安装器生成的版本信息为空。");
            }
            current = detail.InheritsFrom ?? "";
        }
        var resolution = VersionInheritanceResolver.Load(minecraftDir, versionId, FolderService.ResolveVersionJson);
        var plan = VersionRepairPlan.Create(resolution);
        InstallationTargetResolved?.Invoke(plan.ClientVersionId);
        await _vanillaDownloader.DownloadClientJarOnlyAsync(minecraftDir, plan.Client, progress, ct,
            targetVersionId: plan.ClientVersionId, targetJarBaseName: plan.ClientJarBaseName);
        var clientDirectory = ResourceDownloadPath.ResolveChild(Path.Combine(minecraftDir, "versions"), plan.ClientVersionId);
        var clientJar = ResolveVersionFile(clientDirectory, plan.ClientJarBaseName, "jar")
            ?? throw new InvalidDataException("安装器未生成完整的客户端主程序。");
        ArchiveImportSafety.CheckLinks(clientJar);
        using (var archive = ZipFile.OpenRead(clientJar))
            if (archive.Entries.Count == 0) throw new InvalidDataException("客户端主程序 JAR 为空。");
        await _vanillaDownloader.DownloadLibrariesOnlyAsync(minecraftDir, plan.Libraries, progress, ct);
        await _vanillaDownloader.DownloadAssetsOnlyAsync(minecraftDir, plan.Assets, progress, ct);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Quilt 官方 Meta API，接口形状(端点路径/返回字段)跟 Fabric Meta 几乎一一对应——
    /// Quilt 本来就是从 Fabric Loader fork 出来的，两边团队一直保持 Meta API 兼容，
    /// 这也是下面 Quilt 相关方法能直接照抄 Fabric 那几个方法、只换 base url 和产物文件名前缀的原因。</summary>
    private const string QuiltMetaBase = "https://meta.quiltmc.org/v3";
    private const string ForgeMavenBase = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string NeoForgeMavenBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge";

    private readonly HttpClient _http = LauncherHttpClientFactory.Create(TimeSpan.FromMinutes(15));
    private readonly DownloadService _vanillaDownloader;

    /// <summary>用户在设置里选的下载源，供 DownloadFileNoHashCheckAsync/post-install 库补全复用
    /// DownloadEndpoints 的镜像回退逻辑（构造函数里保存一份，避免每处都要多传一个参数）。</summary>
    private readonly bool _preferMirror;
    private readonly AppConfig _maintenanceConfig;

    /// <summary>Fabric API 走 Modrinth 下载（Fabric API 本身就是发布在 Modrinth 上的普通 mod，
    /// 复用现成的 ModrinthService 搜索+下载逻辑，不用重新实现一遍 Modrinth API 调用。</summary>
    private readonly ModrinthService _modrinth = new();

    /// <summary>按完整 AppConfig 构造：加载器安装同样需要先装一个原版底座（父版本），
    /// 走的正是内部这份 _vanillaDownloader，理应享受跟"下载中心-游戏版本"面板一样的
    /// 多线程下载/限速配置，而不是永远单线程——否则用户开了多线程下载，唯独装 Fabric/Forge 时
    /// 感觉不到任何加速，体验不一致。</summary>
    public ClientLoaderInstallService(AppConfig cfg)
        : this(cfg.Source)
    {
        _vanillaDownloader.Dispose();
        _vanillaDownloader = DownloadService.CreateFromConfig(cfg);
        _maintenanceConfig = cfg;
    }

    /// <summary>
    /// 「一键开始游戏」用的加速构造：内部的原版底座下载器强制走镜像源 + 多线程，
    /// 理由见 <see cref="DownloadService.CreateAccelerated"/>。<paramref name="accelerate"/>
    /// 为 false 时行为跟上面那个构造完全一致，方便调用方用一个布尔量开关，
    /// 不用在两个构造之间写 if/else。
    /// </summary>
    public ClientLoaderInstallService(AppConfig cfg, bool accelerate)
        : this(accelerate ? DownloadSource.BMCLAPI : cfg.Source)
    {
        _vanillaDownloader.Dispose();
        _vanillaDownloader = accelerate
            ? DownloadService.CreateAccelerated(cfg)
            : DownloadService.CreateFromConfig(cfg);
        _maintenanceConfig = cfg;
    }

    /// <summary>沿用只传 DownloadSource 的签名；下载器读取当前多线程和限速设置，
    /// 不会因旧调用方未传完整 AppConfig 而退化为单线程。</summary>
    public ClientLoaderInstallService(DownloadSource source)
    {
        _vanillaDownloader = new DownloadService(source);
        _preferMirror = source != DownloadSource.Official;
        _maintenanceConfig = new AppConfig { Source = source };
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("XCL2Launcher", "1.0"));
    }

    /// <summary>Fabric：客户端支持的 MC 版本列表（与服务端是同一份数据源）。</summary>
    public async Task<List<string>> GetFabricMcVersionsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{FabricMetaBase}/versions/game", ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<string>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            if (v.TryGetProperty("stable", out var stable) && !stable.GetBoolean()) continue;
            result.Add(v.GetProperty("version").GetString()!);
        }
        return result;
    }

    public async Task<List<ServerCoreBuild>> GetFabricLoaderVersionsAsync(string? mcVersion = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(mcVersion)
            ? $"{FabricMetaBase}/versions/loader"
            : $"{FabricMetaBase}/versions/loader/{Uri.EscapeDataString(mcVersion.Trim())}";

        string json;
        try
        {
            json = await _http.GetStringAsync(url, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 该 MC 版本在 Fabric 那边压根没有任何 loader 交集——直接给一句人话，
            // 而不是把 "404 (Not Found)" 原样甩给用户。
            throw new InvalidOperationException(
                $"Fabric 目前还没有为 Minecraft {mcVersion} 发布 Loader。\n" +
                "通常是因为该版本太新（Fabric 还没跟进）或太老（已停止支持）。" +
                "可以换一个 MC 版本，或者过几天等 Fabric 更新后再试。", ex);
        }

        using var doc = JsonDocument.Parse(json);
        var result = new List<ServerCoreBuild>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            // 按游戏版本查交集时，返回的每一项形如 { "loader": {...}, "intermediary": {...} }，
            // 真正的 loader 信息在 "loader" 子对象里；全量列表则是平铺的。两种形状都兼容。
            var node = v.TryGetProperty("loader", out var loaderNode) ? loaderNode : v;
            if (!node.TryGetProperty("version", out var verEl)) continue;

            result.Add(new ServerCoreBuild
            {
                DisplayVersion = verEl.GetString()!,
                IsRecommended = node.TryGetProperty("stable", out var stable) && stable.GetBoolean()
            });
        }

        if (result.Count == 0)
            throw new InvalidOperationException(
                $"Fabric 没有返回任何适用于 Minecraft {mcVersion} 的 Loader 版本，无法继续安装。");

        return result;
    }

    /// <summary>Quilt：客户端支持的 MC 版本列表。跟 GetFabricMcVersionsAsync 是同一套过滤逻辑
    /// (只保留 stable=true 的正式版)，Quilt Meta 的 /versions/game 返回结构跟 Fabric Meta 一致。</summary>
    public async Task<List<string>> GetQuiltMcVersionsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{QuiltMetaBase}/versions/game", ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<string>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            if (v.TryGetProperty("stable", out var stable) && !stable.GetBoolean()) continue;
            result.Add(v.GetProperty("version").GetString()!);
        }
        return result;
    }

    public async Task<List<ServerCoreBuild>> GetQuiltLoaderVersionsAsync(string? mcVersion = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(mcVersion)
            ? $"{QuiltMetaBase}/versions/loader"
            : $"{QuiltMetaBase}/versions/loader/{Uri.EscapeDataString(mcVersion.Trim())}";

        string json;
        try
        {
            json = await _http.GetStringAsync(url, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Quilt 目前还没有为 Minecraft {mcVersion} 发布 Loader。\n" +
                "可以换一个 MC 版本，或者过几天等 Quilt 更新后再试。", ex);
        }

        using var doc = JsonDocument.Parse(json);
        var result = new List<ServerCoreBuild>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            // 同 Fabric：按游戏版本查交集时条目是 { "loader": {...}, "intermediary": {...} }，
            // 全量列表时是平铺的。两种形状都要认，否则按版本查回来的数据会全部取不到 "version"
            // 而抛 KeyNotFoundException（表现同样是"安装失败"，只是异常类型不同）。
            var node = v.TryGetProperty("loader", out var loaderNode) ? loaderNode : v;
            if (!node.TryGetProperty("version", out var verEl)) continue;
            var ver = verEl.GetString()!;

            result.Add(new ServerCoreBuild
            {
                DisplayVersion = ver,
                // Quilt Meta 的 loader 列表条目本身不像 Fabric 那样带 "stable" 字段，
                // 官方约定"不含 -beta/-rc 等预发布后缀的版本号"即视为稳定版，
                // 用字符串是否包含连字符来判断，跟 Quilt 官方文档/其它启动器(PCL2/HMCL)
                // 采用的判断口径一致。
                IsRecommended = !ver.Contains('-')
            });
        }

        if (result.Count == 0)
            throw new InvalidOperationException(
                $"Quilt 没有返回任何适用于 Minecraft {mcVersion} 的 Loader 版本，无法继续安装。");

        return result;
    }

    /// <summary>Legacy Fabric 自己维护的 Meta API，接口形状(端点路径/返回字段)跟官方 Fabric Meta
    /// 完全一致——这是专门给 1.13 之前老版本(官方 Fabric 早已不再兼容)做的独立分支项目，
    /// 客户端安装的整体流程复用 InstallLegacyFabricClientAsync，跟 InstallFabricClientAsync
    /// 除了 base url 和不提供"Fabric API"该可选项以外几乎一致。</summary>
    private const string LegacyFabricMetaBase = "https://meta.legacyfabric.net/v2";

    /// <summary>Legacy Fabric：客户端支持的 MC 版本列表。</summary>
    public async Task<List<string>> GetLegacyFabricMcVersionsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{LegacyFabricMetaBase}/versions/game", ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<string>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            if (v.TryGetProperty("version", out var ver) && ver.GetString() is { } s)
                result.Add(s);
        }
        return result;
    }

    /// <summary>Legacy Fabric：某个 MC 版本下可用的 loader 版本列表，跟
    /// GetFabricLoaderVersionsAsync 同样按游戏版本查交集端点，同样把 404 翻译成人话。</summary>
    public async Task<List<ServerCoreBuild>> GetLegacyFabricLoaderVersionsAsync(string mcVersion, CancellationToken ct = default)
    {
        var url = $"{LegacyFabricMetaBase}/versions/loader/{Uri.EscapeDataString(mcVersion.Trim())}";
        string json;
        try
        {
            json = await _http.GetStringAsync(url, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Legacy Fabric 目前还没有为 Minecraft {mcVersion} 发布 Loader。\n" +
                "Legacy Fabric 主要覆盖 1.13 之前的老版本，可以换一个 MC 版本再试。", ex);
        }

        using var doc = JsonDocument.Parse(json);
        var result = new List<ServerCoreBuild>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            var node = v.TryGetProperty("loader", out var loaderNode) ? loaderNode : v;
            if (!node.TryGetProperty("version", out var verEl)) continue;
            var ver = verEl.GetString();
            if (string.IsNullOrEmpty(ver)) continue;
            result.Add(new ServerCoreBuild
            {
                DisplayVersion = ver,
                IsRecommended = node.TryGetProperty("stable", out var stable) && stable.GetBoolean()
            });
        }

        if (result.Count == 0)
            throw new InvalidOperationException(
                $"Legacy Fabric 没有返回任何适用于 Minecraft {mcVersion} 的 Loader 版本，无法继续安装。");
        if (!result.Any(r => r.IsRecommended)) result[0].IsRecommended = true;

        return result;
    }

    public async Task<string> InstallLegacyFabricClientAsync(string minecraftDir, string mcVersion, string loaderVersion,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default, string? customInstanceName = null)
    {
        loaderVersion = await ResolveLoaderVersionOrAutoMatchAsync(
            LegacyFabricMetaBase, "Legacy Fabric", mcVersion, loaderVersion, progress, ct);

        var profileUrl = ClientLoaderProfileEndpoints.ClientProfile(LegacyFabricMetaBase, mcVersion, loaderVersion);
        progress?.Report(new ProgressInfo("下载 Legacy Fabric 版本信息", 0, 1, "profile/json"));
        var profileJson = await GetStringWithFriendly404Async(profileUrl,
            $"Legacy Fabric 没有 \"Minecraft {mcVersion} + Loader {loaderVersion}\" 该组合的安装信息。\n" +
            "可以在「Loader 版本」下拉框里换一个，或者换一个 MC 版本再试。", ct);

        var detail = JsonSerializer.Deserialize<VersionDetail>(profileJson)
            ?? throw new InvalidOperationException("Legacy Fabric 返回的版本信息解析失败。");

        var defaultVersionId = string.IsNullOrEmpty(detail.Id) ? $"legacyfabric-loader-{loaderVersion}-{mcVersion}" : detail.Id;
        var versionId = string.IsNullOrWhiteSpace(customInstanceName)
            ? defaultVersionId
            : ModpackInstallService.MakeUniqueInstanceName(minecraftDir, customInstanceName);
        VersionInheritanceResolver.ValidateId(versionId);
        InstallationTargetResolved?.Invoke(versionId);
        await InstallStandaloneLoaderProfileAsync(minecraftDir, versionId, mcVersion, "Legacy Fabric",
            loaderVersion, detail, progress, ct);

        ct.ThrowIfCancellationRequested();
        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Installation_Complete", "安装完成"), 1, 1, versionId));
        return versionId;
    }

    public Task<List<string>> GetForgeVersionsAsync(CancellationToken ct = default)
        => ForgeVersionQueryService.GetForgeVersionsAsync(_http, ct);

    public Task<List<ServerCoreBuild>> GetForgeInstallerVersionsAsync(string mcVersion, CancellationToken ct = default)
        => ForgeVersionQueryService.GetForgeInstallerVersionsAsync(_http, mcVersion, ct);

    public Task<List<string>> GetNeoForgeVersionsAsync(CancellationToken ct = default)
        => ForgeVersionQueryService.GetNeoForgeVersionsAsync(_http, ct);

    /// <summary>bangbang93 维护的 BMCLAPI，除了给官方 Mojang/Fabric/Forge 等接口当镜像
    /// （见 DownloadEndpoints.BmclMap）以外，还额外提供了几个"官方本身没有可编程接口"的专属数据源，
    /// OptiFine 就是其中之一：optifine.net 的下载页有人机验证挡着，没有公开、可直接程序化调用的
    /// 官方列表/下载接口，BMCLAPI 是社区事实上唯一可靠的 OptiFine 数据源，这里直接对接它，
    /// 不走 DownloadEndpoints 的"官方地址 + 镜像回退"两段式（因为压根没有对应的官方地址可填）。</summary>
    private const string BmclApiBase = "https://bmclapi2.bangbang93.com";

    /// <summary>
    /// OptiFine：查询某个 MC 版本下所有可用构建（BMCLAPI 的 /optifine/{mcVersion} 接口）。
    /// 返回的 DisplayVersion 形如 "HD_U_I6"（type_patch 拼在一起），跟官网下载页上看到的编号一致，
    /// 方便用户跟着教程/记忆去选对应的版本。BMCLAPI 上没有该 MC 版本对应构建时返回空列表
    /// （不算错误——很多版本 OptiFine 官方压根没发布过），调用方按空列表处理成"该版本不支持"。
    /// </summary>
    public async Task<List<ServerCoreBuild>> GetOptiFineVersionsAsync(string mcVersion, CancellationToken ct = default)
    {
        string json;
        try
        {
            json = await _http.GetStringAsync(
                $"{BmclApiBase}/optifine/{Uri.EscapeDataString(mcVersion.Trim())}", ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new List<ServerCoreBuild>();
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<ServerCoreBuild>();

        var result = new List<ServerCoreBuild>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            var type = v.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var patch = v.TryGetProperty("patch", out var p) ? p.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(type) && string.IsNullOrEmpty(patch)) continue;
            result.Add(new ServerCoreBuild
            {
                DisplayVersion = string.IsNullOrEmpty(patch) ? type : $"{type}_{patch}",
                IsRecommended = false,
                RawIdentifier = $"{type}|{patch}"
            });
        }
        // BMCLAPI 按发布顺序（旧到新）给的，最新的一般是用户最想要的默认项，翻转成新到旧，
        // 跟 Fabric/Forge 那几个列表"最新排最前"的展示习惯保持一致。
        result.Reverse();
        if (result.Count > 0) result[0].IsRecommended = true;
        return result;
    }

    /// <summary>Fabric API 在 Modrinth 上的项目 slug，固定值（官方项目，不会变）。</summary>
    private const string FabricApiModrinthSlug = "fabric-api";

    /// <summary>
    /// GetStringAsync 的包装：把 HTTP 404 翻译成一句用户能看懂的话。
    ///
    /// 之前所有 Meta 请求都是裸的 _http.GetStringAsync，一旦 404 就直接把
    /// "Response status code does not indicate success: 404 (Not Found)." 连同整个
    /// 调用栈抛到界面上——用户完全无法从这句话判断"是我选错了版本组合"还是"网炸了"。
    /// 其它状态码（超时/5xx/DNS 失败）保持原样抛出，交给上层的
    /// ErrorPresenter.ShowFriendlyError 统一按"网络问题"处理，这里只专门处理 404，
    /// 因为只有 404 才明确对应"该组合不存在"这一种业务含义。
    /// </summary>
    private async Task<string> GetStringWithFriendly404Async(string url, string friendlyMessage, CancellationToken ct)
    {
        try
        {
            return await _http.GetStringAsync(url, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(friendlyMessage, ex);
        }
    }

    private async Task<string> ResolveLoaderVersionOrAutoMatchAsync(string metaBase, string loaderLabel,
        string mcVersion, string requestedLoaderVersion, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var url = $"{metaBase}/versions/loader/{Uri.EscapeDataString(mcVersion.Trim())}";
        string json;
        try
        {
            json = await _http.GetStringAsync(url, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 该 MC 版本在该加载器那边压根没有任何交集条目，自动匹配也无从谈起，
            // 直接给出跟 GetFabricLoaderVersionsAsync 一致的友好提示。
            throw new InvalidOperationException(
                $"{loaderLabel} 目前还没有为 Minecraft {mcVersion} 发布 Loader，无法安装。\n" +
                "可以换一个 MC 版本，或者过几天等官方更新后再试。", ex);
        }

        using var doc = JsonDocument.Parse(json);
        // (版本号, 是否稳定版) 的有序列表——Meta 接口本身就按新到旧排序，这里保持原始顺序，
        // "取第一个稳定版" 天然就是"支持该 MC 版本的最高稳定版"，不需要额外做版本号比较。
        var candidates = new List<(string Version, bool Stable)>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            var node = v.TryGetProperty("loader", out var loaderNode) ? loaderNode : v;
            if (!node.TryGetProperty("version", out var verEl)) continue;
            var ver = verEl.GetString();
            if (string.IsNullOrEmpty(ver)) continue;
            var stable = node.TryGetProperty("stable", out var stableEl) && stableEl.GetBoolean();
            candidates.Add((ver, stable));
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"{loaderLabel} 没有返回任何适用于 Minecraft {mcVersion} 的 Loader 版本，无法继续安装。");

        if (candidates.Any(c => c.Version == requestedLoaderVersion))
            return requestedLoaderVersion;

        var fallback = candidates.FirstOrDefault(c => c.Stable).Version ?? candidates[0].Version;
        progress?.Report(new ProgressInfo(
            $"{loaderLabel} Loader {requestedLoaderVersion} 不支持 Minecraft {mcVersion}，" +
            $"已自动改用兼容的最高版本 {fallback}",
            0, 1, fallback));
        return fallback;
    }

    public async Task<string> InstallFabricClientAsync(string minecraftDir, string mcVersion, string loaderVersion,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default, bool installFabricApi = false,
        string? customInstanceName = null)
    {

        loaderVersion = await ResolveLoaderVersionOrAutoMatchAsync(
            FabricMetaBase, "Fabric", mcVersion, loaderVersion, progress, ct);

        // Client profile/json takes game + loader only. The installer-version route is server/jar.
        var profileUrl = ClientLoaderProfileEndpoints.ClientProfile(FabricMetaBase, mcVersion, loaderVersion);
        progress?.Report(new ProgressInfo("下载 Fabric 版本信息", 0, 1, "profile/json"));
        var profileJson = await GetStringWithFriendly404Async(profileUrl,
            $"Fabric 没有 \"Minecraft {mcVersion} + Loader {loaderVersion}\" 该组合的安装信息。\n" +
            "多半是该 Loader 版本并不支持所选的 MC 版本。请在「Loader 版本」下拉框里换一个" +
            "（列表现在只会列出确实支持当前 MC 版本的 Loader），或者换一个 MC 版本再试。", ct);

        var detail = JsonSerializer.Deserialize<VersionDetail>(profileJson)
            ?? throw new InvalidOperationException("Fabric 返回的版本信息解析失败。");

        // Fabric Meta 返回的 json 里 id 字段形如 "fabric-loader-0.15.11-1.20.1"，直接采用官方给的命名，
        // 与其它主流启动器保持一致，方便用户在多个启动器之间识别是同一个版本。
        // 用户自定义了实例名时优先用用户给的名字（同「导入整合包」一样做合法化 + 去重），
        // 不传就还是原来的默认格式，行为不变。
        var defaultVersionId = string.IsNullOrEmpty(detail.Id) ? $"fabric-loader-{loaderVersion}-{mcVersion}" : detail.Id;
        var versionId = string.IsNullOrWhiteSpace(customInstanceName)
            ? defaultVersionId
            : ModpackInstallService.MakeUniqueInstanceName(minecraftDir, customInstanceName);
        VersionInheritanceResolver.ValidateId(versionId);
        InstallationTargetResolved?.Invoke(versionId);
        await InstallStandaloneLoaderProfileAsync(minecraftDir, versionId, mcVersion, "Fabric",
            loaderVersion, detail, progress, ct);

        // 用户勾选后，API 属于此次安装的必需产物；下载失败必须让队列显示失败。
        if (installFabricApi)
        {
            progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading_Fabric_Api", "下载 Fabric API"), 0, 1, FabricApiModrinthSlug));
            try
            {
                var versions = await _modrinth.GetVersionsAsync(FabricApiModrinthSlug, mcVersion, ct);
                // Fabric API 的 Modrinth 版本列表里同时有给 Fabric 用的和给 Quilt 用的构建，
                // 用 loaders 字段过滤，避免装到 Quilt 专用构建（能下载但 Fabric Loader 用不了）。
                var apiVersion = versions.FirstOrDefault(v =>
                    v.Loaders != null && v.Loaders.Any(l => l.Equals("fabric", StringComparison.OrdinalIgnoreCase)));

                if (apiVersion == null)
                {
                    throw new InvalidOperationException($"已选择安装 Fabric API，但没有找到适配 Minecraft {mcVersion} 的 fabric 版本。请更换游戏版本或取消该选项后重试。");
                }
                else
                {
                    var apiProgress = new Progress<string>(msg =>
                        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading_Fabric_Api", "下载 Fabric API"), 0, 1, msg)));
                    await _modrinth.DownloadResourceAsync(minecraftDir, ModrinthResourceType.Mod, apiVersion,
                        apiProgress, saveName: null, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not Plugins.PluginFlowCanceledException)
            {
                throw new InvalidOperationException($"已选组件 Fabric API 安装失败：{ex.Message}。已完成的游戏文件会保留，可以重试。", ex);
            }
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Installation_Complete", "安装完成"), 1, 1, versionId));
        return versionId;
    }

    /// <summary>QSL(Quilt Standard Libraries) 在 Modrinth 上的项目 slug，固定值（官方项目，不会变）。</summary>
    private const string QslModrinthSlug = "qsl";

    public async Task<string> InstallQuiltClientAsync(string minecraftDir, string mcVersion, string loaderVersion,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default, bool installQsl = false,
        string? customInstanceName = null)
    {
        // 1.5 同 InstallFabricClientAsync：校验 loaderVersion 是否真的支持该 mcVersion，
        //     不支持就自动换成兼容的最高版本，避免下面拼 profile url 时 404。
        loaderVersion = await ResolveLoaderVersionOrAutoMatchAsync(
            QuiltMetaBase, "Quilt", mcVersion, loaderVersion, progress, ct);

        var profileUrl = $"{QuiltMetaBase}/versions/loader/" +
                         $"{Uri.EscapeDataString(mcVersion)}/" +
                         $"{Uri.EscapeDataString(loaderVersion)}/profile/json";
        progress?.Report(new ProgressInfo("下载 Quilt 版本信息", 0, 1, "profile/json"));
        var profileJson = await GetStringWithFriendly404Async(profileUrl,
            $"Quilt 没有 \"Minecraft {mcVersion} + Loader {loaderVersion}\" 该组合的安装信息。\n" +
            "请在「Loader 版本」下拉框里换一个，或者换一个 MC 版本再试。", ct);

        var detail = JsonSerializer.Deserialize<VersionDetail>(profileJson)
            ?? throw new InvalidOperationException("Quilt 返回的版本信息解析失败。");

        // Quilt Meta 返回的 json 里 id 字段形如 "quilt-loader-0.24.0-1.20.1"，直接采用官方给的命名。
        // 命名优先级同 InstallFabricClientAsync：用户自定义优先，不传退回默认格式。
        var defaultVersionId = string.IsNullOrEmpty(detail.Id) ? $"quilt-loader-{loaderVersion}-{mcVersion}" : detail.Id;
        var versionId = string.IsNullOrWhiteSpace(customInstanceName)
            ? defaultVersionId
            : ModpackInstallService.MakeUniqueInstanceName(minecraftDir, customInstanceName);
        VersionInheritanceResolver.ValidateId(versionId);
        InstallationTargetResolved?.Invoke(versionId);
        await InstallStandaloneLoaderProfileAsync(minecraftDir, versionId, mcVersion, "Quilt",
            loaderVersion, detail, progress, ct);

        // 用户勾选后，QSL 属于此次安装的必需产物；下载失败必须让队列显示失败。
        if (installQsl)
        {
            progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading_Qsl", "下载 QSL"), 0, 1, QslModrinthSlug));
            try
            {
                var versions = await _modrinth.GetVersionsAsync(QslModrinthSlug, mcVersion, ct);
                // QSL 的 Modrinth 版本列表理论上只发布 Quilt 构建，但保险起见仍按 loaders 字段过滤，
                // 跟 Fabric API 那边的处理方式保持一致，避免装到不兼容的构建。
                var qslVersion = versions.FirstOrDefault(v =>
                    v.Loaders != null && v.Loaders.Any(l => l.Equals("quilt", StringComparison.OrdinalIgnoreCase)));

                if (qslVersion == null)
                {
                    throw new InvalidOperationException($"已选择安装 QSL，但没有找到适配 Minecraft {mcVersion} 的 quilt 版本。请更换游戏版本或取消该选项后重试。");
                }
                else
                {
                    var qslProgress = new Progress<string>(msg =>
                        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Downloading_Qsl", "下载 QSL"), 0, 1, msg)));
                    await _modrinth.DownloadResourceAsync(minecraftDir, ModrinthResourceType.Mod, qslVersion,
                        qslProgress, saveName: null, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not Plugins.PluginFlowCanceledException)
            {
                throw new InvalidOperationException($"已选组件 QSL 安装失败：{ex.Message}。已完成的游戏文件会保留，可以重试。", ex);
            }
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Installation_Complete", "安装完成"), 1, 1, versionId));
        return versionId;
    }

    public async Task<string> InstallForgeOrNeoForgeClientAsync(string minecraftDir, ServerCoreType coreType,
        string fullVersion, string javaExePath, IProgress<ProgressInfo>? progress, CancellationToken ct = default,
        string? customInstanceName = null)
    {
        if (coreType is not (ServerCoreType.Forge or ServerCoreType.NeoForge))
            throw new ArgumentException("只支持 Forge/NeoForge。", nameof(coreType));
        if (!string.IsNullOrWhiteSpace(customInstanceName)) InstallationTargetResolved?.Invoke(customInstanceName);
        if (!File.Exists(javaExePath))
            throw new FileNotFoundException("找不到可用的 Java，无法运行加载器安装器。", javaExePath);

        var mavenBase = coreType == ServerCoreType.Forge ? ForgeMavenBase : NeoForgeMavenBase;
        var prefix = coreType == ServerCoreType.Forge ? "forge" : "neoforge";
        var fileName = $"{prefix}-{fullVersion}-installer.jar";
        var url = $"{mavenBase}/{fullVersion}/{fileName}";

        // Forge/NeoForge 官方安装器是照搬 Mojang 官方启动器的行为写的，它在 --installClient 时会去读
        // <.minecraft>/launcher_profiles.json，如果该文件不存在就直接报错退出（"There is no Minecraft
        // launcher profile ... you need to run the launcher first!"，退出码 1），即使 .minecraft 目录本身
        // 已经存在、版本也已经装好也不例外——它只认这一个文件存在与否，不检查目录里是否已经有版本。
        // 因为 XCL2 是独立启动器，从来不会自己生成该 Mojang 专用的档案文件，所以只要是全新的
        // .minecraft 目录（或者是没被官方启动器打开过的目录），装 Forge/NeoForge 必现该报错。
        // 这里在跑安装器之前主动写一份能满足安装器"文件存在且是合法 JSON"要求的最小占位文件，
        // 不需要跟真实的 Mojang 官方启动器格式完全一致（安装器只检查文件存在性和基本 JSON 结构，
        // 不会真的把该当启动器状态使用），已存在的文件不覆盖，避免破坏用户可能真正用官方启动器
        // 生成过的档案数据。
        EnsureLauncherProfilesJson(minecraftDir);

        var tempDir = Path.Combine(Path.GetTempPath(), "xcl2-loader-installer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var installerPath = Path.Combine(tempDir, fileName);

        progress?.Report(new ProgressInfo($"下载 {coreType} 安装器", 0, 2, fileName));
        await DownloadFileNoHashCheckAsync(url, installerPath, ct);
        var installerProfile = ReadForgeInstallerProfile(installerPath);
        InstallationTargetResolved?.Invoke(installerProfile.Id);
        if (!string.IsNullOrWhiteSpace(installerProfile.InheritsFrom))
            InstallationTargetResolved?.Invoke(installerProfile.InheritsFrom);

        progress?.Report(new ProgressInfo("正在运行安装器（首次运行可能需要下载额外库文件）", 1, 2, fileName));
        var psi = new ProcessStartInfo
        {
            FileName = javaExePath,
            ArgumentList = { "-jar", installerPath, "--installClient", minecraftDir },
            WorkingDirectory = tempDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 显式指定 UTF-8：不指定时用的是 Console.OutputEncoding（中文 Windows 下通常是 GBK/936），
            // 而 Forge/NeoForge 安装器（以及它内部调起的 Java 子进程）打印的诊断信息很多是 UTF-8，
            // 编码不一致会导致失败时捕获到的"最后输出"全是乱码，报错信息形同虚设，用户完全看不懂
            // 到底哪里失败了——这是"Forge 安装报错但看不出原因"的一个常见诱因，跟安装器本身是否
            // 真的失败无关，是纯粹的输出编码问题。
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        ct.ThrowIfCancellationRequested();
        process.Start();

        var captured = await InstallerOutputCapture.WaitForExitAsync(process, TimeSpan.FromMinutes(10),
            $"{coreType} 客户端安装器", minecraftDir, null, ct);
        var outputLines = captured.Snapshot();

        if (process.ExitCode != 0)
        {
            var tail = string.Join('\n', outputLines.TakeLast(20));
            var fullOutput = string.Join('\n', outputLines);
            var hint = DiagnoseForgeInstallerFailure(fullOutput);
            throw new InvalidOperationException(
                $"{coreType} 客户端安装器执行失败（退出码 {process.ExitCode}）。{hint}最后输出：\n{tail}");
        }

        try { Directory.Delete(tempDir, recursive: true); } catch { /* 清理失败不影响安装已经完成该事实 */ }

        // Keep the installer's inheritance and patched-client semantics. Flattening only a few
        // fields loses vanilla libraries/arguments just as it did for Fabric.
        var versionId = installerProfile.Id;
        var candidate = ResourceDownloadPath.ResolveChild(Path.Combine(minecraftDir, "versions"), versionId);
        await CompleteInheritedInstallationAsync(minecraftDir, versionId, progress, ct);

        if (!string.IsNullOrWhiteSpace(customInstanceName))
        {
            var renamed = TryRenameInstalledInstance(minecraftDir, candidate, customInstanceName!);
            if (renamed != null)
            {
                InstallationTargetResolved?.Invoke(renamed);
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Installation_Complete", "安装完成"), 2, 2, renamed));
                return renamed;
            }
            if (!Directory.Exists(candidate) || FolderService.ResolveVersionJson(candidate, versionId) == null)
                throw new InvalidOperationException("加载器文件已下载，但实例重命名未完成，请检查版本目录后重试。");
            // 重命名失败（比如目标名冲突、文件被占用）不应该让整个安装被判定为失败——
            // 加载器本身已经装好、能用默认生成的名字正常启动，重命名只是锦上添花，
            // 大不了退回默认名字，游戏依然能用。
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Installation_Complete", "安装完成"), 2, 2, fileName));
        return versionId;
    }

    /// <summary>
    /// OptiFine 客户端安装。
    ///
    /// ===== 跟 Forge/NeoForge 的关键差异 =====
    /// Forge/NeoForge 官方安装器认识 "--installClient <dir>" 该命令行参数，可以完全无交互地跑；
    /// OptiFine 的安装器（optifine.net 下载到的那个 jar）没有对外文档化的命令行参数，双击运行只会弹出
    /// 一个 Swing 图形界面等人点"安装"按钮。为了在无人值守的情况下也能自动装完，这里用官方安装器
    /// 自身实现所依赖的补丁类 optifine.Patcher（负责"把 OptiFine 的补丁应用到原版 client.jar 上，
    /// 产出一份 OptiFine 化的库 jar"这一步核心逻辑，跟弹不弹图形界面无关，是安装器内部纯逻辑），
    /// 通过一段编译期生成的小型 Java 帮助类反射调用它，绕开图形界面。这跟 HMCL/PCL 等主流启动器
    /// 采用的技术路线一致。
    ///
    /// 该技术路线依赖本机 Java 是完整 JDK（需要 javac 编译帮助类），纯 JRE 环境下 javac 不存在，
    /// 会在下面 EnsureJavacAvailable 处提前失败并给出清晰提示——不静默假装装成功。
    /// </summary>
    public async Task<string> InstallOptiFineClientAsync(string minecraftDir, string mcVersion,
        string ofType, string ofPatch, string javaExePath, IProgress<ProgressInfo>? progress,
        CancellationToken ct = default, string? customInstanceName = null)
    {
        if (!File.Exists(javaExePath))
            throw new FileNotFoundException("找不到可用的 Java，无法安装 OptiFine。", javaExePath);

        // 1. 确保原版父版本已装好——OptiFine 是"往原版 client.jar 上打补丁"，没有原版 jar 无从谈起。
        VersionInheritanceResolver.ValidateId(mcVersion);
        var parentVersionDir = ResourceDownloadPath.ResolveChild(Path.Combine(minecraftDir, "versions"), mcVersion);
        var vanillaJarPath = ResolveVersionFile(parentVersionDir, mcVersion, "jar");
        if (vanillaJarPath == null)
        {
            InstallationTargetResolved?.Invoke(mcVersion);
            progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Installing_The_Base_Vanilla_Version", "安装原版父版本"), 0, 1, mcVersion));
            var manifest = await _vanillaDownloader.GetVersionManifestAsync(ct);
            var entry = manifest.Versions.FirstOrDefault(v => v.Id == mcVersion)
                ?? throw new InvalidOperationException($"在版本清单中找不到 MC 版本 {mcVersion}，无法安装 OptiFine 所需的原版父版本。");
            await _vanillaDownloader.InstallVersionAsync(minecraftDir, entry, progress, ct);
            vanillaJarPath = ResolveVersionFile(parentVersionDir, mcVersion, "jar");
        }
        if (vanillaJarPath == null)
            throw new InvalidOperationException("原版父版本安装完成，但找不到 client.jar，无法继续安装 OptiFine。");

        var javacExePath = Path.Combine(Path.GetDirectoryName(javaExePath) ?? "", OperatingSystem.IsWindows() ? "javac.exe" : "javac");
        if (!File.Exists(javacExePath))
            throw new InvalidOperationException(
                "自动安装 OptiFine 需要一份完整的 JDK（要用到 javac），当前选择的 Java 只是 JRE，缺少 javac。\n" +
                "请在「设置 - Java 管理」里换一个完整 JDK 后重试，或前往 OptiFine 官网手动下载安装。");

        var buildLabel = string.IsNullOrEmpty(ofPatch) ? ofType : $"{ofType}_{ofPatch}";
        var fileName = $"OptiFine_{mcVersion}_{buildLabel}.jar";
        var downloadUrl = $"{BmclApiBase}/optifine/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(ofType)}/{Uri.EscapeDataString(ofPatch)}";

        var tempDir = Path.Combine(Path.GetTempPath(), "xcl2-optifine-installer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var installerPath = Path.Combine(tempDir, fileName);

        progress?.Report(new ProgressInfo("下载 OptiFine 安装器（来自 BMCLAPI）", 0, 3, fileName));
        await DownloadFileNoHashCheckAsync(downloadUrl, installerPath, ct);

        // 2. 生成并编译一个几行代码的帮助类：反射调用安装器 jar 内的 optifine.Patcher.process，
        //    直接产出打好补丁的 OptiFine 库 jar，不弹图形界面、不需要人工点击。
        var helperSrc = Path.Combine(tempDir, "XCL2OptiFineHelper.java");
        var outputJarPath = Path.Combine(tempDir, "optifine-patched.jar");
        await File.WriteAllTextAsync(helperSrc, """
            import java.io.File;
            import java.lang.reflect.Method;
            public class XCL2OptiFineHelper {
                public static void main(String[] args) throws Exception {
                    // args: [0]=原版 client.jar  [1]=输出的补丁后 jar  [2]=OptiFine 安装器 jar 自身
                    Class<?> patcherClass = Class.forName("optifine.Patcher");
                    Method process = patcherClass.getMethod("process", File.class, File.class, File.class);
                    process.invoke(null, new File(args[0]), new File(args[1]), new File(args[2]));
                }
            }
            """, ct);

        progress?.Report(new ProgressInfo("正在准备 OptiFine 补丁工具", 1, 3, fileName));
        await RunProcessAsync(javacExePath,
            new[] { "-cp", installerPath, "-encoding", "UTF-8", helperSrc },
            tempDir, "编译 OptiFine 补丁工具失败", ct);

        progress?.Report(new ProgressInfo("正在给客户端打 OptiFine 补丁", 2, 3, fileName));
        var classpathSep = OperatingSystem.IsWindows() ? ";" : ":";
        await RunProcessAsync(javaExePath,
            new[] { "-cp", $".{classpathSep}{installerPath}", "XCL2OptiFineHelper", vanillaJarPath, outputJarPath, installerPath },
            tempDir, "运行 OptiFine 补丁工具失败（optifine.Patcher 反射调用异常）", ct);

        if (!File.Exists(outputJarPath) || new FileInfo(outputJarPath).Length == 0)
            throw new InvalidOperationException("OptiFine 补丁工具运行完成，但没有产出有效的补丁 jar，安装失败。");

        // 3. 组装成独立的 versions/ 实例：mainClass 固定是 OptiFine 自带的 launchwrapper 入口，
        //    libraries 里带上 launchwrapper 本体 + OptiFine 补丁库自身两条（都是 OptiFine 安装器
        //    自带、不需要联网单独下载的东西，直接复制进 libraries/ 目录）。
        var defaultVersionId = $"{mcVersion}-OptiFine_{buildLabel}";
        var versionId = string.IsNullOrWhiteSpace(customInstanceName)
            ? ModpackInstallService.MakeUniqueInstanceName(minecraftDir, defaultVersionId)
            : ModpackInstallService.MakeUniqueInstanceName(minecraftDir, customInstanceName);
        InstallationTargetResolved?.Invoke(versionId);
        var versionDir = Path.Combine(minecraftDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);

        File.Copy(outputJarPath, Path.Combine(versionDir, $"{versionId}.jar"), overwrite: true);

        var vanillaJsonPath = ResolveVersionFile(parentVersionDir, mcVersion, "json")
            ?? throw new InvalidOperationException("找不到原版 version json，无法生成 OptiFine 版本信息。");
        var vanillaDetail = JsonSerializer.Deserialize<VersionDetail>(await File.ReadAllTextAsync(vanillaJsonPath, ct))
            ?? throw new InvalidOperationException("原版 version json 解析失败。");

        vanillaDetail.Id = versionId;
        vanillaDetail.InheritsFrom = null;
        vanillaDetail.MainClass = "net.minecraft.launchwrapper.Launch";
        // launchwrapper 是运行 OptiFine 补丁 jar 必需的启动垫片，OptiFine 官方安装器一直自带、
        // 版本长期固定在 1.12，直接从 Maven Central 拉取即可，不依赖 BMCLAPI 专属数据。
        var launchwrapperLib = new LibraryEntry
        {
            Name = "net.minecraft:launchwrapper:1.12",
            Downloads = new LibraryDownloads
            {
                Artifact = new DownloadArtifact
                {
                    Path = "net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar",
                    Url = "https://libraries.minecraft.net/net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar"
                }
            }
        };
        vanillaDetail.Libraries ??= new List<LibraryEntry>();
        vanillaDetail.Libraries.Insert(0, launchwrapperLib);

        // 根因（"装完 OptiFine 启动还是原版"）：mainClass 换成了 launchwrapper，但从没告诉它
        // 要加载哪个 tweaker，LaunchWrapper 就只是原样启动原版。必须给游戏参数追加
        // --tweakClass optifine.OptiFineTweaker（官方安装器生成的 json 也是这么写的）。
        const string OptiFineTweaker = "optifine.OptiFineTweaker";
        if (!string.IsNullOrWhiteSpace(vanillaDetail.MinecraftArgumentsLegacy))
        {
            if (!vanillaDetail.MinecraftArgumentsLegacy.Contains(OptiFineTweaker, StringComparison.Ordinal))
                vanillaDetail.MinecraftArgumentsLegacy += " --tweakClass " + OptiFineTweaker;
        }
        else
        {
            vanillaDetail.Arguments ??= new ArgumentsSpec();
            vanillaDetail.Arguments.Game ??= new List<object>();
            vanillaDetail.Arguments.Game.Add("--tweakClass");
            vanillaDetail.Arguments.Game.Add(OptiFineTweaker);
        }

        progress?.Report(new ProgressInfo("校验并补全依赖库", 2, 3, fileName));
        await _vanillaDownloader.DownloadLibrariesOnlyAsync(minecraftDir, vanillaDetail, progress, ct);
        await _vanillaDownloader.DownloadAssetsOnlyAsync(minecraftDir, vanillaDetail, progress, ct);
        ct.ThrowIfCancellationRequested();
        var finalJson = JsonSerializer.Serialize(vanillaDetail,
            new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        await DownloadTargetLock.WriteTextAsync(ResourceDownloadPath.ResolveChild(versionDir, $"{versionId}.json"), finalJson, ct);

        try { Directory.Delete(tempDir, recursive: true); } catch { /* 忽略清理失败 */ }

        ct.ThrowIfCancellationRequested();
        progress?.Report(new ProgressInfo(Loc.T("Str_Cs_Installation_Complete", "安装完成"), 3, 3, fileName));
        return versionId;
    }

    /// <summary>起一个前台进程、等它跑完、非 0 退出码时把最后若干行输出打进异常里——
    /// InstallOptiFineClientAsync 里"编译帮助类"和"运行帮助类"两步共用同一段样板逻辑。</summary>
    private static async Task RunProcessAsync(string exePath, IReadOnlyList<string> args, string workingDir,
        string failureMessage, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        ct.ThrowIfCancellationRequested();
        process.Start();

        var captured = await InstallerOutputCapture.WaitForExitAsync(process, TimeSpan.FromMinutes(5),
            failureMessage, workingDir, null, ct);
        var outputLines = captured.Snapshot();

        if (process.ExitCode != 0)
        {
            var tail = string.Join('\n', outputLines.TakeLast(20));
            throw new InvalidOperationException($"{failureMessage}（退出码 {process.ExitCode}）。最后输出：\n{tail}");
        }
    }

    /// <summary>
    /// 把一个已安装版本的文件夹重命名成用户自定义的实例名（供 Forge/NeoForge 客户端安装、以及
    /// 原版直装两处复用——原版没有独立的"加载器安装"步骤能提前指定目标目录名，只能装完之后
    /// 原地改名，跟 Forge/NeoForge 官方安装器自己决定文件夹名、装完再改名是同一种局面）：
    /// 1) 用 MakeUniqueInstanceName 规整 + 去重得到目标文件夹名；
    /// 2) 物理重命名整个版本目录；
    /// 3) 目录内跟旧文件夹名同名的 .json/.jar（LauncherService 靠"文件名跟版本 id 一致"
    ///    去定位主 jar，见 ResolveVersionFile 的改名容错查找逻辑）同步改名成新文件夹名；
    /// 4) json 内部的 "id" 字段同步改成新名字，保持文件名和 json 内容一致。
    /// 任何一步失败都直接返回 null，交给调用方回退到默认名字，不让重命名失败拖累整个安装结果。
    /// </summary>
    public static string? TryRenameInstalledInstance(string minecraftDir, string oldDir, string customInstanceName)
    {
        try
        {
            var oldName = Path.GetFileName(oldDir);
            var versionsDir = Path.Combine(minecraftDir, "versions");
            var newName = ModpackInstallService.MakeUniqueInstanceName(minecraftDir, customInstanceName);
            if (string.Equals(newName, oldName, StringComparison.Ordinal)) return oldName; // 没有实际变化

            var newDir = Path.Combine(versionsDir, newName);
            Directory.Move(oldDir, newDir);

            var oldJsonPath = Path.Combine(newDir, $"{oldName}.json");
            var oldJarPath = Path.Combine(newDir, $"{oldName}.jar");
            var newJsonPath = Path.Combine(newDir, $"{newName}.json");
            var newJarPath = Path.Combine(newDir, $"{newName}.jar");

            if (File.Exists(oldJsonPath))
            {
                var detail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(oldJsonPath));
                if (detail != null)
                {
                    detail.Id = newName;
                    var updatedJson = JsonSerializer.Serialize(detail,
                        new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                    File.WriteAllText(oldJsonPath, updatedJson);
                }
                File.Move(oldJsonPath, newJsonPath, overwrite: true);
            }
            if (File.Exists(oldJarPath))
                File.Move(oldJarPath, newJarPath, overwrite: true);

            return newName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 根据安装器完整输出里的关键词，识别几种社区里最常见、有明确解决办法的 Forge/NeoForge
    /// 安装失败原因，返回一句可直接指导用户下一步该做什么的中文提示（识别不到就返回空字符串，
    /// 调用方仍然会展示原始输出尾巴，不会因为诊断失败而丢失信息）。
    ///
    /// 这几类是实际使用中反复出现、且原始安装器报错信息对普通用户很不友好的典型情况：
    /// - Java 版本不匹配（安装器要求特定 Java 版本运行，用户本地默认 Java 版本不满足）；
    /// - 网络下载失败/校验和不匹配（安装器自己联网下载 library 时失败，常见于国内直连不稳定）；
    /// - 磁盘空间不足；
    /// - 目标目录没有写入权限（常见于装在 Program Files 下、没有管理员权限运行的情况）。
    /// </summary>
    private static string DiagnoseForgeInstallerFailure(string output)
    {
        if (string.IsNullOrEmpty(output)) return "";

        // UnsupportedClassVersionError / "class file version" 是"用错 Java 主版本号跑安装器"的
        // 典型异常类型，比如用 Java 8 跑一个要求 Java 17+ 才能运行的新版安装器。
        if (output.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("class file version", StringComparison.OrdinalIgnoreCase))
        {
            return "\n提示：这通常是本地选用的 Java 版本太旧，安装器本身需要更高版本的 Java 才能运行。" +
                "请在「安装新版本」弹窗里点「自动检测」重新匹配，或去「设置」页下载一个更新的 Java 版本后重试。\n";
        }

        if (output.Contains("NoSuchAlgorithmException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("SSLHandshakeException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("ConnectException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("UnknownHostException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Downloading library", StringComparison.OrdinalIgnoreCase) && output.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return "\n提示：安装器在自己联网下载所需的库文件时失败了，通常是网络问题（国内直连 Forge/NeoForge\n" +
                "官方源经常不稳定）。可以尝试更换网络环境（如使用代理）后重试，多试几次也可能成功。\n";
        }

        if (output.Contains("No space left on device", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("磁盘空间不足", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("There is not enough space", StringComparison.OrdinalIgnoreCase))
        {
            return "\n提示：磁盘空间不足，请清理出至少 1GB 可用空间后重试。\n";
        }

        if (output.Contains("AccessDeniedException", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase))
        {
            return "\n提示：没有写入权限，通常是因为 .minecraft 文件夹装在 C:\\Program Files 等需要管理员\n" +
                "权限才能写入的目录下。建议把游戏文件夹换到不需要额外权限的位置（如 D:\\Games\\.minecraft），\n" +
                "或者以管理员身份运行本启动器后重试。\n";
        }

        return "";
    }

    /// <summary>
    /// 确保 minecraftDir 下存在一份 launcher_profiles.json，满足 Forge/NeoForge 官方安装器
    /// --installClient 的前置检查。已存在时不覆盖（不破坏可能存在的真实数据）。
    /// 格式对齐官方启动器实际写出的最小结构（profiles/settings/version 三个顶层字段），
    /// 安装器只做"文件存在 + JSON 可解析"检查，不校验具体字段内容，这里给的是能通过校验的最小合法值。
    /// </summary>
    private static void EnsureLauncherProfilesJson(string minecraftDir)
    {
        Directory.CreateDirectory(minecraftDir);
        var path = Path.Combine(minecraftDir, "launcher_profiles.json");
        if (File.Exists(path)) return;

        const string minimalProfiles = """
        {
          "profiles": {},
          "settings": {
            "crashAssistance": true,
            "enableAdvanced": false,
            "enableAnalytics": true,
            "enableHistorical": false,
            "enableReleases": true,
            "enableSnapshots": false,
            "keepLauncherOpen": false,
            "profileSorting": "ByLastPlayed",
            "showGameLog": false,
            "showMenu": false,
            "soundOn": false
          },
          "version": 3
        }
        """;

        File.WriteAllText(path, minimalProfiles);
    }

    // Use the shared resolver for CurseForge CDN lookup, mirror probing, range downloads,
    // cancellation and atomic publication. Validate the complete JAR before trying to run it.
    private async Task DownloadFileNoHashCheckAsync(string url, string destPath, CancellationToken ct)
    {
        await DownloadEndpoints.DownloadFileWithFallbackAsync(_http, url, _preferMirror,
            destPath, "加载器安装器下载失败，请检查网络或尝试其他版本。", ct: ct,
            validateFile: path => DownloadService.ValidateArchiveFile(path, ct));
    }

    // All in-place operations now use a separate staging game root and a recoverable
    // profile transaction. The previous implementation could delete the source instance
    // when an installer returned the same ID and removed unrelated top-level JSON/JAR files.
    [Obsolete("Use InstalledVersionMaintenanceService.ApplyAsync with an explicit component choice.")]
    public Task<string> ChangeLoaderInPlaceAsync(string minecraftDir, string existingVersionId,
        Func<CancellationToken, Task<string>> installNewLoaderAsync, string backupReason,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
        => throw new NotSupportedException("旧的加载器维护回调无法隔离安装目录，已停止使用。请从“版本与组件”入口操作。");

    private async Task<string> ApplyComponentChangeAsync(string root, string id, InstalledVersionChange change,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var backup = await new InstalledVersionMaintenanceService(_maintenanceConfig).ApplyAsync(root, id, change, progress, ct);
        InstanceBackupService.NotifyBackupCreated(backup, "版本与组件修改");
        return id;
    }

    public Task<string> ChangeFabricLoaderInPlaceAsync(string minecraftDir, string existingVersionId,
        string mcVersion, string loaderVersion, IProgress<ProgressInfo>? progress, CancellationToken ct = default,
        bool installFabricApi = false)
    {
        if (installFabricApi) throw new NotSupportedException("请在 Mod 管理中添加或更新 Fabric API，以保留模组选择和兼容性检查。");
        return ApplyComponentChangeAsync(minecraftDir, existingVersionId,
            new InstalledVersionChange(mcVersion, ServerCoreType.Fabric, loaderVersion), progress, ct);
    }

    public Task<string> ChangeQuiltLoaderInPlaceAsync(string minecraftDir, string existingVersionId,
        string mcVersion, string loaderVersion, IProgress<ProgressInfo>? progress, CancellationToken ct = default,
        bool installQsl = false)
    {
        if (installQsl) throw new NotSupportedException("请在 Mod 管理中添加或更新 QSL，以保留模组选择和兼容性检查。");
        return ApplyComponentChangeAsync(minecraftDir, existingVersionId,
            new InstalledVersionChange(mcVersion, ServerCoreType.Quilt, loaderVersion), progress, ct);
    }

    public Task<string> ChangeForgeOrNeoForgeLoaderInPlaceAsync(string minecraftDir, string existingVersionId,
        ServerCoreType coreType, string fullVersion, string javaExePath,
        IProgress<ProgressInfo>? progress, CancellationToken ct = default)
    {
        var minecraft = coreType switch
        {
            ServerCoreType.Forge when fullVersion.Contains('-') => fullVersion.Split('-')[0],
            ServerCoreType.NeoForge => ComponentProfileComposer.MinecraftForNeoForge(fullVersion),
            _ => null
        };
        if (minecraft == null) throw new InvalidOperationException("无法从组件版本确定 Minecraft 版本，请使用版本与组件窗口选择兼容构建。");
        return ApplyComponentChangeAsync(minecraftDir, existingVersionId,
            new InstalledVersionChange(minecraft, coreType, fullVersion, javaExePath), progress, ct);
    }

    /// <summary>释放内部 _vanillaDownloader（进而释放它可能持有的智能限速后台采样任务）。</summary>
    public void Dispose() { _vanillaDownloader.Dispose(); _http.Dispose(); }
}
