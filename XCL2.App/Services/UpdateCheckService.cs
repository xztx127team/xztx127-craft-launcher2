using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>
/// Checks GitHub Releases and applies only a size/hash/architecture-verified executable.
/// The update helper waits for a real launcher exit, atomically replaces its existing path,
/// retains a unique same-directory backup and rolls back a failed five-second start probe.
/// </summary>
public static partial class UpdateCheckService
{
    public enum CheckStatus { UpToDate, UpdateAvailable, UpdatesDisabled, Updating, Busy, Failed, Skipped }
    public sealed record CheckResult(CheckStatus Status, string Message, bool ErrorAlreadyShown = false);
    private static readonly SemaphoreSlim CheckGate = new(1, 1);
    // 项目实际发布地址：https://github.com/adorable0127/xztx127-craft-launcher2/releases
    private const string RepoOwner = "adorable0127";
    private const string RepoName = "xztx127-craft-launcher2";
    private static readonly string LatestReleaseApi = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>up-log 目录：xcl2/up-log，每次真正触发一次更新流程（用户点了"现在更新"）
    /// 就会在这里生成一个以时间戳命名的日志文件，记录从检测到（若走完流程）重启的全过程。
    /// 仅仅"检查了一下但没有新版本"不写日志，避免每次启动都在这个目录里堆空文件。</summary>
    private static string UpLogDir => Path.Combine(App.DataDir, "up-log");

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static UpdateCheckService()
    {
        // GitHub API 强制要求带 User-Agent，否则直接 403。
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("XCL2-Launcher-UpdateChecker");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    private sealed record GithubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest);

