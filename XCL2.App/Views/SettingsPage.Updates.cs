using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private bool _checkingLauncherUpdate;

    private void LauncherUpdateSettings_Changed(object sender, RoutedEventArgs e)
        => UpdateLauncherUpdateControls(resetStatus: !_checkingLauncherUpdate);

    private void UpdateLauncherUpdateControls(bool resetStatus = false)
    {
        // XAML 初始化时 Checked 可早于后面的 Button/TextBlock 创建。
        if (CheckLauncherUpdateBtn == null || LauncherUpdateStatusText == null) return;
        var canInstall = EnableLauncherUpdatesCheck.IsChecked == true &&
                         _owner.ConfigService.Config.EnableLauncherUpdates;
        CheckLauncherUpdateBtn.Content = _checkingLauncherUpdate
            ? "正在检查更新…" : canInstall ? "检查更新" : "检查更新（仅查看版本）";
        CheckLauncherUpdateBtn.IsEnabled = !_checkingLauncherUpdate;
        CheckLauncherUpdateBtn.ToolTip = canInstall
            ? "从 GitHub Releases API 获取最新版本；发现更新后由您选择是否下载安装。"
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
}
