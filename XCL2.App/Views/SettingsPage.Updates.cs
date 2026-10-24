using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private bool _checkingLauncherUpdate;
    private Button? _globalReminderButton;

    private void LauncherUpdateSettings_Changed(object sender, RoutedEventArgs e)
        => UpdateLauncherUpdateControls(resetStatus: !_checkingLauncherUpdate);

    private void UpdateLauncherUpdateControls(bool resetStatus = false)
    {
        // XAML 初始化时 Checked 可早于后面的 Button/TextBlock 创建。
        if (CheckLauncherUpdateBtn == null || LauncherUpdateStatusText == null) return;
        EnsureGlobalUpdateReminderControls();
        var canInstall = EnableLauncherUpdatesCheck.IsChecked == true &&
                         _owner.ConfigService.Config.EnableLauncherUpdates;
        CheckLauncherUpdateBtn.Content = _checkingLauncherUpdate
            ? "正在检查更新…" : canInstall ? "检查更新" : "检查更新（仅查看版本）";
        CheckLauncherUpdateBtn.IsEnabled = !_checkingLauncherUpdate;
        CheckLauncherUpdateBtn.ToolTip = canInstall
            ? "从 GitHub Releases API 获取最新正式版及快照/预览版；发现更新后由您选择是否下载安装。"
            : "下载安装已关闭或尚未保存启用设置，仍可查看 API 上的最新版本。";
        if (resetStatus)
            LauncherUpdateStatusText.Text = $"当前启动器版本 v{BuildInfo.Version}。" +
                (canInstall ? "发现更新后会先征求您的确认。" : "更新安装已关闭；启用并保存后才可安装。");
    }

    private async void CheckLauncherUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingLauncherUpdate) return;
        // 未保存的“关闭安装”也尊重；启用安装须先保存，避免绕过设置页保存语义。
        _checkingLauncherUpdate = true;
        UpdateLauncherUpdateControls();
        LauncherUpdateStatusText.Text = "正在向更新 API 查询最新版本…";
        try
        {
            var result = await UpdateCheckService.CheckForUpdateAsync(
                () => Dispatcher.Invoke(() => EnableLauncherUpdatesCheck.IsChecked == true &&
                    _owner.ConfigService.Config.EnableLauncherUpdates));
            LauncherUpdateStatusText.Text = result.Message;
            if (result.Status == UpdateCheckService.CheckStatus.Failed && !result.ErrorAlreadyShown)
                await MessageBoxDialog.ShowErrorAsync(result.Message, "检查更新失败");
        }
        catch (Exception ex)
        {
            LauncherUpdateStatusText.Text = "检查更新失败：" + ex.Message;
            await MessageBoxDialog.ShowErrorAsync(LauncherUpdateStatusText.Text, "检查更新失败");
        }
        finally
        {
            _checkingLauncherUpdate = false;
            UpdateLauncherUpdateControls();
        }
    }
    private void EnsureGlobalUpdateReminderControls()
    {
        if (_globalReminderButton != null || LauncherUpdateStatusText.Parent is not Panel parent) return;
        _globalReminderButton = new Button
        {
            Content = "正式版全局提醒设置…", HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(0, 0, 0, 10)
        };
        _globalReminderButton.Click += (_, _) => ShowGlobalUpdateReminderOptions();
        parent.Children.Insert(parent.Children.IndexOf(LauncherUpdateStatusText), _globalReminderButton);
    }

    private void ShowGlobalUpdateReminderOptions()
    {
        var previous = LauncherUpdatePreferences.Read();
        var dialog = new OverlayDialogControl { MinWidth = 340, MaxWidth = 580 };
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "正式版全局提醒设置", FontSize = 18, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = "此设置写入当前用户的注册表，同一 Windows 账户下的启动器共用。手动检查仍然可用；必须安装的更新以及已过期限的更新不受此设置影响。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) });
        var mode = new ComboBox { Margin = new Thickness(0, 0, 0, 10) };
        var labels = new[] { "正常提醒", "延时提醒", "暂停自动更新提醒", "指定时段内仅角标提醒" };
        foreach (var label in labels) mode.Items.Add(label);
        mode.SelectedIndex = Enum.IsDefined(previous.Mode) ? (int)previous.Mode : 0;
        root.Children.Add(mode);
        root.Children.Add(new TextBlock { Text = "开始时间（本地时间，可留空表示现在）", Margin = new Thickness(0, 0, 0, 4) });
        var start = new TextBox { Text = previous.StartsAt?.LocalDateTime.ToString("yyyy/MM/dd HH:mm") ?? "",
            Margin = new Thickness(0, 0, 0, 8) };
        root.Children.Add(start);
        root.Children.Add(new TextBlock { Text = "恢复时间（yyyy/MM/dd HH:mm；暂停可留空）", Margin = new Thickness(0, 0, 0, 4) });
        var end = new TextBox { Text = previous.EndsAt?.LocalDateTime.ToString("yyyy/MM/dd HH:mm") ?? "",
            Margin = new Thickness(0, 0, 0, 8) };
        root.Children.Add(end);
        var presets = new WrapPanel();
        foreach (var option in new[] { ("1 小时后", TimeSpan.FromHours(1)), ("明天", TimeSpan.FromDays(1)), ("7 天后", TimeSpan.FromDays(7)) })
        {
            var button = new Button { Content = option.Item1, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 8) };
            button.Click += (_, _) => { start.Text = ""; end.Text = DateTime.Now.Add(option.Item2).ToString("yyyy/MM/dd HH:mm"); if (mode.SelectedIndex == 0) mode.SelectedIndex = 1; };
            presets.Children.Add(button);
        }
        root.Children.Add(presets);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
        root.Children.Add(status);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => dialog.Close(); buttons.Children.Add(cancel);
        var save = new Button { Content = "保存全局设置", Padding = new Thickness(12, 6, 12, 6) };
        save.Click += (_, _) =>
        {
            var selected = (LauncherReminderMode)Math.Max(0, mode.SelectedIndex);
            DateTimeOffset? from = null, until = null;
            if (selected != LauncherReminderMode.Normal)
            {
                if (!string.IsNullOrWhiteSpace(start.Text))
                {
                    if (!DateTime.TryParseExact(start.Text.Trim(), "yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal, out var parsed)) { status.Text = "开始时间格式不正确。"; return; }
                    from = new DateTimeOffset(parsed);
                }
                if (!string.IsNullOrWhiteSpace(end.Text))
                {
                    if (!DateTime.TryParseExact(end.Text.Trim(), "yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal, out var parsed)) { status.Text = "恢复时间格式不正确。"; return; }
                    until = new DateTimeOffset(parsed);
                }
                if ((selected is LauncherReminderMode.Snooze or LauncherReminderMode.BadgeOnly) && until == null)
                { status.Text = "请填写恢复时间，或选择一个快捷时间。"; return; }
                if (until.HasValue && (until.Value <= (from ?? DateTimeOffset.Now) || until.Value <= DateTimeOffset.Now))
                { status.Text = "恢复时间必须晚于开始时间和当前时间。"; return; }
            }
            if (!LauncherUpdatePreferences.Save(new(selected, from, until)))
            { status.Text = "注册表写入失败，未保存设置，请检查当前账户权限。"; return; }
            UpdateCheckService.RefreshGlobalReminderPreference();
            ToastService.ShowInfo("已保存正式版全局提醒设置。"); dialog.Close();
        };
        buttons.Children.Add(save); root.Children.Add(buttons); dialog.Content = root;
        OverlayDialogService.ShowNonModal(dialog);
    }

}
