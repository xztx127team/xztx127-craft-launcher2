using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    /// <summary>悬浮窗右键的主动导航，沿用主窗口的未保存设置保护。</summary>
    public void OpenPerformanceOverlaySettings()
    {
        if (!NavSettingsButton.IsEnabled)
        {
            ToastService.ShowInfo("当前模式下未开放设置页面。");
            return;
        }
        RestoreFromTray();
        if (MainContent.Content is SettingsPage settings)
        {
            settings.RevealPerformanceSettings();
            return;
        }
        NavigateLazy(() => new SettingsPage(this), page => ((SettingsPage)page).RevealPerformanceSettings());
    }
}
