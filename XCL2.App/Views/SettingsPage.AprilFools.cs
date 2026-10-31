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
            ToolTip = "直接打开闯关窗口中的彩蛋码输入区；右侧可查看本次启动密码"
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
            using var challenge = new AprilFoolsExitChallengeDialog(_owner, focusCodeEntry: promptForCode);
            if (await OverlayDialogService.ShowModalAsync(challenge) != true || !IsLoaded) return;
            var cfg = _owner.ConfigService.Config;
            var previous = cfg.EnableAprilFoolsEasterEggs;
            cfg.EnableAprilFoolsEasterEggs = false;
            _uxSavingSettings = true;
            try { _owner.ConfigService.Save(); }
            catch { cfg.EnableAprilFoolsEasterEggs = previous; throw; }
            finally { _uxSavingSettings = false; }
            _applyingAprilFoolsClose = true;
            try { RunWithoutDirtyTracking(() => _aprilFoolsToggle!.IsChecked = false); }
            finally { _applyingAprilFoolsClose = false; }
            MarkAprilFoolsCloseAsSaved();
            AprilFoolsService.DismissForToday();
            UpdateUxChangeSummary();
            OnSettingsEdited();
            ToastService.ShowSuccess("愚人节彩蛋已关闭并保存，本次整蛊已停止。");
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("打开愚人节关闭确认失败", ex);
            ToastService.ShowWarning("关闭确认未完成，总开关保持开启；可用小白旗或 F1 结束今天的整蛊。");
        }
        finally { _aprilFoolsCloseFlowOpen = false; }
    }

    private void MarkAprilFoolsCloseAsSaved()
    {
        // Only this explicit close action is committed; other settings drafts and their rollback
        // state keep following the user's chosen manual/automatic saving behavior.
        var name = _aprilFoolsToggle!.Name;
        var current = BuildSettingsUiFingerprint();
        bool Affected(string line)
        {
            var fields = line.Split('|');
            return fields.Length > 1 && fields[1] == name;
        }
        _lastSavedUiFingerprint = string.Join("\n", _lastSavedUiFingerprint.Split('\n').Where(line => !Affected(line))
            .Concat(current.Split('\n').Where(Affected)).OrderBy(line => line, StringComparer.Ordinal));
        _lastPolledFingerprint = current;
        if (_preAutoSaveSnapshotJson != null)
        {
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<XCL2.App.Models.AppConfig>(_preAutoSaveSnapshotJson);
            if (snapshot != null)
            {
                snapshot.EnableAprilFoolsEasterEggs = false;
                _preAutoSaveSnapshotJson = System.Text.Json.JsonSerializer.Serialize(snapshot);
            }
        }
        _hasUnsavedChanges = !string.Equals(current, _lastSavedUiFingerprint, StringComparison.Ordinal);
        if (!_hasUnsavedChanges) ToastService.DismissActionPrompt("settings-dirty");
        SyncUxExternallySavedControls(new[] { name });
    }
}
