using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

internal sealed class AiFloatingBallWindow : Window, IGlobalWindowTreatmentExempt
{
    private readonly AiFloatingButton _ball = new();
    private readonly Action _positionChanged;
    private readonly Action _closedByUser;
    private Point _down;
    private bool _dragged;
    private bool _hostClosing;
    private bool _closed;

    internal AiFloatingBallWindow(AiFloatingBetaSettings settings, Action openChat,
        Func<System.Windows.Controls.ContextMenu> menu, Action positionChanged, Action closedByUser)
    {
        _positionChanged = positionChanged;
        _closedByUser = closedByUser;
        Width = Height = 60;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = settings.BallTopmost;
        ShowInTaskbar = false;
        ShowActivated = false;
        Content = _ball;
        _ball.Margin = new Thickness(6);
        _ball.ApplySettings(settings, 48);
        _ball.BallButton.ContextMenu = menu();
        _ball.Clicked += (_, _) => { if (!_dragged) openChat(); };
        _ball.PreviewMouseLeftButtonDown += (_, e) => { _down = e.GetPosition(this); _dragged = false; };
        _ball.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragged || (e.GetPosition(this) - _down).Length < 7) return;
            _dragged = true;
            _ball.BallButton.ReleaseMouseCapture();
            e.Handled = true;
            try { DragMove(); }
            catch (InvalidOperationException) { return; }
            ClampPosition();
            _positionChanged();
        };
        Loaded += (_, _) => { ClampPosition(); SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged; };
        Closed += (_, _) =>
        {
            _closed = true;
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            if (!_hostClosing) _closedByUser();
        };
        var work = SystemParameters.WorkArea;
        Left = settings.DesktopLeft is { } x && double.IsFinite(x) ? x : work.Right - Width - 22;
        Top = settings.DesktopTop is { } y && double.IsFinite(y) ? y : work.Bottom - Height - 24;
        ClampPosition();
    }

    internal void ApplySettings(AiFloatingBetaSettings settings)
    {
        Topmost = settings.BallTopmost;
        _ball.ApplySettings(settings, 48);
    }

    private void ClampPosition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var bounds))
        {
            // Use an actual monitor's pixel work area, not the virtual desktop's bounding rectangle:
            // unequal/offset monitors can leave gaps where a saved ball would be unreachable.
            var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var x = Math.Clamp(bounds.Left, work.Left, Math.Max(work.Left, work.Right - (bounds.Right - bounds.Left)));
            var y = Math.Clamp(bounds.Top, work.Top, Math.Max(work.Top, work.Bottom - (bounds.Bottom - bounds.Top)));
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
            return;
        }
        // Before HWND creation, use a coarse bound; Loaded performs the monitor-aware correction.
        Left = Math.Clamp(Left, SystemParameters.VirtualScreenLeft,
            Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
        Top = Math.Clamp(Top, SystemParameters.VirtualScreenTop,
            Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        try { _ = Dispatcher.BeginInvoke(new Action(() => { if (!_closed) ClampPosition(); })); }
        catch (InvalidOperationException) { /* dispatcher is shutting down */ }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    internal void CloseFromHost() { _hostClosing = true; Close(); }
}
