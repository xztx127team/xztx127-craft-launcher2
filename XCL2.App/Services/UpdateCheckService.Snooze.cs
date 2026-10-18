using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XCL2.App.Views;

namespace XCL2.App.Services;

public static partial class UpdateCheckService
{
    private static DispatcherTimer? _snoozeTimer;
    private static Version? _latestReminderVersion;

    private static void PublishUpdateReminder(Version remote, Version local, Func<bool> enabled)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(new Action(() => PublishUpdateReminder(remote, local, enabled))); return; }
        if (_latestReminderVersion != null && remote < _latestReminderVersion) return;
        _latestReminderVersion = remote;
        _snoozeTimer?.Stop(); _snoozeTimer = null;
        var version = remote.ToString();
        var key = "launcher-update-" + version;
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
                    PublishUpdateReminder(remote, local, enabled); return Task.CompletedTask;
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
                { ReminderCenterService.AllowAgain(key); ReminderCenterService.Remove(key); PublishUpdateReminder(remote, local, enabled); }
            };
            _snoozeTimer.Start();
            return;
        }
        ReminderCenterService.Publish(key, "启动器有新版本",
            $"当前 v{FormatVersion(local)}，可更新到 v{FormatVersion(remote)}。查看后再决定是否安装。",
            "查看更新", async () =>
            {
                if (!enabled()) { ToastService.ShowInfo("启动器更新已在设置中关闭。"); return; }
                var result = await CheckForUpdateAsync(enabled);
                if (result.Status is CheckStatus.Failed or CheckStatus.Busy) ToastService.ShowWarning(result.Message);
            }, "稍后提醒…", () => { ShowSnoozeOptions(remote, local, enabled); return Task.CompletedTask; });
    }

    private static void ShowSnoozeOptions(Version remote, Version local, Func<bool> enabled)
    {
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
                PublishUpdateReminder(remote, local, enabled); dialog.Close();
            };
            buttons.Children.Add(button);
        }
        var cancel = new Button { Content = "取消", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 0, 8) };
        cancel.Click += (_, _) => dialog.Close(); buttons.Children.Add(cancel); root.Children.Add(buttons);
        dialog.Content = root; OverlayDialogService.ShowNonModal(dialog);
    }
}
