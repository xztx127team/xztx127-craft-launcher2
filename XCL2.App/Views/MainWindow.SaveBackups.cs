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
        if (_openingSaveBackupPicker || _openingSaveBackupDialog) { ToastService.ShowInfo("存档备份窗口正在打开，请稍候。"); return; }
        _openingSaveBackupPicker = true;
        try
        {
            var worlds = await Task.Run(() => WorldArchiveService.Discover(new[] { gameDirectory }));
            if (_isWindowClosed) return;
            if (worlds.Count == 0) { ToastService.ShowInfo("此实例暂时没有 Java 版存档，可在游戏中创建世界后再备份。"); return; }
            if (worlds.Count == 1) { OpenSaveBackups(worlds[0].Path); return; }
            var body = new StackPanel { Margin = new Thickness(20) };
            body.Children.Add(new TextBlock { Text = "选择要备份的存档", FontSize = 18, Margin = new Thickness(0, 0, 0, 12) });
            body.Children.Add(new TextBlock { Text = "选择存档后点击“管理备份”，也可以双击存档直接打开。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            var list = new ListBox { ItemsSource = worlds, DisplayMemberPath = "Settings.Name", SelectedIndex = 0, MaxHeight = Math.Clamp(ActualHeight - 260, 100, 300), MinWidth = 280 };
            body.Children.Add(list);
            var dialog = new OverlayDialogControl { Content = body };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var open = new Button { Content = "管理备份", MinWidth = 92, Margin = new Thickness(0, 0, 8, 0), IsEnabled = list.SelectedItem is WorldArchive, ToolTip = "查看所选存档的备份、恢复副本或设置自动备份。" };
            var close = new Button { Content = "取消", MinWidth = 72 };
            open.SetResourceReference(StyleProperty, "CompactActionButton"); close.SetResourceReference(StyleProperty, "CompactActionButton");
            WorldArchive? chosen = null;
            void OpenSelected()
            {
                if (list.SelectedItem is not WorldArchive selected) return;
                chosen = selected; open.IsEnabled = false; dialog.Close();
            }
            list.SelectionChanged += (_, _) => open.IsEnabled = list.SelectedItem is WorldArchive;
            list.MouseDoubleClick += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) is ListBoxItem) { OpenSelected(); e.Handled = true; }
            };
            open.Click += (_, _) => OpenSelected();
            close.Click += (_, _) => dialog.Close(); actions.Children.Add(open); actions.Children.Add(close); body.Children.Add(actions);
            await OverlayDialogService.ShowModalAsync(dialog);
            if (chosen != null && !_isWindowClosed) OpenSaveBackups(chosen.Path);
        }
        catch (Exception ex) { ToastService.ShowWarning("无法读取存档：" + ex.Message); }
        finally { _openingSaveBackupPicker = false; }
    }
    public async void OpenSaveBackups(string worldPath)
    {
        if (_openingSaveBackupDialog) { ToastService.ShowInfo("存档备份窗口已打开，请先完成当前操作。"); return; }
        _openingSaveBackupDialog = true;
        try
        {
            var world = await Task.Run(() =>
            {
                var path = Path.GetFullPath(worldPath);
                WorldArchiveService.RejectLinks(path);
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException("存档目录不存在：" + path);
                try
                {
                    var archive = WorldArchiveService.Read(path);
                    return (Path: archive.Path, Name: archive.Settings.Name, MetadataWarning: (string?)null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException)
                {
                    ErrorPresenter.LogFallback("存档元数据无法读取，仍打开完整备份管理：" + path, ex);
                    var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                    return (Path: path, Name: string.IsNullOrWhiteSpace(name) ? path : name,
                        MetadataWarning: "当前存档元数据无法读取：" + ex.Message + "。仍可从完整备份恢复副本。");
                }
            });
            if (_isWindowClosed) return;
            await OverlayDialogService.ShowModalAsync(new SaveBackupsDialog(this, world.Path, world.Name, world.MetadataWarning));
        }
        catch (Exception ex) { ToastService.ShowWarning("无法打开存档备份：" + ex.Message); }
        finally { _openingSaveBackupDialog = false; }
    }
}
