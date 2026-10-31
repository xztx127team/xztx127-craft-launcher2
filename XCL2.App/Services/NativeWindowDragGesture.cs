using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace XCL2.App.Services;

internal sealed class NativeWindowDragGesture
{
    private readonly Window _window;
    private readonly UIElement _grip;
    private readonly Func<MouseButtonEventArgs, bool> _canStart;
    private readonly Action _finished;
    private bool _pressed, _dragging, _finishing, _captureOwned;
    private NativePoint _pointer;
    private NativeRect _bounds;

    public NativeWindowDragGesture(Window window, UIElement grip, Func<MouseButtonEventArgs, bool> canStart, Action finished)
    {
        _window = window; _grip = grip; _canStart = canStart; _finished = finished;
        Stylus.SetIsPressAndHoldEnabled(grip, false);
        Stylus.SetIsFlicksEnabled(grip, false);
        grip.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Down), true);
        grip.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(Move), true);
        grip.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Up), true);
        grip.LostMouseCapture += (_, _) => { if (_pressed && !_finishing && !_grip.IsMouseCaptured) Finish(false); };
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _pressed) { Finish(true); e.Handled = true; } };
        window.Deactivated += (_, _) => Finish(false);
        window.Closed += (_, _) => { _pressed = _dragging = false; };
    }

    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (!_canStart(e) || e.ClickCount != 1) return;
        _pressed = GetCursorPos(out _pointer) && GetWindowRect(new WindowInteropHelper(_window).Handle, out _bounds);
        _dragging = false;
        _captureOwned = false;
        // 按钮保留短按的捕获流程，越过阈值后再接管拖动。
        if (_pressed && _grip is not ButtonBase)
        {
            _captureOwned = _grip.CaptureMouse();
            _pressed = _captureOwned;
        }
    }

    private void Move(object sender, MouseEventArgs e)
    {
        if (!_pressed) return;
        if (e.LeftButton != MouseButtonState.Pressed) { Finish(false); return; }
        if (!GetCursorPos(out var point)) return;
        var dpi = VisualTreeHelper.GetDpi(_window);
        int dx = point.X - _pointer.X, dy = point.Y - _pointer.Y;
        if (!_dragging)
        {
            if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX &&
                Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY) return;
            if (!_grip.CaptureMouse()) { _pressed = false; return; }
            _captureOwned = true;
            _dragging = true;
        }
        // 原生坐标贯穿整次拖动，跨不同缩放比例的显示器也不会累加误差。
        SetWindowPos(new WindowInteropHelper(_window).Handle, IntPtr.Zero, _bounds.Left + dx, _bounds.Top + dy, 0, 0, 0x0015);
        e.Handled = true;
    }

    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) e.Handled = true;
        Finish(false);
    }

    private void Finish(bool cancel)
    {
        if (_finishing) return;
        _finishing = true;
        var moved = _dragging;
        _pressed = _dragging = false;
        try
        {
            if (_captureOwned && _grip.IsMouseCaptured) _grip.ReleaseMouseCapture();
            _captureOwned = false;
            if (!moved) return;
            if (cancel) SetWindowPos(new WindowInteropHelper(_window).Handle, IntPtr.Zero, _bounds.Left, _bounds.Top, 0, 0, 0x0015);
            _finished();
        }
        finally { _finishing = false; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
