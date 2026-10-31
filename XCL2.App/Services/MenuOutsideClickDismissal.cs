using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>
/// NOACTIVATE 浮窗的菜单不能依赖失焦关闭。只在菜单打开期间监听鼠标按下；
/// 不截获点击、不记录输入，子菜单同样属于菜单内部，关闭后立即移除监听。
/// </summary>
internal sealed class MenuOutsideClickDismissal : IDisposable
{
    private readonly ContextMenu _menu;
    private readonly MouseHook _callback;
    private readonly DispatcherTimer _fallback;
    private IntPtr _hook;
    private int _generation;
    private int _pressedButtons;
    private bool _disposed;

    public MenuOutsideClickDismissal(ContextMenu menu)
    {
        _menu = menu;
        _callback = OnMouse;
        _fallback = new DispatcherTimer(DispatcherPriority.Input, menu.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(25) };
        _fallback.Tick += PollMouse;
        menu.StaysOpen = false;
        menu.Opened += Opened;
        menu.Closed += Closed;
    }

    internal bool IsListening => _hook != IntPtr.Zero || _fallback.IsEnabled;

    private void Opened(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        Stop();
        _hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _callback, GetModuleHandle(null), 0);
        // Restricted desktop policy can deny hooks. Keep an input-only, menu-scoped fallback.
        if (_hook == IntPtr.Zero)
        {
            _pressedButtons = PressedButtons();
            _fallback.Start();
        }
    }

    private void Closed(object sender, RoutedEventArgs e) => Stop();

    private void Stop()
    {
        _generation++;
        _fallback.Stop();
        var hook = _hook;
        _hook = IntPtr.Zero;
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        try
        {
            if (code >= 0 && message.ToInt32() is 0x0201 or 0x0204 or 0x0207 or 0x020B)
            {
                var point = Marshal.PtrToStructure<MouseData>(data).Point;
                ProcessPointerDown(new Point(point.X, point.Y));
            }
        }
        catch { /* A menu must never interrupt another application's input. */ }
        return CallNextHookEx(_hook, code, message, data);
    }

    private void PollMouse(object? sender, EventArgs e)
    {
        var buttons = PressedButtons();
        if ((buttons & ~_pressedButtons) != 0 && GetCursorPos(out var point))
            ProcessPointerDown(new Point(point.X, point.Y));
        _pressedButtons = buttons;
    }

    private static int PressedButtons()
    {
        var result = 0;
        foreach (var key in new[] { 1, 2, 4, 5, 6 })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) result |= 1 << key;
        return result;
    }

    internal void ProcessPointerDown(Point screenPoint)
    {
        if (_disposed || !_menu.IsOpen || ContainsMenu(_menu, screenPoint)) return;
        var generation = _generation;
        _menu.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!_disposed && generation == _generation) _menu.IsOpen = false;
        }));
    }

    private static bool ContainsMenu(ItemsControl items, Point point)
    {
        if (items is ContextMenu && ContainsVisual(items, point)) return true;
        foreach (var item in items.Items.OfType<MenuItem>())
        {
            if (!item.IsSubmenuOpen) continue;
            if (item.Template?.FindName("PART_Popup", item) is Popup { IsOpen: true, Child: Visual child }
                && ContainsVisual(child, point)) return true;
            if (ContainsMenu(item, point)) return true;
        }
        return false;
    }

    private static bool ContainsVisual(Visual visual, Point point)
    {
        if (PresentationSource.FromVisual(visual) is not HwndSource source ||
            !GetWindowRect(source.Handle, out var rectangle)) return false;
        return point.X >= rectangle.Left && point.X < rectangle.Right &&
               point.Y >= rectangle.Top && point.Y < rectangle.Bottom;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _menu.Opened -= Opened;
        _menu.Closed -= Closed;
        _fallback.Tick -= PollMouse;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData
    { public NativePoint Point; public uint Mouse, Flags, Time; public UIntPtr ExtraInfo; }
    private delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, MouseHook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
