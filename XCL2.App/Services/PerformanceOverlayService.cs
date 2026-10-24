using System.Windows;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>性能浮窗唯一的生命周期所有者。关闭时取消采样，不在后台维护隐藏计时器。</summary>
public sealed class PerformanceOverlayService
{
    private static PerformanceOverlayService? _current;
    private readonly MainWindow _owner;
    private readonly PerformanceMetricsService _metrics = new();
    private readonly DispatcherTimer _timer;
    private PerformanceOverlayWindow? _window;
    private CancellationTokenSource? _samplingCancellation;
    private bool _sampling;
    private bool _disposed;

    public static PerformanceSnapshot? LatestSnapshot { get; private set; }
    public static bool IsPolling => _current is { _disposed: false } current && current._window?.IsVisible == true;
    public static event Action<PerformanceSnapshot>? SnapshotUpdated;
    public static event Action<AppConfig>? SettingsChanged;

    private PerformanceOverlayService(MainWindow owner)
    {
        _owner = owner;
        _timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher);
        _timer.Tick += Timer_Tick;
    }

    public static void Initialize(MainWindow owner)
    {
        if (_current != null && !ReferenceEquals(_current._owner, owner)) Shutdown();
        _current ??= new PerformanceOverlayService(owner);
        _current.Apply();
    }

    public static void ApplySettings() => _current?.Apply();

    /// <summary>Share the overlay's persisted settings path with launcher shortcuts.</summary>
    public static void UpdateSettings(Action<AppConfig> change)
    {
        var current = _current;
        if (current == null || current._disposed) return;
        if (!current._owner.Dispatcher.CheckAccess())
        { current._owner.Dispatcher.BeginInvoke(new Action(() => UpdateSettings(change))); return; }
        current.ChangeSettings(change);
    }

    public static void ResetWindowPosition()
    {
        var current = _current;
        if (current == null || current._disposed) return;
        if (!current._owner.Dispatcher.CheckAccess())
        { current._owner.Dispatcher.BeginInvoke(new Action(ResetWindowPosition)); return; }
        current.ChangeSettings(cfg => { cfg.PerformanceOverlayLeft = null; cfg.PerformanceOverlayTop = null; });
        current._window?.ResetPosition(true);
    }

    /// <summary>Settings-page hardware draft only; no configuration mutation or persistence.</summary>
    public static void PreviewHardwareSettings(AppConfig? draft)
    {
        var current = _current;
        if (current == null || current._disposed) return;
        if (!current._owner.Dispatcher.CheckAccess())
        { current._owner.Dispatcher.BeginInvoke(new Action(() => PreviewHardwareSettings(draft))); return; }
        current._window?.PreviewHardwareSettings(draft);
    }

    private void Apply()
    {
        if (_disposed) return;
        if (!_owner.Dispatcher.CheckAccess())
        {
            _owner.Dispatcher.BeginInvoke(new Action(Apply));
            return;
        }
        var cfg = _owner.ConfigService.Config;
        if (!cfg.PerformanceOverlayEnabled)
        {
            CloseWindow();
            return;
        }
        _timer.Interval = TimeSpan.FromMilliseconds(PerformanceOverlaySettings.RefreshMilliseconds(cfg));
        if (_window == null)
        {
            _window = new PerformanceOverlayWindow(() => _owner.ConfigService.Config, ChangeSettings,
                _owner.OpenPerformanceOverlaySettings, SavePosition);
            _window.IsVisibleChanged += Window_IsVisibleChanged;
            _window.Closed += Window_Closed;
            // Owner 故意不设为主窗口：主窗口最小化到托盘时，游戏性能浮窗仍须可见。
            _window.Show();
        }
        else _window.ApplySettings();
        if (_window.IsVisible) StartSampling();
    }

    private void ChangeSettings(Action<AppConfig> change)
    {
        if (_disposed) return;
        var cfg = _owner.ConfigService.Config;
        change(cfg);
        try { _owner.ConfigService.Save(); }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine("[性能悬浮窗] 保存设置失败：" + ex.Message);
            ToastService.ShowWarning("性能悬浮窗设置暂未保存，请在设置页重试。");
        }
        Apply();
        SettingsChanged?.Invoke(cfg);
    }

    private void SavePosition(double left, double top)
        => ChangeSettings(cfg => { cfg.PerformanceOverlayLeft = left; cfg.PerformanceOverlayTop = top; });

    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_window?.IsVisible == true) StartSampling();
        else StopSampling();
    }

    private void StartSampling()
    {
        if (_disposed || _window?.IsVisible != true) return;
        _samplingCancellation ??= new CancellationTokenSource();
        if (!_timer.IsEnabled)
        {
            _timer.Start();
            _ = PollAsync();
        }
    }

    private async void Timer_Tick(object? sender, EventArgs e) => await PollAsync();

    private async Task PollAsync()
    {
        if (_disposed || _sampling || _window?.IsVisible != true || _samplingCancellation == null) return;
        var cancellation = _samplingCancellation;
        _sampling = true;
        try
        {
            // 集合只在UI线程取快照；读取系统和进程指标在采样器的后台任务中执行。
            var targets = PerformanceMetricsService.CreateJavaTargets(_owner.ProcessManager.Running);
            var snapshot = await _metrics.SampleAsync(targets, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || !ReferenceEquals(cancellation, _samplingCancellation) || _window?.IsVisible != true) return;
            LatestSnapshot = snapshot;
            _window.Render(snapshot);
            SnapshotUpdated?.Invoke(snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed && !cancellation.IsCancellationRequested) _window?.ShowSamplingError();
            LauncherLogService.AppendLine("[性能悬浮窗] 读取失败：" + ex.Message);
        }
        finally { _sampling = false; }
    }

    private void StopSampling()
    {
        _timer.Stop();
        _samplingCancellation?.Cancel();
        _samplingCancellation?.Dispose();
        _samplingCancellation = null;
        LatestSnapshot = null;
        _ = _metrics.ReleaseHardwareAsync();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        StopSampling();
        _window = null;
    }

    private void CloseWindow()
    {
        StopSampling();
        if (_window == null) return;
        _window.IsVisibleChanged -= Window_IsVisibleChanged;
        _window.Closed -= Window_Closed;
        _window.Close();
        _window = null;
    }

    public static void Shutdown()
    {
        var current = _current;
        if (current == null) return;
        _current = null;
        current._disposed = true;
        current.CloseWindow();
        current._timer.Tick -= current.Timer_Tick;
        LatestSnapshot = null;
    }
}
