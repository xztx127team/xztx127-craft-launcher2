using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private CheckBox? _aprilFoolsToggle;
    private bool _aprilFoolsCloseFlowOpen;
    private bool _applyingAprilFoolsClose;

    private void InitializeAprilFoolsCloseControls(CheckBox toggle, StackPanel body)
    {
        _aprilFoolsToggle = toggle;
        toggle.Unchecked += (_, e) =>
        {
            if (_suppressDirtyTracking || _uxResetInProgress || _applyingAprilFoolsClose) return;
            e.Handled = true;
            // Keep the saved value enabled while the overlay is open: auto-save and
            // the settings poll timer must never observe an unconfirmed false value.
            RunWithoutDirtyTracking(() => toggle.IsChecked = true);
            RequestAprilFoolsClose(promptForCode: false);
        };
        var codeButton = new Button
        {
            Name = "AprilFoolsCodeButton", Content = "输入彩蛋码",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 0, 12),
            ToolTip = "验证彩蛋码后，在本次关闭窗口中跳过文字游戏和闯关"
        };
        codeButton.SetResourceReference(StyleProperty, "SecondaryButton");
        codeButton.Click += (_, _) => RequestAprilFoolsClose(promptForCode: true);
        body.Children.Add(codeButton);
    }

    private void RequestAprilFoolsClose(bool promptForCode)
    {
        if (_aprilFoolsCloseFlowOpen || !IsLoaded || Dispatcher.HasShutdownStarted) return;
        if (_aprilFoolsToggle?.IsChecked != true)
        {
            ToastService.ShowInfo("愚人节彩蛋已设为关闭。");
            return;
        }
        _aprilFoolsCloseFlowOpen = true;
        // Finish the toggle's routed event before opening an overlay.
        _ = Dispatcher.BeginInvoke(new Action(async () => await ConfirmAprilFoolsCloseAsync(promptForCode)));
    }

    private async Task ConfirmAprilFoolsCloseAsync(bool promptForCode)
    {
        try
        {
            if (!IsLoaded || _suppressDirtyTracking) return;
            string? easterEggCode = null;
            if (promptForCode)
            {
                var input = new AprilFoolsCodeDialog(_owner);
                if (await OverlayDialogService.ShowModalAsync(input) != true || !IsLoaded) return;
                easterEggCode = input.Code;
            }
            using var challenge = new AprilFoolsExitChallengeDialog(_owner, easterEggCode);
            if (await OverlayDialogService.ShowModalAsync(challenge) != true || !IsLoaded) return;
            _applyingAprilFoolsClose = true;
            try { RunWithoutDirtyTracking(() => _aprilFoolsToggle!.IsChecked = false); }
            finally { _applyingAprilFoolsClose = false; }
            AprilFoolsService.DismissForToday();
            UpdateUxChangeSummary();
            OnSettingsEdited();
            ToastService.ShowSuccess("本次整蛊已停止。关闭总开关的更改将按当前设置的保存方式生效。");
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("打开愚人节关闭确认失败", ex);
            ToastService.ShowWarning("关闭确认未完成，总开关保持开启；可用小白旗或 F1 结束今天的整蛊。");
        }
        finally { _aprilFoolsCloseFlowOpen = false; }
    }

    private sealed class AprilFoolsCodeDialog : OverlayDialogControl
    {
        internal string? Code { get; private set; }

        internal AprilFoolsCodeDialog(MainWindow owner)
        {
            Width = Math.Min(480, Math.Max(300, owner.ActualWidth - 100));
            MaxHeight = Math.Max(260, owner.ActualHeight - 90);
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock { Text = "输入彩蛋码", FontSize = 20, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock
            {
                Text = "验证成功后将打开关闭确认窗口，可直接关闭愚人节彩蛋。本次取消后需重新输入。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12)
            });
            var input = new TextBox { MaxLength = 64, Padding = new Thickness(8), MinHeight = 32 };
            System.Windows.Automation.AutomationProperties.SetName(input, "彩蛋码");
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            error.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            void Verify()
            {
                if (!AprilFoolsExitChallengeState.IsShortcutCode(input.Text))
                {
                    error.Text = "彩蛋码不正确，请重新输入。";
                    error.Visibility = Visibility.Visible;
                    input.SelectAll(); input.Focus();
                    return;
                }
                Code = input.Text.Trim();
                CloseWith(true);
            }
            input.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Verify(); } };
            panel.Children.Add(input); panel.Children.Add(error);
            var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "取消", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
            var confirm = new Button { Content = "验证并打开关闭窗口", Padding = new Thickness(12, 6, 12, 6) };
            cancel.SetResourceReference(StyleProperty, "SecondaryButton");
            confirm.SetResourceReference(StyleProperty, "PrimaryButton");
            cancel.Click += (_, _) => CloseWith(false); confirm.Click += (_, _) => Verify();
            actions.Children.Add(cancel); actions.Children.Add(confirm); panel.Children.Add(actions);
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Loaded += (_, _) => input.Focus();
        }
    }
}
