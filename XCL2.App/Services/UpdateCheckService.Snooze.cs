using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XCL2.App.Views;

namespace XCL2.App.Services;

public static partial class UpdateCheckService
{
    private static DispatcherTimer? _snoozeTimer;
    private static Version? _latestReminderVersion;
    private static LauncherReleaseInstructions? _latestReminderInstructions;
    private static DispatcherTimer? _globalPreferenceTimer;
    private static LauncherReminderMode _lastGlobalReminderMode;

    private static void PublishUpdateReminder(Version remote, Version local, Func<bool> enabled, LauncherReleaseInstructions? instructions = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(new Action(() => PublishUpdateReminder(remote, local, enabled, instructions))); return; }
        if (_latestReminderVersion != null && remote < _latestReminderVersion) return;
        _latestReminderVersion = remote;
        _snoozeTimer?.Stop(); _snoozeTimer = null;
        var version = remote.ToString();
        var key = "launcher-update-" + version;
        instructions ??= LauncherReleaseInstructions.Default;
        _latestReminderInstructions = instructions;
        if (instructions.IsRequired(DateOnly.FromDateTime(DateTime.Now)))
        {
            ReminderCenterService.Remove(key);
            _ = EnforceCachedRequiredUpdateAsync();
            return;
        }
        var global = LauncherUpdatePreferences.Read();
        _lastGlobalReminderMode = global.EffectiveMode(DateTimeOffset.Now);
        if (_globalPreferenceTimer == null)
        {
            _globalPreferenceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _globalPreferenceTimer.Tick += (_, _) =>
            {
                if (_lastGlobalReminderMode != LauncherUpdatePreferences.Read().EffectiveMode(DateTimeOffset.Now))
                    RefreshGlobalReminderPreference();
            };
            _globalPreferenceTimer.Start();
        }
        if (global.SuppressAutomaticReminder(DateTimeOffset.Now, instructions, DateOnly.FromDateTime(DateTime.Now)))
        {
            ReminderCenterService.Remove(key);
            if (global.EndsAt is { } resumes)
            {
                var delay = resumes - DateTimeOffset.Now;
                if (delay > TimeSpan.FromDays(7)) delay = TimeSpan.FromDays(7);
                _snoozeTimer = new DispatcherTimer { Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1) };
                _snoozeTimer.Tick += (_, _) =>
                {
                    _snoozeTimer?.Stop();
                    PublishUpdateReminder(remote, local, enabled, instructions);
                };
                _snoozeTimer.Start();
            }
            return;
        }
        var config = ConfigService.Active?.Config;
        if (config != null && config.Ux50UpdateReminderSnoozes != null
            && config.Ux50UpdateReminderSnoozes.TryGetValue(version, out var until) && until > DateTimeOffset.UtcNow)
        {
            ReminderCenterService.Publish(key, "已暂缓本版本更新提醒",
                $"v{FormatVersion(remote)} 将在 {PresentationPreferencesService.FormatDateTime(until)} 后恢复提醒。主动检查更新仍然可用。",
                "取消暂缓", () =>
                {
                    var active = ConfigService.Active;
                    active?.Config.Ux50UpdateReminderSnoozes?.Remove(version); active?.Save();
                    ReminderCenterService.AllowAgain(key); ReminderCenterService.Remove(key);
                    PublishUpdateReminder(remote, local, enabled, instructions); return Task.CompletedTask;
                }, initiallyRead: true);
            _snoozeTimer?.Stop();
            var delay = until - DateTimeOffset.UtcNow;
            // Imported hand-edited dates must not overflow WPF's millisecond timer range.
            if (delay > TimeSpan.FromDays(7)) delay = TimeSpan.FromDays(7);
            _snoozeTimer = new DispatcherTimer { Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1) };
            _snoozeTimer.Tick += (_, _) =>
            {
                _snoozeTimer?.Stop();
                // Refresh the local badge only. A snooze expiry never starts a network request or download.
                if (enabled())
                { ReminderCenterService.AllowAgain(key); ReminderCenterService.Remove(key); PublishUpdateReminder(remote, local, enabled, instructions); }
            };
            _snoozeTimer.Start();
            return;
        }
        ReminderCenterService.Publish(key, "启动器有新版本",
            $"当前 v{FormatVersion(local)}，可更新到 v{FormatVersion(remote)}。" +
            (instructions.Deadline is { } deadline ? $"请在 {deadline:yyyy/MM/dd} 当日结束前安装，逾期将无法跳过。" : "查看后再决定是否安装。"),
            "查看更新", async () =>
            {
                if (!enabled()) { ToastService.ShowInfo("启动器更新已在设置中关闭。"); return; }
                var result = await CheckForUpdateAsync(enabled);
                if (result.Status is CheckStatus.Failed or CheckStatus.Busy) ToastService.ShowWarning(result.Message);
            }, "稍后提醒…", () => { ShowSnoozeOptions(remote, local, enabled, instructions); return Task.CompletedTask; });
    }

    public static void RefreshGlobalReminderPreference()
    {
        if (_latestReminderVersion == null) return;
        var remote = _latestReminderVersion;
        var local = GetCurrentReleaseVersion().Core;
        ReminderCenterService.AllowAgain("launcher-update-" + remote);
        ReminderCenterService.Remove("launcher-update-" + remote);
        PublishUpdateReminder(remote, local, () => ConfigService.Active?.Config.EnableLauncherUpdates == true, _latestReminderInstructions);
    }

    private static void ShowSnoozeOptions(Version remote, Version local, Func<bool> enabled, LauncherReleaseInstructions instructions)
    {
        if (instructions.IsRequired(DateOnly.FromDateTime(DateTime.Now)))
        {
            ToastService.ShowWarning("此更新已到安装期限，无法再暂缓。");
            _ = EnforceCachedRequiredUpdateAsync();
            return;
        }
        if (ConfigService.Active == null) return;
        var dialog = new OverlayDialogControl { MinWidth = 300, MaxWidth = 500 };
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = $"暂缓 v{FormatVersion(remote)} 的提醒", FontSize = 17, FontWeight = FontWeights.Bold });
        root.Children.Add(new TextBlock { Text = "仅影响这个启动器版本的角标提醒。手动检查、其他版本及模组更新设置均不改变。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        var buttons = new WrapPanel();
        foreach (var option in new[] { ("1 小时", TimeSpan.FromHours(1)), ("1 天", TimeSpan.FromDays(1)), ("7 天", TimeSpan.FromDays(7)) })
        {
            var button = new Button { Content = option.Item1, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 8) };
            button.Click += (_, _) =>
            {
                if (instructions.IsRequired(DateOnly.FromDateTime(DateTime.Now)))
                {
                    dialog.Close(); _ = EnforceCachedRequiredUpdateAsync(); return;
                }
                var active = ConfigService.Active;
                if (active == null) { dialog.Close(); return; }
                var config = active.Config;
                config.Ux50UpdateReminderSnoozes ??= new();
                foreach (var expired in config.Ux50UpdateReminderSnoozes.Where(x => x.Value <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray())
                    config.Ux50UpdateReminderSnoozes.Remove(expired);
                while (config.Ux50UpdateReminderSnoozes.Count >= 20)
                    config.Ux50UpdateReminderSnoozes.Remove(config.Ux50UpdateReminderSnoozes.OrderBy(x => x.Value).First().Key);
                config.Ux50UpdateReminderSnoozes[remote.ToString()] = DateTimeOffset.UtcNow + option.Item2;
                active.Save();
                ReminderCenterService.AllowAgain("launcher-update-" + remote);
                ReminderCenterService.Remove("launcher-update-" + remote);
                PublishUpdateReminder(remote, local, enabled, instructions); dialog.Close();
            };
            buttons.Children.Add(button);
        }
        var cancel = new Button { Content = "取消", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 0, 8) };
        cancel.Click += (_, _) => dialog.Close(); buttons.Children.Add(cancel); root.Children.Add(buttons);
        dialog.Content = root; OverlayDialogService.ShowNonModal(dialog);
    }
}
