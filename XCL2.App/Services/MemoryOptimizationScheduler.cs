using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

public sealed class MemoryOptimizationScheduler : IDisposable
{
    private readonly MainWindow _owner;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _stop = new();
    private long _lastRun = Stopwatch.GetTimestamp();
    private int _interval;
    private bool _running, _disposed;

    public MemoryOptimizationScheduler(MainWindow owner)
    {
        _owner = owner;
        _timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher) { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += Tick;
        owner.ConfigService.Saved += Apply;
        owner.Closed += OwnerClosed;
        Apply();
    }

    private void OwnerClosed(object? sender, EventArgs e) => Dispose();
    private void Apply()
    {
        if (!_owner.Dispatcher.CheckAccess()) { _owner.Dispatcher.BeginInvoke(new Action(Apply)); return; }
        if (_disposed) return;
        var requested = _owner.ConfigService.Config.MemoryOptimizationIntervalMinutes;
        var interval = requested <= 0 ? 0 : Math.Clamp(requested, 5, 240);
        if (interval != _interval) _lastRun = Stopwatch.GetTimestamp();
        _interval = interval;
        if (interval == 0) _timer.Stop(); else _timer.Start();
    }

    private async void Tick(object? sender, EventArgs e)
    {
        if (_disposed || _running || _interval == 0 || Stopwatch.GetElapsedTime(_lastRun) < TimeSpan.FromMinutes(_interval)) return;
        _running = true;
        try
        {
            var result = await WorkingSetOptimizationService.RunAsync(_stop.Token);
            LauncherLogService.AppendLine("[定时内存优化] " + result.Summary);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorPresenter.LogFallback("定时内存优化失败", ex); }
        finally { _lastRun = Stopwatch.GetTimestamp(); _running = false; if (_disposed) _stop.Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop(); _timer.Tick -= Tick;
        _stop.Cancel();
        if (!_running) _stop.Dispose();
        _owner.ConfigService.Saved -= Apply;
        _owner.Closed -= OwnerClosed;
    }
}