    private sealed record GithubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("assets")] List<GithubAsset>? Assets);

    /// <summary>供 App.xaml.cs 在主窗口显示之后调用：整个检查过程完全跑在后台线程，
    /// 任何环节出错都只记日志、绝不弹错误打扰用户，也绝不影响主流程。</summary>
    public static void CheckForUpdateInBackground(Func<AppConfig> getConfig)
    {
        if (App.StartupArgs.SkipUpdateCheck || !LauncherUpdatePolicy.ShouldCheckOnStartup(getConfig().CheckLauncherUpdatesOnStartup)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // 让最耗资源的启动阶段（首帧渲染、账户刷新等）先跑完，更新检查不抢这个时间段。
                await Task.Delay(TimeSpan.FromSeconds(5));
                // 延迟期间用户可能已经关闭启动检查，或导入了另一份配置。
                if (App.StartupArgs.SkipUpdateCheck || !LauncherUpdatePolicy.ShouldCheckOnStartup(getConfig().CheckLauncherUpdatesOnStartup)) return;
                var result = await CheckForUpdateAsync(() => getConfig().EnableLauncherUpdates,
                    () => getConfig().CheckLauncherUpdatesOnStartup, backgroundReminder: true);
                if (result.Status is CheckStatus.Failed or CheckStatus.UpdatesDisabled)
                    LauncherLogService.AppendLine("[自动更新] " + result.Message);
            }
            catch (Exception ex)
            {
                LauncherLogService.AppendLine($"[自动更新] 检查更新时发生异常（已忽略，不影响正常使用）：{ex.Message}");
            }
        });
    }

    /// <summary>手动与启动检查共用同一 API 和安装流程；失败与“已是最新”返回不同状态。
    /// 安装前会再次读取开关，检查期间关闭更新也不会继续下载。</summary>
    public static async Task<CheckResult> CheckForUpdateAsync(Func<bool> updatesEnabled, Func<bool>? checkStillEnabled = null,
        bool backgroundReminder = false)
    {
        if (!await CheckGate.WaitAsync(0))
            return new(CheckStatus.Busy, "已有更新检查或下载正在进行，请稍后再试。");
        try
        {
            return await RunCheckAsync(updatesEnabled, checkStillEnabled, backgroundReminder);
        }
        catch (TaskCanceledException)
        {
            return new(CheckStatus.Failed, "检查更新超时，请检查网络后重试。");
        }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine($"[更新检查] 检查更新失败：{ex.Message}");
            return new(CheckStatus.Failed, "检查更新失败：" + ex.Message);
        }
        finally { CheckGate.Release(); }
    }

    private static async Task<CheckResult> RunCheckAsync(Func<bool> updatesEnabled, Func<bool>? checkStillEnabled, bool backgroundReminder)
    {
        if (checkStillEnabled?.Invoke() == false)
            return new(CheckStatus.Skipped, "启动时检查更新已关闭。");
        var release = await FetchLatestReleaseAsync();
        var remoteVersion = LauncherUpdatePolicy.ExtractVersion(release.TagName);
        var localVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        if (remoteVersion == null)
            return new(CheckStatus.Failed, $"无法识别 API 返回的版本号：{release.TagName}");
        if (!LauncherUpdatePolicy.IsNewer(remoteVersion, localVersion))
            return new(CheckStatus.UpToDate, $"当前 v{FormatVersion(localVersion)} 已是最新版本（API 最新 v{FormatVersion(remoteVersion)}）。");
        if (checkStillEnabled?.Invoke() == false)
            return new(CheckStatus.Skipped, "启动时检查更新已关闭。");
        if (!updatesEnabled())
            return new(CheckStatus.UpdatesDisabled, $"发现新版本 v{FormatVersion(remoteVersion)}（当前 v{FormatVersion(localVersion)}）。启动器更新已关闭；启用并保存设置后才能下载安装。");

        var asset = PickAssetForCurrentArch(release.Assets);
        if (asset == null)
        {
            return new(CheckStatus.Failed, $"发现新版本 v{FormatVersion(remoteVersion)}，但发布中没有当前系统架构（{(Environment.Is64BitOperatingSystem ? "x64" : "x86")}）的 exe 更新包。");
        }

        if (backgroundReminder)
        {
            PublishUpdateReminder(remoteVersion, localVersion, updatesEnabled);
            return new(CheckStatus.UpdateAvailable, $"发现新版本 v{FormatVersion(remoteVersion)}，已放入提醒角标。");
        }

        _ = LauncherUpdateMetadata.FromAsset(asset.Size, asset.Digest);

        // 发布更新提醒也展示 GitHub API 上当前安装版本对应的 Release 日志；
        // 若仓库未发布同标签的版本或 API 暂时不可访问，不影响原有更新流程。
        var changelog = string.IsNullOrWhiteSpace(release.Body) ? "（作者未填写更新日志）" : release.Body!.Trim();
        string currentReleaseNotes;
        try
        {
            var notes = await GitHubReleaseNotesService.FetchAsync();
            currentReleaseNotes = notes.Current;
        }
        catch (Exception ex)
        {
            currentReleaseNotes = "GitHub 当前安装版本的更新日志获取失败：" + ex.Message +
                                  "\n可在「更新日志」窗口查看发布页面并重试。";
        }

        if (checkStillEnabled?.Invoke() == false)
            return new(CheckStatus.Skipped, "启动时检查更新已关闭。");
        if (!updatesEnabled())
            return new(CheckStatus.UpdatesDisabled, "启动器更新已关闭，本次不会下载安装。");

        var confirmed = await Application.Current.Dispatcher.InvokeAsync(() =>
            MessageBoxDialog.ShowConfirmAsync(
                $"发现新版本 v{FormatVersion(remoteVersion)}（当前 v{FormatVersion(localVersion)}）：\n\n" +
                $"【GitHub 最新版本发布日志】\n{changelog}\n\n【GitHub 当前安装版本发布日志】\n{currentReleaseNotes}\n\n" +
                "是否现在下载并更新？更新完成后会关闭并重新启动启动器。\n" +
                "（如果选择「取消」，启用启动检查时，下次启动仍会提示。）",
                "发现新版本", "现在更新")).Task.Unwrap();

        if (confirmed != true)
            return new(CheckStatus.UpdateAvailable, $"发现新版本 v{FormatVersion(remoteVersion)}，您选择了暂不更新。");
        if (!LauncherUpdatePolicy.CanInstall(updatesEnabled(), confirmed == true))
            return new(CheckStatus.UpdatesDisabled, "启动器更新已关闭，本次不会下载安装。");
        if (checkStillEnabled?.Invoke() == false)
            return new(CheckStatus.Skipped, "启动时检查更新已关闭，本次更新已取消。");

        return await DownloadAndApplyUpdateAsync(remoteVersion, localVersion, asset, changelog,
            updatesEnabled, checkStillEnabled);
    }

    private static async Task<GithubRelease> FetchLatestReleaseAsync()
    {
        using var resp = await Http.GetAsync(LatestReleaseApi);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = (int)resp.StatusCode is 403 or 429 ? "，GitHub API 访问受限或请求过于频繁，请稍后重试" : "";
            throw new HttpRequestException($"更新 API 返回 HTTP {(int)resp.StatusCode}（{resp.ReasonPhrase}）{detail}。", null, resp.StatusCode);
        }
        var json = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GithubRelease>(json)
            ?? throw new InvalidDataException("更新 API 返回了空的版本信息。");
    }

    /// <summary>程序集版本通常是 2.2.13.0，而 Release tag 是 2.2.14。提示里去掉末尾无意义的 .0，
    /// 让当前版本稳定显示为 2.2.13、下一版稳定显示为 2.2.14。</summary>
    private static string FormatVersion(Version version)
    {
        if (version.Revision > 0) return version.ToString(4);
        if (version.Build >= 0) return version.ToString(3);
        return version.ToString(2);
    }

    /// <summary>
    /// 按当前系统是 32 位还是 64 位，从 assets 里挑对应的那个 exe。
    /// 附件命名固定形如："...x86(32位特供）不含运行时.exe" / "...x64...不含运行时.exe"，
    /// 只需要认文件名里的 "x86" / "x64" 关键字，不关心中文部分（避免全角括号、空格
    /// 之类的细节写法以后微调导致匹配失效）。64 位系统下如果找不到 x64 包，不会退回
    /// 去装 x86 包——32 位程序在 64 位系统上其实能跑，但"该给用户装对应位数的版本"
    /// 这件事不该由更新逻辑自作主张，找不到匹配的就跳过这次更新提示，比装错架构安全。
    /// </summary>
    private static GithubAsset? PickAssetForCurrentArch(List<GithubAsset>? assets)
    {
        var name = LauncherUpdatePolicy.PickAssetName(assets?.Select(asset => asset.Name), Environment.Is64BitOperatingSystem);
        return name == null ? null : assets?.FirstOrDefault(asset => asset.Name == name);
    }

    private static async Task<CheckResult> DownloadAndApplyUpdateAsync(Version remoteVersion, Version localVersion,
        GithubAsset asset, string changelog, Func<bool> updatesEnabled, Func<bool>? checkStillEnabled)
    {
        var metadata = LauncherUpdateMetadata.FromAsset(asset.Size, asset.Digest);
        var transactionId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(UpLogDir);
        var logPath = Path.Combine(UpLogDir, transactionId + ".log");
        void Log(string line)
        {
            try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {line}\n", Encoding.UTF8); }
            catch { }
        }
        var currentExePath = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前启动器 EXE 路径，未应用更新。");
        var tempRoot = Path.Combine(App.DataDir, "_update_temp", transactionId);
        Directory.CreateDirectory(tempRoot);
        var downloadedExe = Path.Combine(tempRoot, "download.exe");
        LauncherUpdatePlan? plan = null;
        Process? updater = null;
        ProgressDialog? progressDialog = null;
        var handedOff = false;
        CheckResult? GetStopReason()
        {
            if (!updatesEnabled()) return new(CheckStatus.UpdatesDisabled, "启动器更新已关闭，本次未替换当前版本。");
            if (checkStillEnabled?.Invoke() == false) return new(CheckStatus.Skipped, "启动时检查更新已关闭，本次更新已取消。");
            return null;
        }
        try
        {
            Log($"更新 v{FormatVersion(localVersion)} -> v{FormatVersion(remoteVersion)}，保留当前文件名与快捷方式路径：{currentExePath}");
            Log($"发布附件：{asset.Name}；大小：{metadata.Size}；SHA256：{metadata.Sha256}\n{changelog}");
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                progressDialog = new ProgressDialog($"正在下载并校验 v{FormatVersion(remoteVersion)}…");
                progressDialog.Show();
            });
            using var downloader = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            await DownloadQueueService.Instance.RunAndWaitAsync("下载启动器更新", async (item, ct) =>
            {
                var queueProgress = item.CreateProgress();
                var progress = new Progress<ProgressInfo>(value =>
                { queueProgress.Report(value); progressDialog?.Progress.Report(value); });
                return await DownloadEndpoints.DownloadFileWithFallbackAsync(downloader, asset.BrowserDownloadUrl,
                    DownloadEndpoints.PreferMirror, downloadedExe, "启动器更新包下载或校验失败", progress, ct,
                    expectedSize: metadata.Size, expectedSha256: metadata.Sha256, userAgent: "XCL2-Launcher-Updater");
            });
            if (GetStopReason() is { } stopped) return stopped;
            plan = await Task.Run(() => LauncherUpdatePackage.PrepareAsync(downloadedExe, currentExePath, metadata,
                Environment.Is64BitOperatingSystem, logPath, tempRoot));
            LauncherUpdatePackage.TryDelete(downloadedExe);
            if (GetStopReason() is { } stoppedBeforeStart) return stoppedBeforeStart;
            updater = Process.Start(LauncherUpdatePackage.CreateStartInfo(plan))
                ?? throw new IOException("无法启动更新辅助进程，当前版本继续运行。");
            await LauncherUpdatePackage.WaitUntilReadyAsync(updater, plan);
            // A real Close can be cancelled by unsaved settings or notes. The helper does
            // nothing until this original process has actually exited, and observes abort.
            var closeTask = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                progressDialog?.Close();
                if (GetStopReason() != null || Application.Current.MainWindow is not MainWindow owner)
                    return Task.FromResult(false);
                return owner.RequestExitForUpdateAsync(() => LauncherUpdatePackage.AuthorizeOnClosed(plan, updater));
            });
            var closed = await closeTask.ConfigureAwait(false);
            if (!closed)
            {
                LauncherUpdatePackage.Abort(plan);
                Log("关闭启动器被取消，更新辅助进程已收到中止信号，旧版本未替换。");
                return GetStopReason() ?? new(CheckStatus.Skipped, "已取消关闭启动器，本次更新未应用，当前版本继续运行。");
            }
            handedOff = true;
            return new(CheckStatus.Updating, $"已交由辅助进程应用 v{FormatVersion(remoteVersion)} 更新；备份保留，失败将尝试恢复。");
        }
        catch (Exception ex)
        {
            if (plan != null) { try { LauncherUpdatePackage.Abort(plan); } catch { } }
            Log("更新准备失败，未授权替换：" + ex);
            return new(CheckStatus.Failed, "更新准备失败，当前版本未替换：" + ex.Message);
        }
        finally
        {
            if (!handedOff)
            {
                if (plan != null) { try { LauncherUpdatePackage.Abort(plan); } catch { } }
                // Cancelling does not rely on being able to create Abort. The helper
                // has no positive Closed authorization and is also stopped explicitly.
                var helperStopped = updater == null;
                if (updater != null)
                {
                    try
                    {
                        if (!updater.HasExited) updater.Kill();
                        await updater.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        helperStopped = true;
                    }
                    catch (Exception ex) { Log("更新辅助进程清理未完成（无关闭授权，不能替换旧版本）：" + ex.Message); }
                }
                if (helperStopped && plan != null)
                    foreach (var path in new[] { plan.StagePath, plan.RestorePath, plan.ReadyPath, plan.AbortPath, plan.AuthorizePath })
                        LauncherUpdatePackage.TryDelete(path);
                LauncherUpdatePackage.TryDelete(downloadedExe);
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.HasShutdownStarted)
                    await dispatcher.InvokeAsync(() => progressDialog?.Close());
            }
            updater?.Dispose();
        }
    }
}
