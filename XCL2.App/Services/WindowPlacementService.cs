using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using XCL2.App.Models;
using Screen = System.Windows.Forms.Screen;

namespace XCL2.App.Services;

/// <summary>保存启动器自身位置；使用实际显示器工作区，移除显示器后仍能找回窗口。</summary>
public sealed class WindowPlacementService : IDisposable
{
    private readonly Window _window;
    private readonly ConfigService _config;
    private readonly bool _temporaryGeometryOverride;
    private bool _loaded, _disposed, _moving, _displayLayoutChanged;
    private LauncherWindowPlacement? _lastNormal;
    private WindowState _lastNonMinimizedState;

    private WindowPlacementService(Window window, ConfigService config, bool temporaryGeometryOverride)
    {
        _window = window;
        _lastNonMinimizedState = window.WindowState == WindowState.Minimized ? WindowState.Normal : window.WindowState;
        _config = config;
        _temporaryGeometryOverride = temporaryGeometryOverride;
        window.Loaded += OnLoaded;
        window.LocationChanged += OnGeometryChanged;
        window.SizeChanged += OnSizeChanged;
        window.StateChanged += OnGeometryChanged;
        window.Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        if (window.IsLoaded) OnLoaded(window, new RoutedEventArgs());
    }

    public static WindowPlacementService Attach(Window window, ConfigService config, bool temporaryGeometryOverride = false)
        => new(window, config, temporaryGeometryOverride);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || _disposed) return;
        _loaded = true;
        var saved = _config.Config.RememberWindowPlacement && !_temporaryGeometryOverride
            ? _config.Config.LauncherWindowPlacement : null;
        if (saved != null) Apply(saved);
        else EnsureVisible();
        _lastNonMinimizedState = _window.WindowState == WindowState.Minimized ? WindowState.Normal : _window.WindowState;
        Capture();
    }

    private void OnGeometryChanged(object? sender, EventArgs e)
    {
        if (!_loaded || _moving) return;
        if (_window.WindowState != WindowState.Minimized && !AprilFoolsUi.IsWindowGeometryTemporary(_window))
            _lastNonMinimizedState = _window.WindowState;
        if (_displayLayoutChanged && _window.WindowState == WindowState.Normal)
        {
            _displayLayoutChanged = false;
            EnsureVisible();
        }
        Capture();
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) { if (_loaded && !_moving) Capture(); }

    private void Capture()
    {
        if (AprilFoolsUi.IsWindowGeometryTemporary(_window) || _window.WindowState != WindowState.Normal || !TryBounds(out var bounds)) return;
        var screen = Screen.FromHandle(new WindowInteropHelper(_window).Handle);
        var area = screen.WorkingArea;
        var dpi = MonitorDpi(screen);
        _lastNormal = new LauncherWindowPlacement
        {
            Monitor = screen.DeviceName,
            Width = bounds.Width / dpi,
            Height = bounds.Height / dpi,
            HorizontalPosition = Position(bounds.Left - area.Left, area.Width - bounds.Width),
            VerticalPosition = Position(bounds.Top - area.Top, area.Height - bounds.Height)
        };
    }

    private static double Position(double offset, double available)
        => available <= 0 ? 0 : Math.Clamp(offset / available, 0, 1);

    private void Apply(LauncherWindowPlacement placement)
    {
        var screens = Screen.AllScreens;
        var target = screens.FirstOrDefault(s => s.DeviceName == placement.Monitor)
                     ?? Screen.FromHandle(new WindowInteropHelper(_window).Handle);
        var area = target.WorkingArea;
        var dpi = MonitorDpi(target);
        var width = Math.Min(area.Width, Math.Max(_window.MinWidth * dpi, Finite(placement.Width, 1000) * dpi));
        var height = Math.Min(area.Height, Math.Max(_window.MinHeight * dpi, Finite(placement.Height, 640) * dpi));
        var x = area.Left + Math.Max(0, area.Width - width) * Math.Clamp(Finite(placement.HorizontalPosition, .5), 0, 1);
        var y = area.Top + Math.Max(0, area.Height - height) * Math.Clamp(Finite(placement.VerticalPosition, .5), 0, 1);
        _moving = true;
        try
        {
            _window.WindowState = WindowState.Normal;
            SetWindowPos(new WindowInteropHelper(_window).Handle, IntPtr.Zero,
                (int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(width), (int)Math.Round(height), 0x0014);
            Capture();
            if (placement.Maximized) _window.WindowState = WindowState.Maximized;
            _lastNonMinimizedState = _window.WindowState;
        }
        finally { _moving = false; }
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) && value >= 0 ? value : fallback;

    private void EnsureVisible()
    {
        if (_window.WindowState != WindowState.Normal || !TryBounds(out var bounds)) return;
        // 全部窗口都在同一显示器内才不处理；显示器移除、任务栏位置变化时重新放回工作区。
        if (Screen.AllScreens.Any(s => s.WorkingArea.Contains(bounds))) return;
        var target = Screen.FromRectangle(bounds);
        var area = target.WorkingArea;
        var width = Math.Min(bounds.Width, area.Width);
        var height = Math.Min(bounds.Height, area.Height);
        var x = Math.Clamp(bounds.Left, area.Left, area.Right - width);
        var y = Math.Clamp(bounds.Top, area.Top, area.Bottom - height);
        _moving = true;
        try { SetWindowPos(new WindowInteropHelper(_window).Handle, IntPtr.Zero, x, y, width, height, 0x0014); }
        finally { _moving = false; }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_disposed || !_loaded) return;
            if (_window.WindowState == WindowState.Minimized)
            {
                // 拔掉显示器不能把最小化/托盘中的启动器弹回前台；等用户还原时再校正。
                _displayLayoutChanged = true;
                return;
            }
            if (_window.WindowState == WindowState.Normal) EnsureVisible();
            else if (_lastNormal != null)
            {
                // 最大化窗口的还原位置也需移到仍存在的屏幕，避免下一次还原到屏幕外。
                var copy = _lastNormal;
                copy.Maximized = _window.WindowState == WindowState.Maximized;
                Apply(copy);
            }
            Capture();
        }));
    }

    public void Reset()
    {
        _config.Config.LauncherWindowPlacement = null;
        Apply(new LauncherWindowPlacement());
        Capture();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (!_temporaryGeometryOverride && _config.Config.RememberWindowPlacement && _lastNormal != null
            && !_config.Config.GuestModeEnabled)
        {
            _lastNormal.Maximized = (AprilFoolsUi.OriginalWindowState(_window) ?? (_window.WindowState == WindowState.Minimized ? _lastNonMinimizedState : _window.WindowState)) == WindowState.Maximized;
            _config.Config.LauncherWindowPlacement = _lastNormal;
            try { _config.Save(); }
            catch (Exception ex) { ErrorPresenter.LogFallback("保存启动器窗口位置失败", ex); }
        }
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Loaded -= OnLoaded;
        _window.LocationChanged -= OnGeometryChanged;
        _window.SizeChanged -= OnSizeChanged;
        _window.StateChanged -= OnGeometryChanged;
        _window.Closed -= OnClosed;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private bool TryBounds(out System.Drawing.Rectangle bounds)
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
        {
            bounds = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return bounds.Width > 0 && bounds.Height > 0;
        }
        bounds = default;
        return false;
    }

    private static double MonitorDpi(Screen screen)
    {
        try
        {
            var point = new POINT { X = screen.Bounds.Left + screen.Bounds.Width / 2, Y = screen.Bounds.Top + screen.Bounds.Height / 2 };
            var monitor = MonitorFromPoint(point, 2);
            if (GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0) return dpi / 96d;
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
