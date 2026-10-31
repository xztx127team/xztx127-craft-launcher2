using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>Only shown from the save/instance page. Backup settings do not occupy the main toolbar.</summary>
public sealed class SaveBackupsDialog : OverlayDialogControl, IOverlayDialogDismissGuard, IOverlayHostCloseGuard
{
    private readonly MainWindow _owner;
    private readonly string _worldPath, _worldName;
    private readonly ListBox _records = new() { DisplayMemberPath = nameof(BackupRow.Summary), MinHeight = 100, MaxHeight = 180 };
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox _copyName = new() { MinWidth = 180 };
    private readonly TextBox _directory = new() { IsReadOnly = true, MinWidth = 120 };
    private readonly CheckBox _schedule = new() { Content = "自动备份此存档（游戏退出后按间隔检查）" };
    private readonly TextBox _hours = new() { Width = 70 }, _keep = new() { Width = 70 };
    private readonly Button _create = new() { Content = "立即备份" }, _restore = new() { Content = "恢复为副本", IsEnabled = false };
    private readonly StackPanel _options = new();
    private Button _refresh = null!;
    private readonly CancellationTokenSource _cancel = new();
    private bool _busy;
    private bool _initialLoad;
    private bool _closed, _closeWhenIdle;
    public bool IsHostCloseBlocked => _busy;
    private TaskResultKind? _activeKind;
    private sealed record BackupRow(SaveBackupRecord Record)
    {
        public string Summary => PresentationPreferencesService.FormatDateTime(new DateTimeOffset(Record.CreatedUtc)) + " · "
            + PresentationPreferencesService.FormatCapacity(Record.SourceBytes) + $" · {Record.FileCount:N0} 个文件";
    }
    private static BackupRow[] Rows(IReadOnlyList<SaveBackupRecord> records) => records.Select(r => new BackupRow(r)).ToArray();
    public SaveBackupsDialog(MainWindow owner, string worldPath, string worldName, string? metadataWarning = null)
    {
        _owner = owner; _worldPath = Path.GetFullPath(worldPath); _worldName = worldName;
        Width = 610; MaxWidth = Math.Max(360, owner.ActualWidth - 80);
        var body = new StackPanel { Margin = new Thickness(20) };
        body.Children.Add(new TextBlock { Text = "存档备份", FontSize = 18, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = worldName, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = _worldPath, Margin = new Thickness(0, 4, 0, 10) });
        body.Children.Add(new TextBlock { Text = "恢复会在原存档旁创建副本，不覆盖原存档。备份和恢复前请退出使用此目录的游戏。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        if (!string.IsNullOrWhiteSpace(metadataWarning))
            body.Children.Add(new TextBlock { Text = metadataWarning, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var cfg = owner.ConfigService.Config;
        var profile = (cfg.SaveBackupProfiles ?? new()).GetValueOrDefault(SaveBackupService.Key(worldPath)) ?? new SaveBackupProfile();
        _directory.Text = string.IsNullOrWhiteSpace(profile.DestinationDirectory) ? Path.Combine(App.DataDir, "save_backups") : profile.DestinationDirectory;
        _schedule.IsChecked = profile.Enabled; _hours.Text = Math.Clamp(profile.IntervalHours, 1, 720).ToString(); _keep.Text = Math.Clamp(profile.RetentionCount, 1, 100).ToString();
        _copyName.Text = Path.GetFileName(_worldPath) + "-restored-" + DateTime.Now.ToString("MMdd-HHmmss");
        var topActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        ButtonStyle(_create); _create.Click += async (_, _) => await CreateAsync(); topActions.Children.Add(_create);
        _create.ToolTip = "完整备份当前存档并计算校验值；请先关闭正在使用此存档的游戏。";
        _refresh = ActionButton("刷新", RefreshAsync); _refresh.ToolTip = "重新读取所选备份位置，并保留当前选择。"; topActions.Children.Add(_refresh); body.Children.Add(topActions);
        body.Children.Add(_records); _records.SelectionChanged += (_, _) => UpdatePreview(); body.Children.Add(_preview);
        body.Children.Add(new TextBlock { Text = "副本目录名称", Margin = new Thickness(0, 0, 0, 4) }); body.Children.Add(_copyName);
        ButtonStyle(_restore); _restore.HorizontalAlignment = HorizontalAlignment.Left; _restore.Margin = new Thickness(0, 8, 0, 0);
        _restore.Click += async (_, _) => await RestoreAsync(); body.Children.Add(_restore);
        ToolTipService.SetShowOnDisabled(_restore, true);
        _copyName.ToolTip = "恢复副本的新文件夹名称。原存档和已有文件夹不会被覆盖。";
        var options = _options; options.Children.Add(_schedule);
        var times = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        times.Children.Add(new TextBlock { Text = "间隔（小时）", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }); times.Children.Add(_hours);
        times.Children.Add(new TextBlock { Text = "保留自动备份", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) }); times.Children.Add(_keep); options.Children.Add(times);
        options.Children.Add(new TextBlock { Text = "备份位置", Margin = new Thickness(0, 0, 0, 4) });
        var location = new DockPanel(); var browse = ActionButton("更改…", ChooseDirectoryAsync); DockPanel.SetDock(browse, Dock.Right); location.Children.Add(browse); location.Children.Add(_directory); options.Children.Add(location);
        options.Children.Add(new TextBlock { Text = "更换位置不会移动或删除旧备份；保留数量仅清理该位置的自动备份，手动备份始终保留。关闭启动器后不会执行计划。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8), FontSize = 11 });
        var optionActions = new StackPanel { Orientation = Orientation.Horizontal };
        optionActions.Children.Add(ActionButton("打开备份位置", () =>
        {
            try { var directory = Store().DirectoryPath; Directory.CreateDirectory(directory); SaveBackupService.RejectLinks(directory); Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); }
            catch (Exception ex) { _status.Text = "无法打开备份位置：" + ex.Message; }
            return Task.CompletedTask;
        }));
        optionActions.Children.Add(ActionButton("保存备份设置", SaveOptionsAsync)); options.Children.Add(optionActions);
        body.Children.Add(new Expander { Header = "备份位置与自动备份", Content = options, Margin = new Thickness(0, 14, 0, 0) });
        body.Children.Add(_status);
        var close = ActionButton("关闭", () => { RequestDismiss(); return Task.CompletedTask; });
        close.ToolTip = "关闭备份管理；如有任务正在进行，会先取消任务并完成清理。";
        close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, 12, 0, 0); body.Children.Add(close);
        Content = new ScrollViewer { Content = body, MaxHeight = Math.Max(250, owner.ActualHeight - 100), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Loaded += async (_, _) => { if (_initialLoad) return; _initialLoad = true; await RefreshAsync(); };
        OverlayClosed += (_, _) => { _closed = true; _cancel.Cancel(); };
    }
    public void RequestDismiss()
    {
        if (_closed) return;
        if (!_busy) { Close(); return; }
        _closeWhenIdle = true;
        _cancel.Cancel();
        _status.Text = "正在取消任务并清理临时文件，完成后关闭…";
    }
    private static void ButtonStyle(Button button) { button.SetResourceReference(StyleProperty, "CompactActionButton"); button.Padding = new Thickness(11, 5, 11, 5); button.FontSize = 13; button.FontWeight = FontWeights.Normal; button.HorizontalAlignment = HorizontalAlignment.Left; button.MinHeight = 32; button.Margin = new Thickness(0, 0, 8, 0); }
    private Button ActionButton(string text, Func<Task> action)
    {
        var button = new Button { Content = text }; ButtonStyle(button);
        button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { _status.Text = "未完成：" + ex.Message; } };
        return button;
    }
    private SaveBackupService Store() => new(_directory.Text);
    private void UpdatePreview()
    {
        var record = (_records.SelectedItem as BackupRow)?.Record; _restore.IsEnabled = record != null && !_busy && !_cancel.IsCancellationRequested;
        _restore.ToolTip = _busy ? "请等待当前任务完成。" : record == null ? "请先选择一份备份。" : "校验备份后，在原存档旁恢复一个独立副本，原存档保持不变。";
        _preview.Text = record == null ? "选择一份备份查看恢复信息。" : $"存档：{record.WorldName}\n备份时间：{PresentationPreferencesService.FormatDateTime(new DateTimeOffset(record.CreatedUtc))}\n内容：{record.FileCount:N0} 个文件 · {PresentationPreferencesService.FormatCapacity(record.SourceBytes)}\n原位置：{record.WorldPath}";
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_closed || _closeWhenIdle) return;
        if (_busy) { _status.Text = "已有备份任务正在进行，请稍候。"; return; }
        _busy = true;
        _create.IsEnabled = _restore.IsEnabled = _refresh.IsEnabled = _records.IsEnabled = _copyName.IsEnabled = _options.IsEnabled = false;
        _status.Text = "正在处理…";
        try { await action(); }
        catch (OperationCanceledException) { if (_activeKind is { } kind) TaskResultHistoryService.Record(kind, TaskResultOutcome.Canceled); _status.Text = "操作已取消，原存档未被覆盖。"; }
        catch (Exception ex) { if (_activeKind is { } kind) TaskResultHistoryService.Record(kind, TaskResultOutcome.Failed); _status.Text = "未完成：" + ex.Message; }
        finally
        {
            _activeKind = null; _busy = false;
            if (!_closed)
            {
                _create.IsEnabled = _refresh.IsEnabled = _records.IsEnabled = _copyName.IsEnabled = _options.IsEnabled = !_closeWhenIdle;
                UpdatePreview();
                if (_closeWhenIdle) Close();
            }
        }
    }
    private async Task<bool> EnsureStoppedAsync()
    {
        await _owner.ProcessManager.RefreshAsync();
        _cancel.Token.ThrowIfCancellationRequested();
        if (ScheduledSaveBackupService.IsWorldRunning(_owner, _worldPath)) { _status.Text = "请先关闭使用此存档目录的游戏，再进行备份或恢复。"; return false; }
        return true;
    }
    private async Task RefreshAsync()
    {
        await RunAsync(async () =>
        {
            _status.Text = "正在读取备份列表…";
            var selectedId = (_records.SelectedItem as BackupRow)?.Record.Id;
            var store = Store(); var records = await Task.Run(() => store.List(_worldPath), _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested();
            var rows = Rows(records); _records.ItemsSource = rows;
            _records.SelectedItem = rows.FirstOrDefault(row => row.Record.Id == selectedId) ?? rows.FirstOrDefault();
            _status.Text = records.Count == 0 ? "此位置还没有该存档的备份。点击“立即备份”创建第一份。" : $"共 {records.Count} 份备份，选择后可恢复为副本。";
        });
    }
    private async Task CreateAsync()
    {
        await RunAsync(async () =>
        {
            if (!await EnsureStoppedAsync()) return; _status.Text = "正在备份存档…"; var store = Store();
            _activeKind = TaskResultKind.Backup;
            var record = await Task.Run(() => store.Create(_worldPath, _worldName, ct: _cancel.Token), _cancel.Token);
            TaskResultHistoryService.Record(TaskResultKind.Backup, TaskResultOutcome.Completed); _activeKind = null;
            var rows = Rows(await Task.Run(() => store.List(_worldPath), _cancel.Token)); _records.ItemsSource = rows; _records.SelectedItem = rows.FirstOrDefault(r => r.Record.Id == record.Id); _status.Text = "备份已完成并记录 SHA-256 校验值。";
        });
    }
    private async Task RestoreAsync()
    {
        if (_records.SelectedItem is not BackupRow row) { _status.Text = "请先选择要恢复的备份。"; return; } var record = row.Record;
        await RunAsync(async () =>
        {
            var name = _copyName.Text.Trim(); var target = SaveBackupService.CopyTarget(_worldPath, name);
            if (!await EnsureStoppedAsync()) return;
            if (await MessageBoxDialog.ShowConfirmAsync($"将从 {PresentationPreferencesService.FormatDateTime(new DateTimeOffset(record.CreatedUtc))} 的备份恢复副本。\n{record.FileCount:N0} 个文件 · {PresentationPreferencesService.FormatCapacity(record.SourceBytes)}\n\n目标：{target}\n\n原存档保留。是否继续？", "恢复存档副本") != true) { _status.Text = "已取消恢复，原存档保持不变。"; return; }
            if (!await EnsureStoppedAsync()) return; _status.Text = "正在校验并恢复副本…"; var store = Store();
            _activeKind = TaskResultKind.Restore;
            var path = await Task.Run(() => store.RestoreCopy(_worldPath, record.Id, name, _cancel.Token), _cancel.Token);
            TaskResultHistoryService.Record(TaskResultKind.Restore, TaskResultOutcome.Completed); _activeKind = null;
            _status.Text = "副本已生成：" + path;
        });
    }
    private async Task ChooseDirectoryAsync()
    {
        if (_busy) { _status.Text = "请等待当前任务完成后再更换备份位置。"; return; } var picker = new XCL2.App.Services.CompatibleFolderDialog { Title = "选择存档备份位置", InitialDirectory = _directory.Text };
        if (picker.ShowDialog(_owner) != true) return; _directory.Text = picker.FolderName; await RefreshAsync();
    }
    private Task SaveOptionsAsync()
    {
        if (_busy) { _status.Text = "请等待当前任务完成后再保存设置。"; return Task.CompletedTask; }
        if (!int.TryParse(_hours.Text, out var hours) || hours is < 1 or > 720 || !int.TryParse(_keep.Text, out var keep) || keep is < 1 or > 100)
        { _status.Text = "间隔为 1–720 小时，保留数量为 1–100。"; return Task.CompletedTask; }
        try
        {
            var store = Store(); var root = store.DirectoryPath;
            if (root.Equals(_worldPath, StringComparison.OrdinalIgnoreCase) || root.StartsWith(_worldPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("备份位置不能放在该存档内部。");
            if (_schedule.IsChecked == true)
            {
                try { XCL2.App.Services.Worlds.WorldCatalog.RequireBackupKnown(_worldPath, _owner.ServerInstanceService.Instances.Select(server => server.Directory)); }
                catch { throw new IOException("自动备份仅支持已配置的客户端或服务器世界；此存档仍可手动备份。"); }
            }
            var cfg = _owner.ConfigService.Config; cfg.SaveBackupProfiles ??= new(StringComparer.OrdinalIgnoreCase);
            var key = SaveBackupService.Key(_worldPath); var previous = cfg.SaveBackupProfiles.GetValueOrDefault(key);
            cfg.SaveBackupProfiles[key] = new SaveBackupProfile
            { WorldPath = _worldPath, WorldName = _worldName, Enabled = _schedule.IsChecked == true, IntervalHours = hours, RetentionCount = keep, DestinationDirectory = root };
            try { _owner.ConfigService.Save(); }
            catch { if (previous == null) cfg.SaveBackupProfiles.Remove(key); else cfg.SaveBackupProfiles[key] = previous; throw; }
            _status.Text = "备份设置已保存。";
        }
        catch (Exception ex) { _status.Text = "设置未保存：" + ex.Message; }
        return Task.CompletedTask;
    }
}
