using System.Net.Http;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

public static class LoaderAvailabilityService
{
    private static readonly HttpClient Http = LauncherHttpClientFactory.Create(TimeSpan.FromSeconds(8));

    /// <summary>
    /// 返回 LoaderChoiceDialog 关心的加载器 -> 是否支持该 mcVersion 的字典。
    /// 永远不会抛异常：单项探测失败会被吞掉并记为 false（置灰），不影响其它项和整个方法的返回。
    /// </summary>
    public static async Task<Dictionary<ServerCoreType, bool>> GetAvailabilityAsync(
        ClientLoaderInstallService loaderSvc, string mcVersion, CancellationToken ct = default)
    {
        var result = new Dictionary<ServerCoreType, bool>
        {
            [ServerCoreType.Vanilla] = true, // 原版永远可选，不需要探测
        };

        // 逐项探测，全部并行发出，互不等待。Task.WhenAll 收尾时哪怕个别任务内部已经把异常
        // 转换成了 false（见下面每个 Probe 调用都包了 try/catch），这里就不会再抛出。
        var fabricTask = Probe(() => IsFabricSupportedAsync(loaderSvc, mcVersion, ct));
        var quiltTask = Probe(() => IsQuiltSupportedAsync(loaderSvc, mcVersion, ct));
        var forgeTask = Probe(() => IsForgeSupportedAsync(loaderSvc, mcVersion, ct));
        var neoForgeTask = Probe(() => IsNeoForgeSupportedAsync(loaderSvc, mcVersion, ct));
        var optiFineTask = Probe(() => IsOptiFineSupportedAsync(loaderSvc, mcVersion, ct));
        var liteLoaderTask = Probe(() => IsLiteLoaderSupportedAsync(mcVersion, ct));
        var cleanroomTask = Probe(() => IsCleanroomSupportedAsync(mcVersion, ct));
        var labyModTask = Probe(() => IsLabyModSupportedAsync(mcVersion, ct));

        await Task.WhenAll(fabricTask, quiltTask, forgeTask, neoForgeTask,
            optiFineTask, liteLoaderTask, cleanroomTask, labyModTask);

        result[ServerCoreType.Fabric] = fabricTask.Result;
        result[ServerCoreType.Quilt] = quiltTask.Result;
        result[ServerCoreType.Forge] = forgeTask.Result;
        result[ServerCoreType.NeoForge] = neoForgeTask.Result;
        result[ServerCoreType.OptiFine] = optiFineTask.Result;
        result[ServerCoreType.LiteLoader] = liteLoaderTask.Result;
        result[ServerCoreType.Cleanroom] = cleanroomTask.Result;
        result[ServerCoreType.LabyMod] = labyModTask.Result;
        return result;
    }

    private static async Task<bool> Probe(Func<Task<bool>> probe)
    {
        try { return await probe(); }
        catch { return false; }
    }

    private static async Task<bool> IsFabricSupportedAsync(ClientLoaderInstallService svc, string mcVersion, CancellationToken ct)
    {
        var loaders = await svc.GetFabricLoaderVersionsAsync(mcVersion, ct);
        return loaders.Count > 0;
    }

    private static async Task<bool> IsQuiltSupportedAsync(ClientLoaderInstallService svc, string mcVersion, CancellationToken ct)
    {
        var loaders = await svc.GetQuiltLoaderVersionsAsync(mcVersion, ct);
        return loaders.Count > 0;
    }

    private static async Task<bool> IsForgeSupportedAsync(ClientLoaderInstallService svc, string mcVersion, CancellationToken ct)
    {
        var builds = await svc.GetForgeInstallerVersionsAsync(mcVersion, ct);
        return builds.Count > 0;
    }

    private static async Task<bool> IsNeoForgeSupportedAsync(ClientLoaderInstallService svc, string mcVersion, CancellationToken ct)
    {
        // NeoForge 只支持 1.20.1 及以后，版本号规则本身跟 MC 版本没有直接的字符串对应关系
        // （NeoForge 用的是它自己的 20.x/21.x 编号），所以复用完整版本列表按前缀粗筛：
        // 例如 mcVersion=1.20.2 对应 NeoForge 版本形如 "20.2.x"。
        var all = await svc.GetNeoForgeVersionsAsync(ct);
        var mcPrefix = McVersionToNeoForgePrefix(mcVersion);
        return mcPrefix != null && all.Any(v => v.StartsWith(mcPrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string? McVersionToNeoForgePrefix(string mcVersion)
    {
        // "1.20.1" -> "20.1", "1.21" -> "21.0"（NeoForge 对形如 1.21 的整数版本统一按 .0 编号）。
        var parts = mcVersion.Trim().Split('.');
        if (parts.Length < 2 || parts[0] != "1") return null;
        var minor = parts[1];
        var patch = parts.Length >= 3 ? parts[2] : "0";
        return $"{minor}.{patch}";
    }

    private static async Task<bool> IsOptiFineSupportedAsync(ClientLoaderInstallService svc, string mcVersion, CancellationToken ct)
    {
        var builds = await svc.GetOptiFineVersionsAsync(mcVersion, ct);
        return builds.Count > 0;
    }

    /// <summary>LiteLoader：官方 versions.json 里直接给了每个 MC 版本对应的构建信息，
    /// 没有条目就是没有该版本的构建（LiteLoader 早已停止更新，只覆盖到 1.12.2 左右的老版本）。</summary>
    private static async Task<bool> IsLiteLoaderSupportedAsync(string mcVersion, CancellationToken ct)
    {
        var json = await DownloadEndpoints.GetStringWithFallbackAsync(
            Http, "https://dl.liteloader.com/versions/versions.json", preferMirror: false,
            "无法获取 LiteLoader 版本列表", ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("versions", out var versions)) return false;
        return versions.TryGetProperty(mcVersion.Trim(), out _);
    }

    /// <summary>Cleanroom：只发布给 1.12.2（TRIP 后端目前只兼容这一个 MC 版本线），
    /// 用 GitHub Releases API 确认一下当前确实存在已发布的 release，避免"仓库暂时没有可下资产"
    /// 时仍然显示可点。</summary>
    private static async Task<bool> IsCleanroomSupportedAsync(string mcVersion, CancellationToken ct)
    {
        if (mcVersion.Trim() != "1.12.2") return false;
        var json = await DownloadEndpoints.GetStringWithFallbackAsync(
            Http, "https://api.github.com/repos/CleanroomMC/Cleanroom/releases", preferMirror: false,
            "无法获取 Cleanroom 版本列表", ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0;
    }

    /// <summary>
    /// LabyMod：官方没有公开、稳定的"按 MC 版本查支持列表"编程接口（labymod.net 的下载页是
    /// 前端渲染的，接口未公开文档化，贸然探测容易因为接口改版而误判)。保守起见，暂不做真实探测，
    /// 统一返回 false（置灰 + 后续在 UI 上提示"请前往 LabyMod 官网手动下载"），等确认好可用的
    /// 查询接口后再改成真实探测，避免在没把握的情况下把"不确定能不能装"误判成"能装"。
    /// </summary>
    private static Task<bool> IsLabyModSupportedAsync(string mcVersion, CancellationToken ct)
        => Task.FromResult(false);
}
