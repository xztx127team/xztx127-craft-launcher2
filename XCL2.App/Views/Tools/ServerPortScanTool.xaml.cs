using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views.Tools;

public partial class ServerPortScanTool : UserControl
{
    private const int MaximumVisibleResults = 5000;
    private readonly ObservableCollection<OpenPortRow> _rows = new();
    private readonly DispatcherTimer _refreshTimer;
    private ScanSession? _session;
    private bool _ready;
    private CancellationTokenSource? _minecraftCancellation;

    public ServerPortScanTool()
    {
        InitializeComponent();
        ScanResultsGrid.ItemsSource = _rows;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _refreshTimer.Tick += (_, _) => RefreshScanUi();
        Loaded += (_, _) =>
        {
            RefreshScanUi();
            if (_session is { } session && (!session.Finished || !session.OpenPorts.IsEmpty)) _refreshTimer.Start();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (_ready && !IsVisible) { CancelScan(); _minecraftCancellation?.Cancel(); _refreshTimer.Stop(); }
        };
        _ready = true;
        UpdateScanPlan();
        UpdateMinecraftValidation();
    }

    private bool IsScanning => _session is { Finished: false } || _minecraftCancellation != null;

    private void ScanInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (_ready) UpdateScanPlan();
    }

    private bool TryReadRequest(out ServerPortScanRequest? request, out string? error)
    {
        request = null;
        if (!TryInteger(ScanStartPortBox.Text, out var first) || !TryInteger(ScanEndPortBox.Text, out var last) ||
            !TryInteger(ScanMaximumPortsBox.Text, out var maximum))
        {
            error = "开始端口、结束端口和最大数量都应填写 1–65535 的整数。";
            return false;
        }
        return ServerPortScanService.TryCreateRequest(ScanAddressesBox.Text, first, last, maximum, out request, out error);
    }

    private static bool TryInteger(string text, out int number)
        => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private void UpdateScanPlan()
    {
        var valid = TryReadRequest(out var request, out var error);
        ScanStartButton.IsEnabled = valid && !IsScanning;
        ScanPlanText.Foreground = valid ? (Brush)FindResource("TextSecondaryBrush") : Brushes.IndianRed;
        if (!valid) { ScanPlanText.Text = error; return; }
        ScanPlanText.Text = $"{request!.Addresses.Count} 个 IP · 每个 IP 探测 {request.PortsPerAddress:N0} 个端口" +
            $"（{request.StartPort}–{request.EffectiveEndPort}）· 共 {request.TotalProbes:N0} 次。" +
            (request.WasTruncated ? " 已按最大数量截取范围起始部分。" : "");
    }

    private async void ScanStart_Click(object sender, RoutedEventArgs e)
    {
        if (IsScanning) return;
        if (!TryReadRequest(out var request, out _))
        {
            UpdateScanPlan();
            return;
        }
        var session = new ScanSession();
        _session = session;
        _rows.Clear();
        ProbeModeBox.IsEnabled = false;
        ScanInputsPanel.IsEnabled = ScanStartButton.IsEnabled = ScanCopyButton.IsEnabled = false;
        ScanCancelButton.IsEnabled = true;
        ScanResultLimitText.Visibility = Visibility.Collapsed;
        ScanErrorText.Visibility = Visibility.Collapsed;
        ScanProgressBar.Maximum = request!.TotalProbes;
        ScanProgressBar.Value = 0;
        ShowCounts(0, request.TotalProbes, 0, 0, 0, 0);
        ScanStatusText.Text = "正在探测…";
        _refreshTimer.Start();
        // Background workers report into a bounded queue, never one Dispatcher callback per port.
        var progress = new CallbackProgress<ServerPortScanProgress>(value => Volatile.Write(ref session.Progress, value));
        var openPorts = new CallbackProgress<ServerPortProbeResult>(value =>
        {
            if (Interlocked.Increment(ref session.DiscoveredResults) <= MaximumVisibleResults) session.OpenPorts.Enqueue(value);
        });
        try
        {
            session.Summary = await ServerPortScanService.ScanAsync(request, progress, openPorts, session.Cancellation.Token);
        }
        catch (OperationCanceledException) { session.Failure = "探测已取消，保留已经发现的开放端口。"; }
        catch (Exception ex) { session.Failure = "探测失败：" + ex.Message; }
        finally
        {
            session.Finished = true;
            session.Cancellation.Dispose();
            if (ReferenceEquals(_session, session))
            {
                ProbeModeBox.IsEnabled = ScanInputsPanel.IsEnabled = true;
                ScanCancelButton.IsEnabled = false;
                UpdateScanPlan();
                RefreshScanUi();
                if (!IsLoaded) _refreshTimer.Stop();
            }
        }
    }

    private void RefreshScanUi()
    {
        if (_session is not { } session) return;
        // Keep large result sets virtualized and bound the work performed during a single UI tick.
        for (var i = 0; i < 256 && session.OpenPorts.TryDequeue(out var result); i++)
            _rows.Add(new OpenPortRow(result.DisplayAddress, $"{result.ElapsedMilliseconds} ms"));
        var resultsReady = session.Finished && session.OpenPorts.IsEmpty;
        ScanCopyButton.IsEnabled = resultsReady && _rows.Count > 0;
        var progress = Volatile.Read(ref session.Progress);
        if (session.Summary is { } summary)
        {
            ShowCounts(summary.Completed, summary.Total, summary.Open, summary.Refused, summary.TimedOut, summary.Errors);
            ScanStatusText.Text = summary.Cancelled
                ? "探测已取消，保留已经发现的开放端口。"
                : summary.Open == 0 ? "探测完成，本次未发现开放的 TCP 端口。" : "探测完成。";
            if (!resultsReady)
                ScanStatusText.Text = summary.Cancelled ? "已取消，正在整理已发现的开放端口…" : "探测完成，正在整理开放端口列表…";
            var detail = summary.LastError;
            if (summary.NotificationError != null)
                detail = (detail == null ? "" : detail + "\n") + "结果通知失败：" + summary.NotificationError;
            ScanErrorText.Text = detail == null ? "" : "最近一次错误：" + detail;
            ScanErrorText.Visibility = detail == null ? Visibility.Collapsed : Visibility.Visible;
        }
        else if (progress != null)
            ShowCounts(progress.Completed, progress.Total, progress.Open, progress.Refused, progress.TimedOut, progress.Errors);
        if (session.Failure != null) ScanStatusText.Text = session.Failure;
        else if (!session.Finished) ScanStatusText.Text = session.CancelRequested ? "正在取消…" : "正在探测…";
        if (session.Finished && session.OpenPorts.IsEmpty) _refreshTimer.Stop();
    }

    private void ShowCounts(int completed, int total, int open, int refused, int timedOut, int errors)
    {
        ScanProgressBar.Maximum = Math.Max(1, total);
        ScanProgressBar.Value = completed;
        ScanCountsText.Text = $"已完成 {completed:N0} / {total:N0} · 开放 {open:N0} · 拒绝 {refused:N0} · 超时 {timedOut:N0} · 错误 {errors:N0}";
        ScanResultLimitText.Visibility = open > MaximumVisibleResults ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScanCancel_Click(object sender, RoutedEventArgs e) => CancelScan();

    private void CancelScan()
    {
        if (_session is not { Finished: false } session || session.CancelRequested) return;
        session.CancelRequested = true;
        session.Cancellation.Cancel();
        ScanCancelButton.IsEnabled = false;
        ScanStatusText.Text = "正在取消…";
    }

    private void ServerPortScanTool_Unloaded(object sender, RoutedEventArgs e)
    {
        CancelScan();
        _minecraftCancellation?.Cancel();
        _refreshTimer?.Stop();
    }

    private void ScanCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0 || _session is not { Finished: true } session || !session.OpenPorts.IsEmpty) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, _rows.Select(row => row.Endpoint))); }
        catch (ExternalException) { ToastService.ShowWarning("剪贴板暂时不可用，请稍后再试。"); }
    }

    private void ProbeMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var minecraft = ProbeModeBox.SelectedIndex == 0;
        MinecraftProbePanel.Visibility = minecraft ? Visibility.Visible : Visibility.Collapsed;
        PortRangePanel.Visibility = minecraft ? Visibility.Collapsed : Visibility.Visible;
        if (minecraft) UpdateMinecraftValidation(); else UpdateScanPlan();
    }

    private void MinecraftInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (_ready) UpdateMinecraftValidation();
    }

    private void UpdateMinecraftValidation()
    {
        var valid = ServerPingAddress.TryParse(MinecraftAddressBox.Text, MinecraftPortBox.Text, out _, out var error);
        MinecraftValidationText.Text = valid ? "" : error;
        MinecraftStartButton.IsEnabled = valid && !IsScanning;
    }

    private async void MinecraftStart_Click(object sender, RoutedEventArgs e)
    {
        if (IsScanning) return;
        if (!ServerPingAddress.TryParse(MinecraftAddressBox.Text, MinecraftPortBox.Text, out var endpoint, out var error))
        { UpdateMinecraftValidation(); ToastService.ShowWarning(error ?? "服务器地址不正确。"); return; }
        var cancellation = new CancellationTokenSource();
        _minecraftCancellation = cancellation;
        ProbeModeBox.IsEnabled = MinecraftAddressBox.IsEnabled = MinecraftPortBox.IsEnabled = false;
        MinecraftStartButton.IsEnabled = MinecraftCopyButton.IsEnabled = false;
        MinecraftCancelButton.IsEnabled = true;
        MinecraftProgress.Visibility = Visibility.Visible;
        MinecraftResultBox.Foreground = (Brush)FindResource("TextPrimaryBrush");
        MinecraftResultBox.Text = "正在解析地址并读取 Minecraft 服务器状态…";
        try
        {
            var result = await ServerPingService.PingAsync(endpoint!, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            MinecraftResultBox.Text = $"目标：{result.ServerAddress ?? endpoint!.DisplayAddress}\n" +
                (result.ResolutionNote == null ? "" : result.ResolutionNote + "\n") +
                (result.Success || result.StatusReceived
                    ? $"协议：Minecraft Java 状态查询\n版本：{result.VersionName ?? "未知"}\n" +
                      $"当前人数：{result.OnlinePlayers?.ToString() ?? "未知"} / {result.MaxPlayers?.ToString() ?? "未知"}\n" +
                      $"响应延迟：{(result.LatencyMs.HasValue ? result.LatencyMs + " ms" : "未取得有效 Pong")}\n服务器标语（MOTD）：\n{result.MotdPlain ?? "（空）"}" +
                      (result.Success ? "" : "\n\n提示：" + result.ErrorMessage)
                    : "探测失败：" + result.ErrorMessage + "\n请确认该地址是 Java 版游戏服务器；网页能打开并不代表它提供 Minecraft 服务。");
            if (!result.Success)
            {
                MinecraftResultBox.Foreground = Brushes.IndianRed;
                ToastService.ShowWarning(result.ErrorMessage ?? "Minecraft 服务器未响应。");
            }
            MinecraftCopyButton.IsEnabled = true;
        }
        catch (OperationCanceledException) { MinecraftResultBox.Text = "探测已取消。"; }
        catch (Exception ex)
        {
            MinecraftResultBox.Foreground = Brushes.IndianRed;
            MinecraftResultBox.Text = "探测失败：" + ex.Message;
            ToastService.ShowWarning("探测失败，请检查地址和网络后重试。");
        }
        finally
        {
            _minecraftCancellation = null;
            cancellation.Dispose();
            ProbeModeBox.IsEnabled = MinecraftAddressBox.IsEnabled = MinecraftPortBox.IsEnabled = true;
            MinecraftCancelButton.IsEnabled = false;
            MinecraftProgress.Visibility = Visibility.Collapsed;
            UpdateMinecraftValidation();
        }
    }

    private void MinecraftCancel_Click(object sender, RoutedEventArgs e) => _minecraftCancellation?.Cancel();
    private void MinecraftCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(MinecraftResultBox.Text); ToastService.ShowSuccess("已复制探测结果。"); }
        catch (ExternalException) { ToastService.ShowWarning("剪贴板暂时不可用，请稍后再试。"); }
    }

    public sealed record OpenPortRow(string Endpoint, string Elapsed);

    private sealed class ScanSession
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly ConcurrentQueue<ServerPortProbeResult> OpenPorts = new();
        public ServerPortScanProgress? Progress;
        public ServerPortScanSummary? Summary;
        public int DiscoveredResults;
        public bool Finished, CancelRequested;
        public string? Failure;
    }

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
