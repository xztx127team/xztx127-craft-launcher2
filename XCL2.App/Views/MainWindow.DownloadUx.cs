using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Path = System.IO.Path;
using System.Windows.Shapes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private ICollectionView? _downloadFilteredView;
    private TextBox? _downloadSearch;
    private ComboBox? _downloadStatusFilter;
    private ComboBox? _downloadTypeFilter;
    private DispatcherTimer? _downloadActivityTimer;
    private StackPanel? _downloadSpeedHost;
    private Canvas? _downloadSpeedCanvas;
    private TextBlock? _downloadSpeedText;
    private readonly Queue<double> _downloadSpeedSamples = new();
    private long _downloadSpeedLastBytes;
    private long _downloadSpeedLastTick;
    private bool _downloadSpeedHasBytes;
    private DownloadGameActivityMonitor? _downloadGameMonitor;

    /// <summary>管理工具只放在用户主动打开的现有下载列表中。</summary>
    private void InitializeDownloadUx()
    {
        DownloadTransferPolicy.CapacityFormatter = bytes => PresentationPreferencesService.FormatCapacity(bytes);
        if (DownloadQueuePopupBorder.Child is not Grid grid) return;
        DownloadQueuePopupBorder.Width = 380;
        DownloadQueuePopupBorder.MinWidth = 320;
        DownloadQueuePopupBorder.MinHeight = 240;
        var oldTitle = grid.Children.OfType<TextBlock>().FirstOrDefault(child => Grid.GetRow(child) == 0);
        if (oldTitle != null) grid.Children.Remove(oldTitle);
        var toolbar = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(toolbar, 0);
        var title = new DockPanel { LastChildFill = true };
        var manage = new Button { Content = "管理", Padding = new Thickness(8, 3, 8, 3), MinWidth = 46, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(manage, Dock.Right);
        manage.SetResourceReference(StyleProperty, "SecondaryButton");
        manage.Click += (_, _) => ShowDownloadManagementMenu(manage);
        title.Children.Add(manage);
        var label = new TextBlock { Text = "下载列表", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); title.Children.Add(label);
        toolbar.Children.Add(title);
        var searchHost = new Grid { Margin = new Thickness(0, 6, 0, 5) };
        _downloadSearch = new TextBox { ToolTip = "搜索任务名称", MinHeight = 28 };
        System.Windows.Automation.AutomationProperties.SetName(_downloadSearch, "搜索下载任务");
        var placeholder = new TextBlock { Text = "搜索任务名称", IsHitTestVisible = false, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _downloadSearch.TextChanged += (_, _) => { placeholder.Visibility = string.IsNullOrEmpty(_downloadSearch.Text) ? Visibility.Visible : Visibility.Collapsed; RefreshDownloadFilter(); };
        searchHost.Children.Add(_downloadSearch); searchHost.Children.Add(placeholder); toolbar.Children.Add(searchHost);
        var filters = new Grid();
        filters.ColumnDefinitions.Add(new ColumnDefinition()); filters.ColumnDefinitions.Add(new ColumnDefinition());
        _downloadStatusFilter = new ComboBox { ItemsSource = new[] { "全部状态", "等待执行", "正在进行", "已暂停", "失败", "已完成", "已取消" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 5, 0), MinHeight = 28 };
        _downloadTypeFilter = new ComboBox { ItemsSource = new[] { "全部类型", "游戏实例", "运行环境", "模组与资源", "其他" }, SelectedIndex = 0, MinHeight = 28 };
        Grid.SetColumn(_downloadTypeFilter, 1); filters.Children.Add(_downloadStatusFilter); filters.Children.Add(_downloadTypeFilter);
        _downloadStatusFilter.SelectionChanged += (_, _) => RefreshDownloadFilter();
        _downloadTypeFilter.SelectionChanged += (_, _) => RefreshDownloadFilter();
        toolbar.Children.Add(filters);
        _downloadSpeedHost = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        _downloadSpeedText = new TextBlock { FontSize = 10, Text = "等待真实传输数据", TextTrimming = TextTrimming.CharacterEllipsis };
        _downloadSpeedText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _downloadSpeedCanvas = new Canvas { Height = 36, ClipToBounds = true, IsHitTestVisible = false };
        _downloadSpeedHost.Children.Add(_downloadSpeedText); _downloadSpeedHost.Children.Add(_downloadSpeedCanvas);
        toolbar.Children.Add(_downloadSpeedHost); grid.Children.Add(toolbar);
        _downloadFilteredView = CollectionViewSource.GetDefaultView(DownloadQueueService.Instance.Items);
        _downloadFilteredView.Filter = MatchesDownloadFilter;
        DownloadQueueItemsControl.ItemsSource = _downloadFilteredView;
        DownloadQueueItemsControl.AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler(DownloadTaskContextMenuOpening));
        foreach (var item in DownloadQueueService.Instance.Items) item.PropertyChanged += DownloadItemUxChanged;
        DownloadQueueService.Instance.Items.CollectionChanged += DownloadItemsUxChanged;
        _downloadActivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _downloadActivityTimer.Tick += (_, _) =>
        {
            foreach (var item in DownloadQueueService.Instance.Items.Where(entry => entry.IsRunning)) item.RefreshTiming();
            SampleDownloadSpeed();
        };
        DownloadQueuePopup.Opened += (_, _) => { RefreshDownloadFilter(); ResetDownloadSpeed(); SampleDownloadSpeed(); _downloadActivityTimer.Start(); };
        DownloadQueuePopup.Closed += (_, _) => { _downloadActivityTimer.Stop(); ResetDownloadSpeed(); };
        ProcessManager.Changed += UpdateGameDownloadLimit;
        ConfigService.Saved += UpdateGameDownloadLimit;
        _downloadGameMonitor = new DownloadGameActivityMonitor(
            () => BedrockLaunchService.GetRunningClientProcesses().Any(), DownloadTransferPolicy.SetGameRunning);
        UpdateGameDownloadLimit();
        Closed += (_, _) =>
        {
            _downloadActivityTimer.Stop(); ProcessManager.Changed -= UpdateGameDownloadLimit;
            ConfigService.Saved -= UpdateGameDownloadLimit;
            _downloadGameMonitor?.Dispose();
            DownloadQueueService.Instance.Items.CollectionChanged -= DownloadItemsUxChanged;
            foreach (var item in DownloadQueueService.Instance.Items) item.PropertyChanged -= DownloadItemUxChanged;
        };
    }
    private void UpdateGameDownloadLimit()
    {
        if (_isWindowClosed || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateGameDownloadLimit)); return; }
        _downloadGameMonitor?.Update(ConfigService.Config.ReduceDownloadSpeedWhileGaming,
            DownloadQueueService.Instance.Items.Any(item => item.IsRunning), ProcessManager.Running.Count > 0);
    }
    private void DownloadItemsUxChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        if (args.OldItems != null) foreach (DownloadQueueItem item in args.OldItems) item.PropertyChanged -= DownloadItemUxChanged;
        if (args.NewItems != null) foreach (DownloadQueueItem item in args.NewItems) item.PropertyChanged += DownloadItemUxChanged;
        RefreshDownloadFilter(); UpdateGameDownloadLimit();
    }
    private void DownloadItemUxChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(DownloadQueueItem.Status) or nameof(DownloadQueueItem.IsRunning))) return;
        RefreshDownloadFilter(); UpdateGameDownloadLimit();
    }
    private bool MatchesDownloadFilter(object value)
    {
        if (value is not DownloadQueueItem item) return false;
        if (!string.IsNullOrWhiteSpace(_downloadSearch?.Text) && !item.Name.Contains(_downloadSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (_downloadTypeFilter?.SelectedIndex > 0 && item.Category != _downloadTypeFilter.SelectedItem?.ToString()) return false;
        return (_downloadStatusFilter?.SelectedIndex ?? 0) switch
        {
            1 => item.Status == DownloadQueueItemStatus.Waiting,
            2 => item.IsRunning,
            3 => item.Status == DownloadQueueItemStatus.Paused,
            4 => item.Status == DownloadQueueItemStatus.Failed,
            5 => item.Status == DownloadQueueItemStatus.Completed,
            6 => item.Status == DownloadQueueItemStatus.Canceled,
            _ => true
        };
    }
    private void RefreshDownloadFilter()
    {
        if (_downloadFilteredView == null) return;
        _downloadFilteredView.Refresh();
        var empty = _downloadFilteredView.IsEmpty;
        DownloadQueueEmptyHint.Text = DownloadQueueService.Instance.Items.Count == 0 ? "暂无下载任务" : "无匹配任务，请清除筛选";
        DownloadQueueEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowDownloadManagementMenu(Button owner)
    {
        var menu = new ContextMenu();
        var retry = new MenuItem { Header = "仅重试失败任务", IsEnabled = DownloadQueueService.Instance.Items.Any(entry => entry.Status == DownloadQueueItemStatus.Failed && entry.CanResume) };
        retry.Click += (_, _) => { var count = DownloadQueueService.Instance.RetryFailed(); ToastService.ShowInfo($"已重新排队 {count} 个失败任务"); };
        menu.Items.Add(retry);
        var pauseAll = new MenuItem { Header = "暂停全部可安全暂停的任务", IsEnabled = DownloadQueueService.Instance.Items.Any(entry => entry.CanPause || (entry.CanMoveWaiting && entry.SupportsPause)) };
        pauseAll.Click += (_, _) => ToastService.ShowInfo($"已请求暂停 {DownloadQueueService.Instance.PauseAllSafe()} 个任务；安全的等待项一并暂停，不可暂停的安装任务保持原状态。");
        menu.Items.Add(pauseAll);
        var cancelWaiting = new MenuItem { Header = "选择并取消等待任务…", IsEnabled = DownloadQueueService.Instance.Items.Any(entry => entry.CanMoveWaiting) };
        cancelWaiting.Click += (_, _) => OpenWaitingTaskSelection(); menu.Items.Add(cancelWaiting);
        var recent = new MenuItem { Header = "本次会话最近完成", IsEnabled = DownloadQueueService.Instance.RecentCompleted.Count > 0 };
        foreach (var completed in DownloadQueueService.Instance.RecentCompleted)
        {
            var entry = completed;
            var task = new MenuItem { Header = completed.Name, ToolTip = completed.Name };
            var detail = new MenuItem { Header = "查看详情与定位文件…" };
            detail.Click += (_, _) => OpenDownloadTaskDetails(entry); task.Items.Add(detail); recent.Items.Add(task);
        }
        menu.Items.Add(recent);
        var clearFilters = new MenuItem { Header = "清除搜索与筛选" };
        clearFilters.Click += (_, _) => { _downloadSearch!.Clear(); _downloadStatusFilter!.SelectedIndex = 0; _downloadTypeFilter!.SelectedIndex = 0; RefreshDownloadFilter(); };
        menu.Items.Add(clearFilters); menu.Items.Add(new Separator());
        var concurrency = new MenuItem { Header = "同时执行的任务数" };
        foreach (var limit in new[] { 0, 1, 2, 3, 4, 8 })
        {
            var choice = new MenuItem { Header = limit == 0 ? "不限制（原有行为）" : $"{limit} 个", IsCheckable = true, IsChecked = ConfigService.Config.DownloadTaskConcurrency == limit };
            choice.Click += (_, _) => { ConfigService.Config.DownloadTaskConcurrency = limit; ConfigService.Save(); DownloadQueueService.Instance.RefreshScheduling(); };
            concurrency.Items.Add(choice);
        }
        menu.Items.Add(concurrency);
        var speed = new MenuItem { Header = "总下载速度上限" };
        foreach (var limit in new[] { 0, 128, 512, 1024, 2048, 5120, 10240 })
        {
            var choice = new MenuItem { Header = limit == 0 ? "不限速" : $"{limit} KiB/秒", IsCheckable = true, IsChecked = ConfigService.Config.DownloadSpeedLimitKBps == limit };
            choice.Click += (_, _) => { ConfigService.Config.DownloadSpeedLimitKBps = limit; ConfigService.Save(); };
            speed.Items.Add(choice);
        }
        menu.Items.Add(speed);
        var gaming = new MenuItem { Header = "游戏运行时降低下载速度", IsCheckable = true, IsChecked = ConfigService.Config.ReduceDownloadSpeedWhileGaming };
        gaming.Click += (_, _) => { ConfigService.Config.ReduceDownloadSpeedWhileGaming = gaming.IsChecked; ConfigService.Save(); UpdateGameDownloadLimit(); };
        menu.Items.Add(gaming);
        var gamingSpeed = new MenuItem { Header = "游戏运行时的速度上限" };
        foreach (var limit in new[] { 128, 512, 1024, 2048, 5120 })
        {
            var choice = new MenuItem { Header = $"{limit} KiB/秒", IsCheckable = true, IsChecked = ConfigService.Config.GamingDownloadSpeedLimitKBps == limit };
            choice.Click += (_, _) => { ConfigService.Config.GamingDownloadSpeedLimitKBps = limit; ConfigService.Save(); };
            gamingSpeed.Items.Add(choice);
        }
        menu.Items.Add(gamingSpeed);
        menu.Items.Add(new Separator());
        var help = new MenuItem { Header = "下载管理帮助" };
        help.Click += (_, _) => OfflineHelpService.Show("downloads"); menu.Items.Add(help);
        owner.ContextMenu = menu; menu.PlacementTarget = owner; menu.IsOpen = true;
    }
    private void DownloadTaskContextMenuOpening(object sender, ContextMenuEventArgs args)
    {
        var current = args.OriginalSource as DependencyObject;
        DownloadQueueItem? item = null;
        while (current != null)
        {
            if (current is FrameworkElement element && element.DataContext is DownloadQueueItem entry) { item = entry; break; }
            current = current is Visual || current is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        if (item == null) return;
        var selected = item;
        var menu = new ContextMenu();
        var up = new MenuItem { Header = "上移等待任务", IsEnabled = item.CanMoveWaiting };
        up.Click += (_, _) => DownloadQueueService.Instance.MoveWaiting(selected, -1); menu.Items.Add(up);
        var down = new MenuItem { Header = "下移等待任务", IsEnabled = item.CanMoveWaiting };
        down.Click += (_, _) => DownloadQueueService.Instance.MoveWaiting(selected, 1); menu.Items.Add(down);
        var explanation = new MenuItem { Header = "任务详情、备注与文件…" };
        explanation.Click += (_, _) => OpenDownloadTaskDetails(selected);
        menu.Items.Add(explanation);
        var cleanup = new MenuItem { Header = "清理本任务的续传临时文件…", IsEnabled = !DownloadQueueService.Instance.HasActive && item.TransferObservation.HasRecordedResumableFiles };
        cleanup.Click += async (_, _) =>
        {
            if (DownloadQueueService.Instance.HasActive) { ToastService.ShowInfo("请先等待或取消其他下载任务，再清理临时文件。"); return; }
            if (!MessageBoxDialog.ShowConfirm("仅删除本任务记录的 .part 续传文件，下次需重新下载这些片段。已完成的文件、实例和存档均会保留。是否继续？", "清理续传文件")) return;
            if (DownloadQueueService.Instance.HasActive) return;
            try { var count = await DownloadQueueService.Instance.CleanupRecordedPartsAsync(selected); selected.TaskExplanation = await Task.Run(selected.TransferObservation.Summary); ToastService.ShowSuccess($"已清理 {count} 个续传临时文件"); }
            catch (Exception ex) { ToastService.ShowWarning("未完成清理：" + ex.Message); }
        };
        menu.Items.Add(cleanup);
        args.Handled = true;
        menu.PlacementTarget = DownloadQueueItemsControl; menu.IsOpen = true;
    }
    private void ResetDownloadSpeed()
    {
        _downloadSpeedSamples.Clear(); _downloadSpeedLastBytes = 0; _downloadSpeedLastTick = 0; _downloadSpeedHasBytes = false;
        _downloadSpeedCanvas?.Children.Clear();
        if (_downloadSpeedText != null) _downloadSpeedText.Text = "等待真实传输数据";
    }
    private void SampleDownloadSpeed()
    {
        if (_downloadSpeedHost == null || _downloadSpeedCanvas == null || _downloadSpeedText == null) return;
        var enabled = ConfigService.Config.Ux50DownloadSpeedGraph && DownloadQueuePopup.IsOpen;
        _downloadSpeedHost.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled) { ResetDownloadSpeed(); return; }
        // 包括刚完成但尚未从列表收起的任务，避免终态转换导致总字节数突然回退。
        var bytes = DownloadQueueService.Instance.Items.Concat(DownloadQueueService.Instance.RecentCompleted)
            .Distinct().Sum(item => item.TransferObservation.ReceivedBytes);
        var now = Stopwatch.GetTimestamp();
        if (_downloadSpeedLastTick != 0 && bytes >= _downloadSpeedLastBytes)
        {
            var elapsed = Stopwatch.GetElapsedTime(_downloadSpeedLastTick, now).TotalSeconds;
            if (elapsed > 0 && (_downloadSpeedHasBytes || bytes > _downloadSpeedLastBytes))
            {
                _downloadSpeedHasBytes = true;
                var speed = (bytes - _downloadSpeedLastBytes) / elapsed;
                _downloadSpeedSamples.Enqueue(speed); while (_downloadSpeedSamples.Count > 60) _downloadSpeedSamples.Dequeue();
                _downloadSpeedText.Text = "已观测传输 " + DownloadTransferPolicy.FormatCapacity((long)speed) + "/秒 · 最近 60 秒";
                _downloadSpeedText.ToolTip = "仅统计接入统一下载器的真实网络字节；关闭面板即清除曲线，不累计后台历史。";
            }
        }
        _downloadSpeedLastBytes = bytes; _downloadSpeedLastTick = now;
        _downloadSpeedCanvas.Children.Clear();
        var values = _downloadSpeedSamples.ToArray(); if (values.Length < 2) return;
        var width = _downloadSpeedCanvas.ActualWidth; if (width <= 0) return;
        var maximum = Math.Max(1, values.Max());
        var line = new Polyline { StrokeThickness = 1.5 };
        line.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        for (var i = 0; i < values.Length; i++) line.Points.Add(new Point(i * width / (values.Length - 1), 34 - 32 * values[i] / maximum));
        _downloadSpeedCanvas.Children.Add(line);
    }
    private static TextBlock DownloadDetailText(string text) => new()
    { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private static Button DownloadActionButton(string label)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(10, 5, 10, 5), MinHeight = 30 };
        button.SetResourceReference(StyleProperty, "SecondaryButton"); return button;
    }
    private OverlayDialogControl DownloadDetailDialog(string title, StackPanel body)
    {
        DownloadQueuePopup.IsOpen = false;
        body.Margin = new Thickness(18); body.MaxWidth = 600;
        body.Children.Insert(0, new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var dialog = new OverlayDialogControl { Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, MaxHeight = Math.Max(240, ActualHeight - 80) };
        var close = DownloadActionButton("关闭"); close.Click += (_, _) => dialog.Close(); body.Children.Add(close);
        return dialog;
    }
    private void OpenWaitingTaskSelection()
    {
        var entries = DownloadQueueService.Instance.Items.Where(entry => entry.CanMoveWaiting).ToArray();
        if (entries.Length == 0) { ToastService.ShowInfo("当前没有等待执行的任务。"); return; }
        var body = new StackPanel();
        body.Children.Add(DownloadDetailText("仅取消你勾选且仍在等待的任务。已经开始的任务不会被本操作中断，已有文件会保留。"));
        var checks = entries.Select(entry => new CheckBox { Content = new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 480 }, Tag = entry, Margin = new Thickness(0, 4, 0, 4), ToolTip = entry.Name }).ToArray();
        var list = new StackPanel(); foreach (var check in checks) list.Children.Add(check);
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = Math.Clamp(ActualHeight - 350, 100, 280), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var selectAll = DownloadActionButton("选择当前全部等待任务"); selectAll.Click += (_, _) => { foreach (var check in checks) check.IsChecked = true; }; body.Children.Add(selectAll);
        var cancel = DownloadActionButton("取消勾选任务…");
        var dialog = DownloadDetailDialog("选择等待任务", body);
        cancel.Click += (_, _) =>
        {
            var selected = checks.Where(check => check.IsChecked == true).Select(check => (DownloadQueueItem)check.Tag)
                .Where(entry => entry.CanMoveWaiting && DownloadQueueService.Instance.Items.Contains(entry)).ToArray();
            if (selected.Length == 0) { ToastService.ShowInfo("请勾选仍在等待的任务。"); return; }
            if (!MessageBoxDialog.ShowConfirm($"确认取消 {selected.Length} 个仍在等待的任务？本次排队不再执行，已有文件不会被删除。", "取消等待任务")) return;
            var count = DownloadQueueService.Instance.CancelSelectedWaiting(selected);
            dialog.Close(); ToastService.ShowInfo($"已取消 {count} 个等待任务；已经开始的任务保持原状态。");
        };
        body.Children.Insert(body.Children.Count - 1, cancel); dialog.Show();
    }
    private async void OpenDownloadTaskDetails(DownloadQueueItem item)
    {
        var body = new StackPanel();
        if (ConfigService.Config.Ux50DownloadShowOrigin) body.Children.Add(DownloadDetailText("发起页面：" + item.OriginPage));
        var progress = DownloadDetailText(""); body.Children.Add(progress);
        void RefreshProgress()
        {
            var stage = item.TransferStageText;
            progress.Text = item.StatusText + "\n" + item.ActivityText + (stage.Length == 0 ? "" : "\n" + stage)
                + "\n" + (string.IsNullOrWhiteSpace(item.TaskExplanation) ? "完整文件会保留；临时文件能否续传由下载方式决定。" : item.TaskExplanation);
        }
        RefreshProgress();
        body.Children.Add(DownloadDetailText("个人备注（最多 200 字，只随本会话任务保留，不修改真实文件名）："));
        var note = new TextBox { Text = item.Remark, MaxLength = 200, MinHeight = 30, Margin = new Thickness(0, 0, 0, 5) };
        System.Windows.Automation.AutomationProperties.SetName(note, "下载任务个人备注"); body.Children.Add(note);
        var saveNote = DownloadActionButton("保存备注"); saveNote.Click += (_, _) => { item.Remark = note.Text; ToastService.ShowInfo("备注已保存到本次任务。"); }; body.Children.Add(saveNote);
        var disk = DownloadDetailText("磁盘可用空间：点击刷新后读取；不是完整安装所需空间估算。"); body.Children.Add(disk);
        var refreshDisk = DownloadActionButton("刷新目标磁盘空间");
        refreshDisk.Click += async (_, _) =>
        {
            refreshDisk.IsEnabled = false;
            var target = item.TransferObservation.TargetPath ?? item.TargetFolderPath;
            try
            {
                var result = await Task.Run(() =>
                {
                    if (string.IsNullOrWhiteSpace(target)) return "此任务尚未提供实际目标路径，暂不可读取。";
                    var root = Path.GetPathRoot(Path.GetFullPath(target));
                    if (string.IsNullOrWhiteSpace(root)) return "无法确定目标磁盘。";
                    return root + " 可用 " + DownloadTransferPolicy.FormatCapacity(new DriveInfo(root).AvailableFreeSpace) + " · " + DateTime.Now.ToString("HH:mm:ss");
                });
                disk.Text = result;
            }
            catch (Exception ex) { disk.Text = "读取失败：" + ex.Message; }
            finally { refreshDisk.IsEnabled = true; }
        }; body.Children.Add(refreshDisk);
        if (item.Status == DownloadQueueItemStatus.Completed)
        {
            body.Children.Add(DownloadDetailText("已记录的完成文件（安装程序可能已经移动或删除原下载文件）："));
            var files = item.TransferObservation.CompletedPaths().Where(File.Exists).ToArray();
            if (files.Length == 0) body.Children.Add(DownloadDetailText("没有仍可定位的已记录文件。"));
            foreach (var path in files)
            {
                var file = path;
                var fileRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                fileRow.ColumnDefinitions.Add(new ColumnDefinition()); fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                fileRow.Children.Add(new TextBlock { Text = Path.GetFileName(path), ToolTip = path, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
                var locate = DownloadActionButton("定位文件"); locate.ToolTip = path;
                Grid.SetColumn(locate, 1); fileRow.Children.Add(locate);
                locate.Click += (_, _) =>
                {
                    if (!File.Exists(file)) { ToastService.ShowInfo("文件已移动或删除。"); return; }
                    try
                    {
                        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                        start.ArgumentList.Add("/select," + file); Process.Start(start);
                    }
                    catch (Exception ex) { ToastService.ShowWarning("无法定位文件：" + ex.Message); }
                }; body.Children.Add(fileRow);
            }
        }
        var dialog = DownloadDetailDialog(item.Name, body);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => RefreshProgress();
        timer.Start();
        try { await OverlayDialogService.ShowModalAsync(dialog); }
        finally { timer.Stop(); }
    }

}
