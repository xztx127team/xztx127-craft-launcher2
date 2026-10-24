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
    private static readonly string ReleaseListApi = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=100";
    private static int _startupCheckScheduled;

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
        [property: JsonPropertyName("assets")] List<GithubAsset>? Assets,
        [property: JsonPropertyName("prerelease")] bool Prerelease = false,
        [property: JsonPropertyName("draft")] bool Draft = false);

    /// <summary>主窗口与协议流程就绪后启动：网络失败只记日志；正式版沿用提醒角标，
    /// 用户明确加入预览计划时，新的 RC 才展示带三个选择的预览提示。</summary>
    public static void CheckForUpdateInBackground(Func<AppConfig> getConfig)
    {
        bool ShouldCheck() => LauncherUpdatePolicy.ShouldCheckOnStartup(getConfig().CheckLauncherUpdatesOnStartup,
            getConfig().EnableUserExperiencePreviewProgram, App.StartupArgs.SkipUpdateCheck);
        if (!ShouldCheck() || Interlocked.Exchange(ref _startupCheckScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (!ShouldCheck()) return;
                // Stable reminders retain their existing preference. The explicit preview opt-in is independent.
                if (getConfig().CheckLauncherUpdatesOnStartup)
                {
                    var stable = await CheckForUpdateAsync(() => getConfig().EnableLauncherUpdates,
                        () => !App.StartupArgs.SkipUpdateCheck && getConfig().CheckLauncherUpdatesOnStartup,
                        backgroundReminder: true, includePreviews: false);
                    if (stable.Status == CheckStatus.Updating) return;
                    if (stable.Status is CheckStatus.Failed or CheckStatus.UpdatesDisabled)
                        LauncherLogService.AppendLine("[自动更新] " + stable.Message);
                }
                if (!App.StartupArgs.SkipUpdateCheck && getConfig().EnableUserExperiencePreviewProgram)
                {
                    var preview = await CheckPreviewForUpdateAsync(() => getConfig().EnableLauncherUpdates,
                        () => !App.StartupArgs.SkipUpdateCheck && getConfig().EnableUserExperiencePreviewProgram);
                    if (preview.Status == CheckStatus.Failed) LauncherLogService.AppendLine("[预览版更新] " + preview.Message);
                }
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
        bool backgroundReminder = false, bool includePreviews = true)
    {
        if (!await CheckGate.WaitAsync(0))
            return new(CheckStatus.Busy, "已有更新检查或下载正在进行，请稍后再试。");
        try
        {
            return await RunCheckAsync(updatesEnabled, checkStillEnabled, backgroundReminder, includePreviews);
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

    private static async Task<CheckResult> RunCheckAsync(Func<bool> updatesEnabled, Func<bool>? checkStillEnabled, bool backgroundReminder,
        bool includePreviews, bool previewOnly = false)
    {
        if (checkStillEnabled?.Invoke() == false)
            return new(CheckStatus.Skipped, "启动时检查更新已关闭。");
        var release = await FetchLatestReleaseAsync(includePreviews, previewOnly);
        var localVersion = await GetCurrentReleaseVersionWithChannelAsync();
        if (release == null)
            return new(CheckStatus.UpToDate, previewOnly ? "未发现可用的快照或预览版本。" : "未发现可用的启动器发布版本。");
        var parsed = LauncherUpdatePolicy.ParseReleaseVersion(release.TagName, release.Prerelease);
        if (!parsed.HasValue)
            return new(CheckStatus.Failed, $"无法识别 API 返回的版本号：{release.TagName}");
        var remoteVersion = parsed.Value;
        if (!LauncherUpdatePolicy.IsNewer(remoteVersion, localVersion))
            return new(CheckStatus.UpToDate, $"当前 v{FormatVersion(localVersion)} 已是最新版本（API 最新 v{FormatVersion(remoteVersion)}）。");
        if (checkStillEnabled?.Invoke() == false)
            return new(CheckStatus.Skipped, "本次启动检查已关闭。");
        var instructionResult = await FetchReleaseInstructionsAsync(release);
        var instructions = instructionResult.Instructions;
        RememberRequiredRelease(release, instructions, instructionResult.Verified);
        var asset = PickAssetForCurrentArch(release.Assets);
        if (instructions.IsRequired(DateOnly.FromDateTime(DateTime.Now)))
            return await OfferRequiredUpdateAsync(release, remoteVersion, localVersion, asset, instructions);
        if ((backgroundReminder || previewOnly) && instructions.Minor)
        {
            if (remoteVersion.IsPreview) PublishMinorPreviewReminder(release, localVersion, updatesEnabled, instructions);
            else PublishUpdateReminder(remoteVersion.Core, localVersion.Core, updatesEnabled, instructions);
            return new(CheckStatus.UpdateAvailable, $"发现次要更新 {release.TagName}，仅显示提醒。");
        }
        if (instructions.Deadline is { } due)
            release = release with { Body = $"请在 {due:yyyy/MM/dd} 当日结束前安装，逾期将无法跳过。\n\n" + release.Body };
        if (remoteVersion.IsPreview)
            return await OfferPreviewUpdateAsync(release, remoteVersion, localVersion, asset, updatesEnabled,
                checkStillEnabled, honorSuppression: previewOnly);
        if (!updatesEnabled())
            return new(CheckStatus.UpdatesDisabled, $"发现新版本 v{FormatVersion(remoteVersion)}（当前 v{FormatVersion(localVersion)}）。启动器更新已关闭；启用并保存设置后才能下载安装。");
        if (asset == null)
            return new(CheckStatus.Failed, $"发现新版本 v{FormatVersion(remoteVersion)}，但发布中没有当前系统架构（{(Environment.Is64BitOperatingSystem ? "x64" : "x86")}）的 exe 更新包。");

        if (backgroundReminder)
        {
            PublishUpdateReminder(remoteVersion.Core, localVersion.Core, updatesEnabled, instructions);
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

    private static LauncherReleaseVersion GetCurrentReleaseVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return LauncherUpdatePolicy.ParseReleaseVersion(informational)
            ?? LauncherUpdatePolicy.ParseReleaseVersion(BuildInfo.Version)
            ?? new LauncherReleaseVersion(LauncherUpdatePolicy.ExtractVersion(assembly.GetName().Version?.ToString()) ?? new Version(0, 0, 0, 0), null);
    }

    private static LauncherReleaseVersion? _currentVersionWithChannel;

    private static async Task<LauncherReleaseVersion> GetCurrentReleaseVersionWithChannelAsync()
    {
        if (_currentVersionWithChannel.HasValue) return _currentVersionWithChannel.Value;
        var local = GetCurrentReleaseVersion();
        if (local.IsPreview) { _currentVersionWithChannel = local; return local; }
        // A numeric tag may still be marked prerelease by the publisher. Resolve its channel via GitHub.
        try
        {
            var releases = JsonSerializer.Deserialize<List<GithubRelease>>(await FetchReleaseJsonAsync(ReleaseListApi));
            var ownRelease = releases?.FirstOrDefault(item => !item.Draft &&
                (item.TagName.Equals(BuildInfo.Version, StringComparison.OrdinalIgnoreCase) ||
                 item.TagName.Equals("v" + BuildInfo.Version, StringComparison.OrdinalIgnoreCase)));
            if (ownRelease != null)
                local = LauncherUpdatePolicy.ParseReleaseVersion(ownRelease.TagName, ownRelease.Prerelease) ?? local;
            _currentVersionWithChannel = local;
        }
        catch (Exception ex) { LauncherLogService.AppendLine("[更新检查] 当前版本发布通道获取失败，沿用版本号判断：" + ex.Message); }
        return local;
    }

    private static async Task<string> FetchReleaseJsonAsync(string url)
    {
        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            var detail = (int)response.StatusCode is 403 or 429 ? "，GitHub API 访问受限或请求过于频繁，请稍后重试" : "";
            throw new HttpRequestException($"更新 API 返回 HTTP {(int)response.StatusCode}（{response.ReasonPhrase}）{detail}。", null, response.StatusCode);
        }
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<GithubRelease?> FetchLatestReleaseAsync(bool includePreviews, bool previewOnly)
    {
        if (!includePreviews)
        {
            var latest = JsonSerializer.Deserialize<GithubRelease>(await FetchReleaseJsonAsync(LatestReleaseApi));
            var latestVersion = LauncherUpdatePolicy.ParseReleaseVersion(latest?.TagName, latest?.Prerelease == true);
            if (latest != null && !latest.Draft && !latest.Prerelease && latestVersion is { IsPreview: false }) return latest;
            // A publisher may accidentally mark an -rc tag as final. Never install it through stable-only checks.
        }
        var releases = JsonSerializer.Deserialize<List<GithubRelease>>(await FetchReleaseJsonAsync(ReleaseListApi))
            ?? throw new InvalidDataException("更新 API 返回了空的版本列表。");
        var selectedTag = LauncherUpdatePolicy.PickReleaseTag(
            releases.Select(release => (release.TagName, release.Prerelease, release.Draft)), includePreviews, previewOnly);
        return selectedTag == null ? null : releases.First(release => release.TagName == selectedTag);
    }

    private static string FormatVersion(LauncherReleaseVersion version) => version.ToString();

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

    private static async Task<CheckResult> DownloadAndApplyUpdateAsync(LauncherReleaseVersion remoteVersion, LauncherReleaseVersion localVersion,
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
