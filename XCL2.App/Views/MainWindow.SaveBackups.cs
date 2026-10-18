using System.IO;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;
using XCL2.App.Services.Worlds;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private ScheduledSaveBackupService? _scheduledSaveBackupService;
    private bool _openingSaveBackupPicker, _openingSaveBackupDialog;
    // Called once after first render; opt-in schedules do not run in guest startup.
    private void InitializeSaveBackupSchedules()
    {
        if (ConfigService.Config.GuestModeEnabled) return;
        _scheduledSaveBackupService ??= new ScheduledSaveBackupService(this);
        Closed += (_, _) => { _scheduledSaveBackupService?.Dispose(); _scheduledSaveBackupService = null; };
    }
    public async void OpenSaveBackupFolder(string gameDirectory)
    {
        if (_openingSaveBackupPicker || _openingSaveBackupDialog) return;
        _openingSaveBackupPicker = true;
        try
        {
            var worlds = await Task.Run(() => WorldArchiveService.Discover(new[] { gameDirectory }));
            if (_isWindowClosed) return;
            if (worlds.Count == 0) { ToastService.ShowInfo("此实例暂时没有 Java 版存档，可在游戏中创建世界后再备份。"); return; }
            if (worlds.Count == 1) { OpenSaveBackups(worlds[0].Path); return; }
            var body = new StackPanel { Margin = new Thickness(20) };
            body.Children.Add(new TextBlock { Text = "选择要备份的存档", FontSize = 18, Margin = new Thickness(0, 0, 0, 12) });
            var list = new ListBox { ItemsSource = worlds, DisplayMemberPath = "Settings.Name", MaxHeight = Math.Clamp(ActualHeight - 260, 100, 300), MinWidth = 280 };
            body.Children.Add(list);
            var dialog = new OverlayDialogControl { Content = body };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var open = new Button { Content = "管理备份", MinWidth = 92, Margin = new Thickness(0, 0, 8, 0) };
            var close = new Button { Content = "取消", MinWidth = 72 };
            WorldArchive? chosen = null;
            open.Click += (_, _) => { if (list.SelectedItem is WorldArchive selected) { chosen = selected; open.IsEnabled = false; dialog.Close(); } };
            close.Click += (_, _) => dialog.Close(); actions.Children.Add(open); actions.Children.Add(close); body.Children.Add(actions);
            dialog.ShowDialog();
            if (chosen != null && !_isWindowClosed) OpenSaveBackups(chosen.Path);
        }
        catch (Exception ex) { ToastService.ShowWarning("无法读取存档：" + ex.Message); }
        finally { _openingSaveBackupPicker = false; }
    }
    public async void OpenSaveBackups(string worldPath)
    {
        if (_openingSaveBackupDialog) return;
        _openingSaveBackupDialog = true;
        try
        {
            var world = await Task.Run(() => WorldArchiveService.Read(worldPath));
            if (_isWindowClosed) return;
            new SaveBackupsDialog(this, world.Path, world.Settings.Name).ShowDialog();
        }
        catch (Exception ex) { ToastService.ShowWarning("无法打开存档备份：" + ex.Message); }
        finally { _openingSaveBackupDialog = false; }
    }
}
