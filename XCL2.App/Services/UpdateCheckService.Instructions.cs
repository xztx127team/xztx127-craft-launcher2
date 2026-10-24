using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using XCL2.App.Views;

namespace XCL2.App.Services;

public static partial class UpdateCheckService
{
    private sealed record RequiredReleaseCache(GithubRelease Release, LauncherReleaseInstructions Instructions,
        string CheckedBuildIdentity, bool InstalledWasGitHubPreview);
    private static string CurrentBuildIdentity => typeof(UpdateCheckService).Assembly.ManifestModule.ModuleVersionId.ToString("D");
    private static TaskCompletionSource? _requiredDialogCompletion;
    private static string RequiredReleaseCachePath => Path.Combine(App.DataDir, "required-launcher-update.json");
    private static DispatcherTimer? _requiredDeadlineTimer;
    private static bool _requiredDialogOpen;
    public static bool IsRequiredUpdatePending { get; private set; }

    private static bool IsOfficialReleaseAsset(GithubRelease release, GithubAsset asset)
    {
        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var prefix = $"/{RepoOwner}/{RepoName}/releases/download/{release.TagName}/";
        return Uri.UnescapeDataString(uri.AbsolutePath).StartsWith(prefix, StringComparison.Ordinal)
            && !release.Draft;
    }

