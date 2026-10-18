using System.Diagnostics;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class VersionSelectPage
{
    private sealed record InstanceBrowseState(string Query, int Scope, double Offset);
    private static readonly Dictionary<string, InstanceBrowseState> InstanceBrowseStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object InstanceUxMenuTag = new();
    private bool _instanceUxReady;
    private bool _restoringInstanceBrowse;
    private bool _restoringInstanceScroll;
    private string? _browseFolder;
    private long _filterGeneration;

    public static void ClearRememberedBrowsePositions() => InstanceBrowseStates.Clear();

    private void InitializeInstanceUx()
    {
        _instanceUxReady = true;
        SwitchInstanceBrowseFolder(GetSelectedFolderPath());
        InstalledListBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, args) =>
        {
            if (!_refreshingItems && !_restoringInstanceScroll && !_restoringInstanceBrowse && args.VerticalChange != 0) SaveInstanceBrowseState();
        }));
        Unloaded += (_, _) => SaveInstanceBrowseState();
        Loaded += (_, _) => { SubscribeOwnershipTasks(); RefreshBackgroundTaskOwnership(); };
        Unloaded += (_, _) => UnsubscribeOwnershipTasks();
    }

    private static ScrollViewer? FindInstanceScroller(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindInstanceScroller(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private void SaveInstanceBrowseState(double? offsetOverride = null)
    {
        if (!_instanceUxReady || string.IsNullOrWhiteSpace(_browseFolder) || !_owner.ConfigService.Config.RememberInstanceBrowsePosition) return;
        if (InstanceBrowseStates.Count > 50 && !InstanceBrowseStates.ContainsKey(_browseFolder)) InstanceBrowseStates.Clear();
        InstanceBrowseStates[_browseFolder] = new(InstanceFilterBox.Text, Math.Max(0, InstanceScopeBox.SelectedIndex),
            offsetOverride ?? FindInstanceScroller(InstalledListBox)?.VerticalOffset ?? 0);
    }

    private void SwitchInstanceBrowseFolder(string? folder)
    {
        if (!_instanceUxReady || string.Equals(_browseFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
        SaveInstanceBrowseState();
        _browseFolder = folder;
        _restoringInstanceScroll = true;
        InstanceBrowseState? state = null;
        if (!string.IsNullOrWhiteSpace(folder) && _owner.ConfigService.Config.RememberInstanceBrowsePosition)
            InstanceBrowseStates.TryGetValue(folder, out state);
        _restoringInstanceBrowse = true;
        try
        {
            InstanceFilterBox.Text = state?.Query ?? "";
            InstanceScopeBox.SelectedIndex = Math.Clamp(state?.Scope ?? 0, 0, 2);
            InstanceFilterRow.Visibility = !string.IsNullOrEmpty(state?.Query) || state?.Scope > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _restoringInstanceBrowse = false; }
    }

    private void ToggleInstanceFilter_Click(object sender, RoutedEventArgs e)
    {
        if (InstanceFilterRow.Visibility == Visibility.Visible)
            InstanceFilterRow.Visibility = Visibility.Collapsed;
        else
        {
            InstanceFilterRow.Visibility = Visibility.Visible;
            InstanceFilterBox.Focus();
        }
    }

    private void ClearInstanceFilter_Click(object sender, RoutedEventArgs e)
    {
        _restoringInstanceBrowse = true;
        try { InstanceFilterBox.Clear(); InstanceScopeBox.SelectedIndex = 0; }
        finally { _restoringInstanceBrowse = false; }
        ApplyInstanceFilter(); SaveInstanceBrowseState(0);
    }

    private void InstanceFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_instanceUxReady || _restoringInstanceBrowse) return;
        ApplyInstanceFilter(); SaveInstanceBrowseState(0);
    }

    private void ApplyInstanceFilter(bool restoreScroll = false, string? selectedId = null)
    {
        if (!_instanceUxReady) return;
        var cfg = _owner.ConfigService.Config;
        var folder = GetSelectedFolderPath() ?? "";
        var offset = FindInstanceScroller(InstalledListBox)?.VerticalOffset ?? 0;
        if (restoreScroll && cfg.RememberInstanceBrowsePosition && InstanceBrowseStates.TryGetValue(folder, out var state)) offset = state.Offset;
        var presentationChanged = false;
        foreach (var version in _installed)
        {
            var metadata = InstanceUxService.Get(cfg, folder, version.Id);
            var favorite = InstanceUxService.IsFavorite(cfg, folder, version.Id);
            var icon = InstanceUxService.GetIconPath(cfg, folder, version.Id);
            var tags = string.Join(" · ", metadata?.Tags ?? new());
            var notes = metadata?.Notes ?? "";
            var shared = cfg.Ux50ShowSharedGameDirectory && InstanceExperienceService.SharesGameDirectory(cfg, folder, version, _installed);
            presentationChanged |= version.SharesGameDirectory != shared;
            version.SharesGameDirectory = shared;
            presentationChanged |= version.IsFavorite != favorite || version.CustomIconPath != icon || version.MetadataSummary != tags || version.PersonalNotes != notes;
            version.IsFavorite = favorite; version.CustomIconPath = icon; version.MetadataSummary = tags; version.PersonalNotes = notes;
        }
        var query = InstanceFilterBox.Text.Trim();
        IEnumerable<GameVersion> matching = _installed;
        if (query.Length > 0) matching = matching.Where(v => (v.Id + " " + v.Details + " " + v.MetadataSummary + " " + v.PersonalNotes)
            .Contains(query, StringComparison.OrdinalIgnoreCase));
        if (InstanceScopeBox.SelectedIndex == 1) matching = matching.Where(v => v.IsFavorite);
        if (InstanceScopeBox.SelectedIndex == 2)
        {
            var recent = (cfg.RecentInstances ?? new()).Where(r => r != null && string.Equals(r.FolderPath, folder, StringComparison.OrdinalIgnoreCase))
                .GroupBy(r => r.VersionId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Max(r => r.LaunchedAtUtc), StringComparer.OrdinalIgnoreCase);
            matching = matching.Where(v => recent.ContainsKey(v.Id)).OrderByDescending(v => recent[v.Id]);
        }
        var shown = matching.ToList();
        selectedId ??= (InstalledListBox.SelectedItem as GameVersion)?.Id ?? cfg.SelectedVersionId;
        var refreshing = _refreshingItems;
        _refreshingItems = true;
        _restoringInstanceScroll = true;
        try
        {
            if (InstalledListBox.ItemsSource is not IEnumerable<GameVersion> currentRows || !currentRows.SequenceEqual(shown))
                InstalledListBox.ItemsSource = shown;
            else if (presentationChanged) InstalledListBox.Items.Refresh();
            InstalledListBox.SelectedItem = shown.FirstOrDefault(v => string.Equals(v.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        }
        finally { _refreshingItems = refreshing; }
        RefreshBackgroundTaskOwnership();
        InstanceEmptyMessage.Text = _installed.Count == 0 ? "这个文件夹还没有实例。可以安装新版本，或从左侧导入已有实例。" :
            InstanceScopeBox.SelectedIndex == 2 && !cfg.EnableRecentInstanceHistory ? "最近使用记录默认关闭。可以在设置中开启；开启前的启动不会补记。" :
            "没有符合条件的实例。可以修改关键词或点击“清除”。";
        InstanceEmptyMessage.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var generation = ++_filterGeneration;
        var targetOffset = restoreScroll ? offset : 0;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (generation != _filterGeneration) return;
            try { if (IsLoaded) FindInstanceScroller(InstalledListBox)?.ScrollToVerticalOffset(targetOffset); }
            finally { _restoringInstanceScroll = false; }
        }), DispatcherPriority.Loaded);
    }

    private MenuItem InstanceUxAction(string title, Action action)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { ErrorPresenter.ShowFriendlyError("操作没有完成，请检查目录权限或稍后重试。", ex.Message, title); }
        };
        return item;
    }

    private void PopulateInstanceUxMenu(ContextMenu menu, GameVersion version)
    {
        foreach (var old in menu.Items.OfType<FrameworkElement>().Where(item => ReferenceEquals(item.Tag, InstanceUxMenuTag)).ToArray()) menu.Items.Remove(old);
        var folder = GetSelectedFolderPath();
        if (string.IsNullOrWhiteSpace(folder)) return;
        var cfg = _owner.ConfigService.Config;
        var personal = new MenuItem { Header = "收藏与个性化", Tag = InstanceUxMenuTag };
        personal.Items.Add(InstanceUxAction(InstanceUxService.IsFavorite(cfg, folder, version.Id) ? "取消收藏" : "收藏实例", () =>
        {
            InstanceUxService.ToggleFavorite(cfg, folder, version.Id); _owner.ConfigService.Save(); ApplyInstanceFilter(restoreScroll: true);
        }));
        personal.Items.Add(InstanceUxAction("备注与标签…", () =>
        {
            var dialog = new InstancePersonalizationDialog(version.Id, InstanceUxService.Get(cfg, folder, version.Id));
            if (dialog.ShowDialog() != true) return;
            InstanceUxService.Set(cfg, folder, version.Id, dialog.Notes, dialog.Tags); _owner.ConfigService.Save(); ApplyInstanceFilter(restoreScroll: true);
        }));
        personal.Items.Add(InstanceUxAction("选择并裁剪图标…", () => ChooseInstanceIcon(folder, version.Id)));
        var restore = InstanceUxAction("恢复原始图标", () =>
        {
            var oldPath = InstanceUxService.GetIconPath(cfg, folder, version.Id);
            InstanceUxService.SetIconFile(cfg, folder, version.Id, null); _owner.ConfigService.Save(); ApplyInstanceFilter(restoreScroll: true);
            if (oldPath != null) { try { File.Delete(oldPath); } catch { } }
        });
        restore.IsEnabled = InstanceUxService.GetIconPath(cfg, folder, version.Id) != null;
        personal.Items.Add(restore);
        menu.Items.Add(personal);

        var files = new MenuItem { Header = "文件与存档", Tag = InstanceUxMenuTag };
        files.Items.Add(InstanceUxAction("打开实例文件夹", () => OpenExistingInstanceDirectory(InstanceUxService.GetVersionDirectory(folder, version.Id))));
        var gameDirectory = InstanceUxService.GetGameDirectory(cfg, folder, version.Id);
        foreach (var entry in new[] { ("存档文件夹", "saves"), ("模组文件夹", "mods"), ("资源包文件夹", "resourcepacks") })
        {
            var path = Path.Combine(gameDirectory, entry.Item2);
            files.Items.Add(InstanceUxAction(entry.Item1, () => OpenExistingInstanceDirectory(path)));
        }
        files.Items.Add(new Separator());
        files.Items.Add(InstanceUxAction("查看截图…", () => ScreenshotBrowserDialog.Show(gameDirectory)));
        files.Items.Add(InstanceUxAction("存档备份与恢复…", () => _owner.OpenSaveBackupFolder(gameDirectory)));
        files.Items.Add(new Separator());
        files.Items.Add(InstanceUxAction("创建实例文件夹桌面快捷方式", () =>
        {
            var created = InstanceUxService.CreateFolderDesktopShortcut(folder, version.Id);
            ToastService.ShowSuccess("已创建桌面文件夹快捷方式：" + Path.GetFileName(created));
        }));
        menu.Items.Add(files);
        files.Items.Add(new Separator());
        files.Items.Add(InstanceUxAction("复制环境摘要", async () =>
        {
            try
            {
                var mods = await Task.Run(() => new LocalModService().ScanMods(gameDirectory));
                var isolated = !string.Equals(Path.GetFullPath(gameDirectory), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
                Clipboard.SetText(InstanceExperienceService.EnvironmentSummary(version, isolated, mods.Count(m => m.IsEnabled), mods.Count(m => !m.IsEnabled)));
                if (cfg.Ux50InputLocalCopyFeedback) InputExperienceService.ShowCopied(menu.PlacementTarget as FrameworkElement ?? InstalledListBox);
                else ToastService.ShowSuccess("环境摘要已复制，未包含账号或私人路径。");
            }
            catch (Exception ex) { ErrorPresenter.ShowFriendlyError("无法复制环境摘要。", ex.Message, "环境摘要"); }
        }));
        files.Items.Add(InstanceUxAction("实例帮助", () => OfflineHelpService.Show("instances")));
    }

    private readonly HashSet<DownloadQueueItem> _ownershipTasks = new();
    private void SubscribeOwnershipTasks()
    {
        UnsubscribeOwnershipTasks();
        DownloadQueueService.Instance.Items.CollectionChanged += OwnershipTasksChanged;
        foreach (var task in DownloadQueueService.Instance.Items) { _ownershipTasks.Add(task); task.PropertyChanged += OwnershipTaskChanged; }
    }
    private void UnsubscribeOwnershipTasks()
    {
        DownloadQueueService.Instance.Items.CollectionChanged -= OwnershipTasksChanged;
        foreach (var task in _ownershipTasks) task.PropertyChanged -= OwnershipTaskChanged;
        _ownershipTasks.Clear();
    }
    private void OwnershipTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    { SubscribeOwnershipTasks(); RefreshBackgroundTaskOwnership(); }
    private void OwnershipTaskChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(DownloadQueueItem.Status) or nameof(DownloadQueueItem.IsRunning)) RefreshBackgroundTaskOwnership(); }
    private void RefreshBackgroundTaskOwnership()
    {
        if (!IsLoaded || BackgroundTaskOwnership == null) return;
        var selected = (InstalledListBox.SelectedItem as GameVersion)?.Id;
        var folder = GetSelectedFolderPath();
        var others = DownloadQueueService.Instance.Items.Where(task => !string.IsNullOrWhiteSpace(task.TargetVersionId) &&
            (task.IsRunning || task.Status is DownloadQueueItemStatus.Paused or DownloadQueueItemStatus.Waiting) &&
            !(string.Equals(task.TargetVersionId, selected, StringComparison.OrdinalIgnoreCase) &&
              string.Equals(task.TargetFolderPath?.TrimEnd('/', '\\'), folder?.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase))).ToArray();
        BackgroundTaskOwnership.Visibility = _owner.ConfigService.Config.Ux50ShowBackgroundTaskOwnership && others.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BackgroundTaskOwnership.Text = "后台任务仍属于：" + string.Join("、", others.Take(3).Select(task => task.TargetVersionId)) + (others.Length > 3 ? $" 等 {others.Length} 项" : "");
        BackgroundTaskOwnership.ToolTip = string.Join("\n", others.Take(20).Select(task => task.Name + " → " + task.TargetVersionId));
    }

    private static void OpenExistingInstanceDirectory(string path)
    {
        if (!Directory.Exists(path)) { ToastService.ShowInfo("这个文件夹尚未创建。运行游戏或安装相应内容后才会出现。"); return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void ChooseInstanceIcon(string folder, string id)
    {
        var chooser = new OpenFileDialog { Title = "选择实例图标", Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
        if (chooser.ShowDialog() != true) return;
        var crop = new InstanceIconCropDialog(chooser.FileName);
        if (crop.ShowDialog() != true || crop.Result == null) return;
        var cfg = _owner.ConfigService.Config;
        var oldFile = InstanceUxService.Get(cfg, folder, id)?.IconFileName;
        var oldPath = InstanceUxService.GetIconPath(cfg, folder, id);
        var target = InstanceUxService.CreateIconPath(folder, id);
        try
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crop.Result));
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(output);
            InstanceUxService.SetIconFile(cfg, folder, id, Path.GetFileName(target));
            _owner.ConfigService.Save();
        }
        catch
        {
            InstanceUxService.SetIconFile(cfg, folder, id, oldFile);
            try { File.Delete(target); } catch { }
            throw;
        }
        ApplyInstanceFilter(restoreScroll: true);
        if (oldPath != null) { try { File.Delete(oldPath); } catch { } }
    }
}
