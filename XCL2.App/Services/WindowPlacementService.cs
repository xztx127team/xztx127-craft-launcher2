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
    private bool _loaded, _disposed, _moving, _displayLayoutChanged, _initialPlacementApplied, _resetAfterFullScreen;
    private LauncherWindowPlacement? _lastNormal;
    private WindowState _lastNonMinimizedState;

    private WindowPlacementService(Window window, ConfigService config, bool temporaryGeometryOverride)
    {
        _window = window;
        _lastNonMinimizedState = window.WindowState == WindowState.Minimized ? WindowState.Normal : window.WindowState;
        _config = config;
        _temporaryGeometryOverride = temporaryGeometryOverride;
        // SourceInitialized runs before the first native ShowWindow. Do not let CenterScreen
        // subsequently overwrite the recovered monitor/position during the same Show().
        // First launch (no saved rectangle) is centered explicitly in ApplyInitialPlacement, so it also uses Manual.
        if (SavedPlacement != null || !_temporaryGeometryOverride) window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.SourceInitialized += OnSourceInitialized;
        window.Loaded += OnLoaded;
        window.LocationChanged += OnGeometryChanged;
        window.SizeChanged += OnSizeChanged;
        window.StateChanged += OnGeometryChanged;
        window.Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        WindowChromeService.FullScreenChanged += OnFullScreenChanged;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) ApplyInitialPlacement();
        if (window.IsLoaded) OnLoaded(window, new RoutedEventArgs());
    }

    public static WindowPlacementService Attach(Window window, ConfigService config, bool temporaryGeometryOverride = false)
        => new(window, config, temporaryGeometryOverride);

    private LauncherWindowPlacement? SavedPlacement =>
        _config.Config.RememberWindowPlacement && !_temporaryGeometryOverride
            ? _config.Config.LauncherWindowPlacement : null;

    private bool IsTemporaryGeometry => AprilFoolsUi.IsWindowGeometryTemporary(_window)
        || WindowChromeService.IsFullScreenGeometryTemporary(_window);

    private void OnSourceInitialized(object? sender, EventArgs e) => ApplyInitialPlacement();

    private void ApplyInitialPlacement()
    {
        if (_disposed || _initialPlacementApplied) return;
        _initialPlacementApplied = true;
        if (SavedPlacement is { } saved) Apply(saved);
        // 首次启动：按当前显示器工作区居中显示默认大小，而不是停在左上角。
        else if (!_temporaryGeometryOverride) Apply(new LauncherWindowPlacement());
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || _disposed) return;
        _loaded = true;
        ApplyInitialPlacement();
        EnsureVisible();
        _lastNonMinimizedState = _window.WindowState == WindowState.Minimized ? WindowState.Normal : _window.WindowState;
        Capture();
    }

    private void OnGeometryChanged(object? sender, EventArgs e)
    {
        if (!_loaded || _moving || IsTemporaryGeometry) return;
        if (_window.WindowState != WindowState.Minimized)
            _lastNonMinimizedState = _window.WindowState;
        if (_displayLayoutChanged && _window.WindowState == WindowState.Normal)
        {
            _displayLayoutChanged = false;
            EnsureVisible();
        }
        Capture();
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) { if (_loaded && !_moving) Capture(); }

    private void OnFullScreenChanged(Window window)
    {
        if (!ReferenceEquals(window, _window)) return;
        if (_resetAfterFullScreen && !IsTemporaryGeometry)
        {
            _resetAfterFullScreen = false;
            Apply(new LauncherWindowPlacement());
        }
        OnGeometryChanged(window, EventArgs.Empty);
    }

    private void Capture()
    {
        if (IsTemporaryGeometry || _window.WindowState != WindowState.Normal || !TryBounds(out var bounds)) return;
        var screen = Screen.FromHandle(new WindowInteropHelper(_window).Handle);
        var area = screen.WorkingArea;
        var dpi = MonitorDpi(screen);
        _lastNormal = new LauncherWindowPlacement
        {
            Monitor = screen.DeviceName,
            Width = bounds.Width / dpi,
            Height = bounds.Height / dpi,
            HorizontalPosition = WindowPlacementGeometry.RelativePosition(bounds.Left - area.Left, area.Width - bounds.Width),
            VerticalPosition = WindowPlacementGeometry.RelativePosition(bounds.Top - area.Top, area.Height - bounds.Height)
        };
    }

    private void Apply(LauncherWindowPlacement placement)
    {
        var screens = Screen.AllScreens;
        var target = screens.FirstOrDefault(s => s.DeviceName == placement.Monitor)
                     ?? Screen.FromHandle(new WindowInteropHelper(_window).Handle);
        var area = target.WorkingArea;
        var dpi = MonitorDpi(target);
        var bounds = WindowPlacementGeometry.Resolve(placement,
            new(area.Left, area.Top, area.Width, area.Height), dpi, _window.MinWidth, _window.MinHeight);
        _moving = true;
        try
        {
            _window.WindowState = WindowState.Normal;
            SetWindowPos(new WindowInteropHelper(_window).Handle, IntPtr.Zero,
                bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0014);
            Capture();
            if (placement.Maximized) _window.WindowState = WindowState.Maximized;
            _lastNonMinimizedState = _window.WindowState;
        }
        finally { _moving = false; }
    }

    private void EnsureVisible()
    {
        if (IsTemporaryGeometry || _window.WindowState != WindowState.Normal || !TryBounds(out var bounds)) return;
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
            if (_window.WindowState == WindowState.Minimized || IsTemporaryGeometry)
            {
                // 拔掉显示器不能把最小化/托盘中的启动器弹回前台；等用户还原时再校正。
                _displayLayoutChanged = true;
                return;
            }
            if (_window.WindowState == WindowState.Normal) EnsureVisible();
            else if (_lastNormal != null)
            {
                // 最大化窗口的还原位置也需移到仍存在的屏幕，避免下一次还原到屏幕外。
                var copy = new LauncherWindowPlacement
                {
                    Monitor = _lastNormal.Monitor, Width = _lastNormal.Width, Height = _lastNormal.Height,
                    HorizontalPosition = _lastNormal.HorizontalPosition, VerticalPosition = _lastNormal.VerticalPosition,
                    Maximized = _window.WindowState == WindowState.Maximized
                };
                Apply(copy);
            }
            Capture();
        }));
    }

    public void Reset()
    {
        _config.Config.LauncherWindowPlacement = null;
        if (WindowChromeService.IsFullScreenGeometryTemporary(_window))
        {
            // Keep the host's F11 title bar/layout state intact; apply the reset when it exits.
            _resetAfterFullScreen = true;
            _lastNormal = new LauncherWindowPlacement();
            return;
        }
        Apply(new LauncherWindowPlacement());
        Capture();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (!_temporaryGeometryOverride && _config.Config.RememberWindowPlacement && _lastNormal != null
            && !_config.Config.GuestModeEnabled)
        {
            // F11 and minimized HWND states are temporary. Persist the ordinary state that
            // the user had before them, along with the last ordinary restore rectangle.
            _lastNormal.Maximized = !_resetAfterFullScreen && (AprilFoolsUi.OriginalWindowState(_window)
                ?? WindowChromeService.GetPreFullScreenState(_window)
                ?? _lastNonMinimizedState) == WindowState.Maximized;
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
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Loaded -= OnLoaded;
        _window.LocationChanged -= OnGeometryChanged;
        _window.SizeChanged -= OnSizeChanged;
        _window.StateChanged -= OnGeometryChanged;
        _window.Closed -= OnClosed;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        WindowChromeService.FullScreenChanged -= OnFullScreenChanged;
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
            if (WindowsCompatibilityService.Current.SupportsPerMonitorDpi
                && GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0) return dpi / 96d;
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
        // Vista / Windows 7 / Windows 8 have system DPI, not per-monitor DPI.
        // Assuming 96 here makes restored windows too small at 125% / 150% scaling.
        var dc = IntPtr.Zero;
        try
        {
            dc = GetDC(IntPtr.Zero);
            if (dc != IntPtr.Zero)
            {
                var dpi = GetDeviceCaps(dc, 88 /* LOGPIXELSX */);
                if (dpi > 0) return dpi / 96d;
            }
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
        finally { if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc); }
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
}
