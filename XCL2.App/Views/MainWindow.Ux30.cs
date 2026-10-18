using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private WindowPlacementService? _launcherWindowPlacement;
    private IDisposable? _taskResultsSubscription;

    private void InitializeUxPresentation()
    {
        var temporary = App.StartupArgs.WindowWidth.HasValue || App.StartupArgs.WindowHeight.HasValue;
        _launcherWindowPlacement = WindowPlacementService.Attach(this, ConfigService, temporary);
        Closed += (_, _) => _taskResultsSubscription?.Dispose();
    }

    public bool ResetLauncherLayout()
    {
        if (!MessageBoxDialog.ShowConfirm("恢复窗口位置、搜索框位置与下载面板大小？账号、实例和其他设置不受影响。", "恢复界面布局")) return false;
        LayoutResetService.Reset(ConfigService.Config);
        EndFeatureSearchPreview();
        DownloadQueuePopup.IsOpen = false;
        DownloadQueuePopupBorder.Width = 380;
        DownloadQueuePopupBorder.Height = Math.Min(ConfigService.Config.DownloadPopupShowDetailStats ? 576 : 340,
            Math.Max(240, ActualHeight - 80));
        _launcherWindowPlacement?.Reset();
        ConfigService.Save();
        RefreshFeatureSearchForHostResize();
        ToastService.ShowSuccess("界面布局已恢复");
        return true;
    }

    private void InitializeLaunchSummary()
    {
        foreach (var button in new[] { LaunchGameBtn, LaunchGameBtnCollapsed })
        {
            var menu = button.ContextMenu ??= new ContextMenu();
            var item = new MenuItem { Header = "查看启动摘要" };
            item.Click += (_, _) => OpenLaunchSummary(); menu.Items.Add(item);
            var help = new MenuItem { Header = "实例管理帮助（离线）" };
            help.Click += (_, _) => OfflineHelpService.Show("instances"); menu.Items.Add(help);
            button.ToolTip = "启动游戏；右键或按 Shift+F10 查看启动摘要";
        }
    }

    public void OpenLaunchSummary()
    {
        try
        {
            var config = LaunchSummaryService.Capture(ConfigService.Config);
            var account = ConfigService.GetSelectedAccount();
            var accountLabel = account?.DisplayLabel ?? "尚未选择账户";
            var loginStatus = AccountLoginStatus.Describe(account, DateTime.UtcNow);
            var min = App.StartupArgs.MinMemoryMb; var max = App.StartupArgs.MaxMemoryMb;
            new LaunchSummaryDialog(ct => Task.Run(() =>
                LaunchSummaryService.Read(config, accountLabel, loginStatus, min, max, ct), ct)).Show();
        }
        catch (Exception ex) { ErrorPresenter.ShowFriendlyError("无法读取启动摘要，请检查所选实例。", ex.ToString()); }
    }
}
