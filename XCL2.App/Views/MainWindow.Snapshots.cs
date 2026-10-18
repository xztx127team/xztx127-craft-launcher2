using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private bool _restoringModSnapshot;
    public async void OpenModUpdateSnapshots()
    {
        var directory = Path.Combine(App.DataDir, "mod_update_snapshots");
        var store = new ModUpdateSnapshotStore(directory);
        IReadOnlyList<ModUpdateSnapshot> snapshots;
        try { snapshots = await Task.Run(store.List); }
        catch (Exception ex) { ToastService.ShowWarning("无法读取快照：" + ex.Message); return; }
        if (_isWindowClosed) return;
        var body = new StackPanel { Margin = new Thickness(18), MaxWidth = 620 };
        body.Children.Add(new TextBlock { Text = "模组 / 资源包更新快照", FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
        body.Children.Add(new TextBlock { Text = "仅在你点击恢复并确认后写入文件；有后续修改的文件会阻止恢复。更新意外中断、尚未就绪的快照请先打开目录核对。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var list = new StackPanel();
        foreach (var snapshot in snapshots)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            var state = snapshot.State switch { "Ready" => "可恢复", "Restored" => "已恢复", "Restoring" => "恢复中断，可继续", _ => "未完成，需核对" };
            row.Children.Add(new TextBlock { Text = $"{PresentationPreferencesService.FormatDateTime(new DateTimeOffset(DateTime.SpecifyKind(snapshot.CreatedUtc, DateTimeKind.Utc)))} · {state} · {snapshot.Files.Count} 个文件", TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = snapshot.Folder, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 5) });
            var restore = new Button { Content = "恢复此快照", IsEnabled = snapshot.State is "Ready" or "Restoring", HorizontalAlignment = HorizontalAlignment.Left };
            restore.SetResourceReference(StyleProperty, "SecondaryButton");
            restore.Click += async (_, _) =>
            {
                if (_restoringModSnapshot) { ToastService.ShowInfo("已有快照正在恢复。"); return; }
                bool IsRunning() => ProcessManager.Running.Any(game =>
                {
                    try
                    {
                        var gameDir = Path.GetFullPath(game.GameDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        return snapshot.Folder.Equals(gameDir, StringComparison.OrdinalIgnoreCase)
                            || snapshot.Folder.StartsWith(gameDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return true; }
                });
                _restoringModSnapshot = true; restore.IsEnabled = false;
                var completed = false;
                try
                {
                    await ProcessManager.RefreshAsync();
                    if (_isWindowClosed) return;
                    if (IsRunning()) { ToastService.ShowWarning("请先关闭使用此目录的游戏，再恢复快照。"); return; }
                    if (!MessageBoxDialog.ShowConfirm("将恢复更新前的文件，并移除本次更新新增的文件。后续手动修改不会被覆盖。\n\n是否继续？", "恢复更新快照")) return;
                    await ProcessManager.RefreshAsync();
                    if (_isWindowClosed) return;
                    if (IsRunning()) { ToastService.ShowWarning("游戏仍在运行，未恢复文件。"); return; }
                    await Task.Run(() => store.Restore(snapshot.Id));
                    completed = true;
                    restore.Content = "已恢复";
                    ToastService.ShowSuccess("更新前的文件已恢复，可重新检查模组列表。");
                }
                catch (Exception ex) { restore.IsEnabled = true; ToastService.ShowWarning("未完成恢复：" + ex.Message); }
                finally { _restoringModSnapshot = false; restore.IsEnabled = !completed; }
            };
            row.Children.Add(restore); list.Children.Add(row);
        }
        if (snapshots.Count == 0) list.Children.Add(new TextBlock { Text = "暂无更新快照。开启更新前快照后，下一次更新模组 / 资源包时会保存。", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = Math.Clamp(ActualHeight - 260, 100, 300), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var dialog = new OverlayDialogControl
        {
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            MaxHeight = Math.Max(200, ActualHeight - 80)
        };
        var actions = new WrapPanel();
        var open = new Button { Content = "打开快照目录", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        open.SetResourceReference(StyleProperty, "SecondaryButton");
        open.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(directory); Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); }
            catch (Exception ex) { ToastService.ShowWarning("打开目录失败：" + ex.Message); }
        };
        actions.Children.Add(open);
        var close = new Button { Content = "关闭", Margin = new Thickness(10, 12, 0, 0) };
        close.SetResourceReference(StyleProperty, "SecondaryButton");
        close.Click += (_, _) => dialog.Close();
        actions.Children.Add(close); body.Children.Add(actions);
        OverlayDialogService.ShowNonModal(dialog);
    }
}
