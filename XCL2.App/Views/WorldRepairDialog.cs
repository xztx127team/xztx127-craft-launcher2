using System.IO;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;
using XCL2.App.Services.Worlds;

namespace XCL2.App.Views;

/// <summary>Read-only inspection and explicit, backed-up metadata recovery for client/server Java saves.</summary>
public sealed class WorldRepairDialog : OverlayDialogControl, IOverlayDialogDismissGuard, IOverlayHostCloseGuard
{
    private readonly MainWindow _owner;
    private readonly string _directory;
    private readonly TextBox _report = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 180, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox _candidates = new() { DisplayMemberPath = nameof(CandidateRow.Label), MinWidth = 240 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Button _restore = new() { Content = "备份并恢复元数据", IsEnabled = false };
    private readonly Button _scan = new() { Content = "重新检查" };
    private readonly Button _backups = new() { Content = "完整备份与恢复副本" };
    private bool _busy;
    private bool _closed;
    private bool _initialScan;
    public bool IsHostCloseBlocked => _busy;
    private readonly CancellationTokenSource _scanStop = new();
    public bool Repaired { get; private set; }
    private sealed record CandidateRow(WorldRepairCandidate Candidate)
    { public string Label => Candidate.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " · " + Candidate.Name; }

    public WorldRepairDialog(MainWindow owner, string directory)
    {
        _owner = owner; _directory = Path.GetFullPath(directory);
        Width = 690; MaxWidth = Math.Max(360, owner.ActualWidth - 64);
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = "存档健康检查与修复", FontSize = 22, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = _directory, TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new Thickness(0, 8, 0, 16) });
        body.Children.Add(_report);
        body.Children.Add(new TextBlock { Text = "选择此世界的元数据备份", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8) });
        body.Children.Add(_candidates);
        body.Children.Add(new TextBlock { Text = "恢复只替换 level.dat，并先保存原始文件。它会回退世界设置、时间和 level.dat 中的单人玩家数据；服务器 playerdata、区块和维度数据均保留。区块损坏请使用完整备份恢复为副本。", TextWrapping = TextWrapping.Wrap, Opacity = .75, Margin = new Thickness(0, 10, 0, 16) });
        var actions = new WrapPanel();
        StyleButton(_scan, false); _scan.Click += async (_, _) => await ScanAsync(); actions.Children.Add(_scan);
        _scan.ToolTip = "只读取世界元数据与备份，检查文件是否损坏，不修改任何存档内容。";
        StyleButton(_restore, true); _restore.Click += async (_, _) => await RestoreAsync(); actions.Children.Add(_restore);
        StyleButton(_backups, false); _backups.ToolTip = "备份整个世界，或将完整备份恢复成一个独立副本。";
        _backups.Click += async (_, _) =>
        {
            try { await OverlayDialogService.ShowModalAsync(new SaveBackupsDialog(owner, _directory, Path.GetFileName(_directory))); }
            catch (Exception ex) { _status.Text = "无法打开备份管理：" + ex.Message; }
        }; actions.Children.Add(_backups);
        var close = new Button { Content = "完成" }; StyleButton(close, false); close.Click += (_, _) => RequestDismiss(); actions.Children.Add(close);
        body.Children.Add(actions); body.Children.Add(_status);
        _candidates.SelectionChanged += (_, _) => _restore.IsEnabled = !_busy && _candidates.SelectedItem is CandidateRow;
        Content = new ScrollViewer { Content = body, MaxHeight = Math.Max(280, owner.ActualHeight - 100), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        OverlayClosed += (_, _) => { _closed = true; _scanStop.Cancel(); };
        Loaded += async (_, _) => { if (_initialScan) return; _initialScan = true; await ScanAsync(); };
    }
    public void RequestDismiss()
    {
        if (_busy) { _status.Text = "正在处理存档，请等待当前操作完成后关闭。"; return; }
        Close();
    }
    private static void StyleButton(Button button, bool primary)
    { button.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : "CompactActionButton"); button.Padding = new Thickness(11, 5, 11, 5); button.MinHeight = 32; button.FontSize = 13; button.FontWeight = FontWeights.Normal; button.HorizontalAlignment = HorizontalAlignment.Left; button.Margin = new Thickness(0, 0, 6, 4); }
    private async Task ScanAsync()
    {
        if (_closed) return;
        if (_busy) { _status.Text = "正在处理存档，请等待当前操作完成。"; return; }
        _busy = true; _scan.IsEnabled = _restore.IsEnabled = _backups.IsEnabled = _candidates.IsEnabled = false; _status.Text = "正在只读检查…";
        _candidates.ItemsSource = null; _report.Text = "正在检查 level.dat、元数据备份与世界目录…";
        try
        {
            var report = await Task.Run(() => WorldArchiveService.Inspect(_directory, _scanStop.Token));
            if (_closed) return;
            _report.Text = string.Join("\n\n", report.Findings); _candidates.ItemsSource = report.Candidates.Select(c => new CandidateRow(c)).ToArray();
            _candidates.SelectedIndex = report.Candidates.Count > 0 ? 0 : -1;
            _status.Text = report.Candidates.Count == 0 ? "检查完成，没有可用元数据备份，因此恢复按钮不可用；可打开完整备份与恢复副本。" : "检查完成。选择备份后可恢复；执行前请退出游戏或停止服务器。";
            _restore.ToolTip = report.Candidates.Count == 0 ? "此世界没有验证通过的 level.dat_old 或 xcl2-backups 元数据备份。" : "先保存当前元数据，再恢复选中的备份。";
            ToolTipService.SetShowOnDisabled(_restore, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "检查未完成：" + ex.Message; }
        finally { _busy = false; _scan.IsEnabled = _backups.IsEnabled = _candidates.IsEnabled = !_closed; _restore.IsEnabled = !_closed && _candidates.SelectedItem is CandidateRow; }
    }
    private async Task RestoreAsync()
    {
        if (_closed) return;
        if (_busy) { _status.Text = "正在处理存档，请等待当前操作完成。"; return; }
        if (_candidates.SelectedItem is not CandidateRow row) { _status.Text = "请先选择一份通过检查的元数据备份。"; return; }
        _busy = true; _scan.IsEnabled = _restore.IsEnabled = _backups.IsEnabled = _candidates.IsEnabled = false;
        try
        {
            await _owner.ProcessManager.RefreshAsync();
            if (ScheduledSaveBackupService.IsWorldRunning(_owner, _directory))
            { _status.Text = "此存档所属的游戏或服务器仍在运行，请先关闭后再恢复。"; return; }
            if (_closed) return;
            if (await MessageBoxDialog.ShowConfirmAsync($"恢复来源：{row.Label}\n\n先备份当前 level.dat，再恢复这份元数据。世界设置、时间和单人玩家数据将回到备份状态。请确认服务器和游戏都已关闭。", "确认恢复存档元数据", "备份并恢复") != true) { _status.Text = "已取消恢复，存档保持不变。"; return; }
            await _owner.ProcessManager.RefreshAsync();
            if (_closed) return;
            if (ScheduledSaveBackupService.IsWorldRunning(_owner, _directory))
            { _status.Text = "确认期间检测到游戏或服务器启动，已停止恢复。请关闭后重试。"; return; }
            _status.Text = "正在备份原始元数据并恢复…";
            var backup = await Task.Run(() => WorldArchiveService.RestoreMetadata(_directory, row.Candidate));
            Repaired = true; _status.Text = "元数据已恢复。修改前的原文件保存在：" + backup; _report.Text += "\n\n✓ 恢复完成，可关闭此窗口并重新选择存档。";
        }
        catch (Exception ex) { _status.Text = "恢复未完成：" + ex.Message; }
        finally { _busy = false; _scan.IsEnabled = _backups.IsEnabled = _candidates.IsEnabled = !_closed; _restore.IsEnabled = !_closed && _candidates.SelectedItem is CandidateRow; }
    }
}
