using System.Windows;
using XCL2.App.Views;

namespace XCL2.App.Services;

public static partial class UpdateCheckService
{
    private static void PublishMinorPreviewReminder(GithubRelease release, LauncherReleaseVersion local,
        Func<bool> enabled, LauncherReleaseInstructions instructions)
    {
        if (LauncherUpdatePolicy.IsSuppressedPreviewTag(release.TagName, ConfigService.Active?.Config.SuppressedLauncherPreviewTags)) return;
        ReminderCenterService.Publish("launcher-preview-minor-" + release.TagName, "启动器有次要预览更新",
            $"当前 v{FormatVersion(local)}，可更新到 {release.TagName}。" +
            (instructions.Deadline is { } deadline ? $"请在 {deadline:yyyy/MM/dd} 当日结束前安装，逾期无法跳过。" : "可查看详情后决定是否安装。"),
            "查看更新", async () =>
            {
                var result = await CheckForUpdateAsync(enabled);
                if (result.Status is CheckStatus.Failed or CheckStatus.Busy) ToastService.ShowWarning(result.Message);
            });
    }

    private static async Task<CheckResult> CheckPreviewForUpdateAsync(Func<bool> updatesEnabled, Func<bool> previewStillEnabled)
    {
        if (!await CheckGate.WaitAsync(0)) return new(CheckStatus.Busy, "已有更新检查或下载正在进行。");
        try
        {
            return await RunCheckAsync(updatesEnabled, previewStillEnabled, backgroundReminder: false,
                includePreviews: true, previewOnly: true);
        }
        catch (TaskCanceledException) { return new(CheckStatus.Failed, "预览版更新检查超时，请稍后重试。"); }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine("[预览版更新] " + ex.Message);
            return new(CheckStatus.Failed, "预览版更新检查失败：" + ex.Message);
        }
        finally { CheckGate.Release(); }
    }

    private static async Task<CheckResult> OfferPreviewUpdateAsync(GithubRelease release, LauncherReleaseVersion remote,
        LauncherReleaseVersion local, GithubAsset? asset, Func<bool> updatesEnabled, Func<bool>? previewStillEnabled,
        bool honorSuppression)
    {
        if (honorSuppression && LauncherUpdatePolicy.IsSuppressedPreviewTag(release.TagName,
            ConfigService.Active?.Config.SuppressedLauncherPreviewTags))
            return new(CheckStatus.Skipped, $"您已选择不再提示预览版 {release.TagName}。");
        if (previewStillEnabled?.Invoke() == false) return new(CheckStatus.Skipped, "本次启动检查已关闭。");

        string? unavailable = null;
        if (!updatesEnabled()) unavailable = "下载安装已在设置中关闭。您仍可查看版本，启用并保存后才能安装。";
        else if (asset == null) unavailable = "发布中没有当前系统架构对应的 EXE 更新包，暂不能安装。";
        else
        {
            try { _ = LauncherUpdateMetadata.FromAsset(asset.Size, asset.Digest); }
            catch (Exception ex) { unavailable = "此更新包暂不能自动安装：" + ex.Message; }
        }
        var changelog = string.IsNullOrWhiteSpace(release.Body) ? "（作者未填写更新日志）" : release.Body.Trim();
        var choice = await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var dialog = new LauncherPreviewUpdateDialog(release.TagName, FormatVersion(local), changelog, unavailable);
            await OverlayDialogService.ShowModalAsync(dialog);
            return dialog.Choice;
        }).Task.Unwrap();
        if (choice == LauncherPreviewChoice.SuppressVersion)
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var active = ConfigService.Active;
                if (active == null) return new CheckResult(CheckStatus.Failed, "配置已关闭，无法保存此预览版本的提醒偏好。");
                active.Config.SuppressedLauncherPreviewTags ??= new();
                var added = !LauncherUpdatePolicy.IsSuppressedPreviewTag(release.TagName, active.Config.SuppressedLauncherPreviewTags);
                if (added) active.Config.SuppressedLauncherPreviewTags.Add(release.TagName);
                try { active.Save(); }
                catch
                {
                    if (added) active.Config.SuppressedLauncherPreviewTags.Remove(release.TagName);
                    throw;
                }
                return new CheckResult(CheckStatus.Skipped, $"已保存：不再自动提示预览版 {release.TagName}，其他版本仍会提醒。");
            });
        }
        if (choice != LauncherPreviewChoice.Install)
            return new(CheckStatus.UpdateAvailable, honorSuppression
                ? $"已忽略本次预览版 {release.TagName} 提醒，下次启动仍会检查。"
                : $"本次未安装预览版 {release.TagName}。启用用户体验改进计划后，会在后续启动时继续检查。");
        if (!LauncherUpdatePolicy.CanInstall(updatesEnabled(), true))
            return new(CheckStatus.UpdatesDisabled, "下载安装已关闭，本次不会安装预览版。");
        if (previewStillEnabled?.Invoke() == false) return new(CheckStatus.Skipped, "本次启动检查已关闭，未安装预览版。");
        if (asset == null || unavailable != null) return new(CheckStatus.Failed, unavailable ?? "没有对应架构的更新包。");
        // Reuse the size/SHA256/PE/architecture verification, atomic replace, backup and rollback transaction.
        return await DownloadAndApplyUpdateAsync(remote, local, asset, changelog, updatesEnabled, previewStillEnabled);
    }
}