    private static async Task<(LauncherReleaseInstructions Instructions, bool Verified)> FetchReleaseInstructionsAsync(GithubRelease release)
    {
        var asset = release.Assets?.FirstOrDefault(a => a.Name.Equals("setup.ini", StringComparison.OrdinalIgnoreCase));
        if (asset == null) return (LauncherReleaseInstructions.Default, true);
        try
        {
            if (asset.Size is < 0 or > 16384 || !IsOfficialReleaseAsset(release, asset))
                throw new InvalidDataException("setup.ini 的来源或大小无效。");
            using var response = await Http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 16384) throw new InvalidDataException("setup.ini 超出大小限制。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var block = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(block, timeout.Token)) != 0)
            {
                if (buffer.Length + count > 16384) throw new InvalidDataException("setup.ini 超出大小限制。");
                buffer.Write(block, 0, count);
            }
            var bytes = buffer.ToArray();
            if (bytes.Length != asset.Size) throw new InvalidDataException("setup.ini 与发布附件大小不一致。");
            if (!string.IsNullOrWhiteSpace(asset.Digest))
            {
                var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes));
                if (!hash.Equals(asset.Digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("setup.ini 的 SHA256 校验失败。");
            }
            return (LauncherReleaseInstructions.Parse(Encoding.UTF8.GetString(bytes)), true);
        }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine("[更新策略] 无法读取 setup.ini，沿用原有更新流程：" + ex.Message);
            return (LauncherReleaseInstructions.Default, false);
        }
    }

    private static List<RequiredReleaseCache> ReadRequiredReleaseCache()
    {
        if (!File.Exists(RequiredReleaseCachePath) || new FileInfo(RequiredReleaseCachePath).Length > 1024 * 1024)
            return new();
        return JsonSerializer.Deserialize<List<RequiredReleaseCache>>(File.ReadAllText(RequiredReleaseCachePath)) ?? new();
    }

    private static void RememberRequiredRelease(GithubRelease release, LauncherReleaseInstructions instructions, bool verified)
    {
        if (!verified) return;
        try
        {
            var entries = ReadRequiredReleaseCache();
            var local = _currentVersionWithChannel ?? GetCurrentReleaseVersion();
            entries.RemoveAll(item => item.Release.TagName.Equals(release.TagName, StringComparison.OrdinalIgnoreCase)
                || LauncherUpdatePolicy.ParseReleaseVersion(item.Release.TagName, item.Release.Prerelease) is not { } version
                || !LauncherUpdatePolicy.IsNewer(version, local));
            if (instructions.HasRequirement)
                entries.Add(new RequiredReleaseCache(release, instructions, CurrentBuildIdentity, local.OfficialPrerelease));
            // Keep independent required versions: a future deadline cannot erase an already mandatory older release.
            entries = entries.OrderByDescending(item => item.Instructions.IsRequired(DateOnly.FromDateTime(DateTime.Now)))
                .ThenBy(item => item.Instructions.Deadline).Take(32).ToList();
            Directory.CreateDirectory(App.DataDir);
            var temporary = RequiredReleaseCachePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries), Encoding.UTF8);
            File.Move(temporary, RequiredReleaseCachePath, overwrite: true);
            StartRequiredDeadlineTimer();
        }
        catch (Exception ex) { LauncherLogService.AppendLine("[更新策略] 保存强制更新状态失败：" + ex.Message); }
    }

    /// <summary>Call after the main overlay host is ready, before processing startup commands.</summary>
    public static async Task EnforceCachedRequiredUpdateAsync()
    {
        if (_requiredDialogOpen)
        {
            if (_requiredDialogCompletion != null) await _requiredDialogCompletion.Task;
            return;
        }
        await CheckGate.WaitAsync();
        try
        {
            var cached = ReadRequiredReleaseCache();
            var local = GetCurrentReleaseVersion();
            if (cached.Any(item => item.CheckedBuildIdentity == CurrentBuildIdentity && item.InstalledWasGitHubPreview))
            {
                local = LauncherUpdatePolicy.ParseReleaseVersion(BuildInfo.Version, true) ?? (local with { OfficialPrerelease = true });
                _currentVersionWithChannel = local;
            }
            var entries = cached.Where(item => item.Instructions.HasRequirement && !item.Release.Draft)
                .Select(item => (Cache: item, Version: LauncherUpdatePolicy.ParseReleaseVersion(item.Release.TagName, item.Release.Prerelease)))
                .Where(item => item.Version.HasValue && LauncherUpdatePolicy.IsNewer(item.Version.Value, local)).ToArray();
            if (entries.Length == 0)
            {
                IsRequiredUpdatePending = false;
                if (File.Exists(RequiredReleaseCachePath)) File.Delete(RequiredReleaseCachePath);
                return;
            }
            StartRequiredDeadlineTimer();
            var selected = entries.Where(item => item.Cache.Instructions.IsRequired(DateOnly.FromDateTime(DateTime.Now)))
                .OrderByDescending(item => item.Version).FirstOrDefault();
            if (selected.Cache == null || !selected.Version.HasValue) return;
            var asset = PickAssetForCurrentArch(selected.Cache.Release.Assets);
            if (asset != null && !IsOfficialReleaseAsset(selected.Cache.Release, asset)) return;
            await OfferRequiredUpdateAsync(selected.Cache.Release, selected.Version.Value, local, asset, selected.Cache.Instructions);
        }
        catch (Exception ex) { LauncherLogService.AppendLine("[更新策略] 读取已确认的更新要求失败：" + ex.Message); }
        finally { CheckGate.Release(); }
    }

    private static void StartRequiredDeadlineTimer()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(new Action(StartRequiredDeadlineTimer)); return; }
        if (_requiredDeadlineTimer != null) return;
        _requiredDeadlineTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _requiredDeadlineTimer.Tick += async (_, _) =>
        {
            if (!_requiredDialogOpen && CheckGate.CurrentCount > 0) await EnforceCachedRequiredUpdateAsync();
        };
        _requiredDeadlineTimer.Start();
    }

    private static async Task<CheckResult> OfferRequiredUpdateAsync(GithubRelease release, LauncherReleaseVersion remote,
        LauncherReleaseVersion local, GithubAsset? asset, LauncherReleaseInstructions instructions)
    {
        if (_requiredDialogOpen) return new(CheckStatus.Busy, "需要先完成启动器更新。");
        IsRequiredUpdatePending = true;
        _requiredDialogOpen = true;
        _requiredDialogCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var notes = string.IsNullOrWhiteSpace(release.Body) ? "（作者未填写更新日志）" : release.Body.Trim();
            var reason = instructions.Required ? "发布者已将此版本设置为必须安装。"
                : $"此版本的安装期限为 {instructions.Deadline:yyyy/MM/dd}，现已超过期限。";
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                var dialog = new LauncherRequiredUpdateDialog(release.TagName, reason, notes, async () =>
                {
                    if (asset == null) return "发布中没有当前系统架构对应的更新包，请联系发布者。可以退出启动器，稍后重试。";
                    if (!IsOfficialReleaseAsset(release, asset)) return "更新附件来源校验失败，未开始安装。";
                    var result = await DownloadAndApplyUpdateAsync(remote, local, asset, notes, () => true, null);
                    return result.Message;
                }, () => (Application.Current.MainWindow as MainWindow)?.PerformFullExit(), async () =>
                {
                    try
                    {
                        var fresh = JsonSerializer.Deserialize<GithubRelease>(await FetchReleaseJsonAsync(
                            $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/tags/{Uri.EscapeDataString(release.TagName)}"));
                        if (fresh == null || fresh.Draft || fresh.TagName != release.TagName) return false;
                        var refreshed = await FetchReleaseInstructionsAsync(fresh);
                        release = fresh;
                        asset = PickAssetForCurrentArch(fresh.Assets);
                        notes = string.IsNullOrWhiteSpace(fresh.Body) ? "（作者未填写更新日志）" : fresh.Body.Trim();
                        RememberRequiredRelease(fresh, refreshed.Instructions, refreshed.Verified);
                        if (refreshed.Instructions.IsRequired(DateOnly.FromDateTime(DateTime.Now))) return false;
                        var stillRequired = ReadRequiredReleaseCache().Any(item => item.Instructions.IsRequired(DateOnly.FromDateTime(DateTime.Now))
                            && LauncherUpdatePolicy.ParseReleaseVersion(item.Release.TagName, item.Release.Prerelease) is { } version
                            && LauncherUpdatePolicy.IsNewer(version, local));
                        if (!refreshed.Verified || stillRequired) return false;
                        IsRequiredUpdatePending = false;
                        return true;
                    }
                    catch (Exception ex) { LauncherLogService.AppendLine("[更新策略] 重新检查要求失败：" + ex.Message); return false; }
                });
                await OverlayDialogService.ShowModalAsync(dialog, dismissOnBackgroundClick: false, dismissOnEsc: false);
            }).Task.Unwrap();
            return new(CheckStatus.UpdateAvailable, IsRequiredUpdatePending
                ? "需要安装规定的更新后才能继续使用启动器。"
                : "已确认发布者撤销或延后当前安装要求，可继续使用启动器。");
        }
        finally { _requiredDialogOpen = false; _requiredDialogCompletion?.TrySetResult(); }
    }
}
