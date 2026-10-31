using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>普通应用窗口的指针路由：固定顶层窗口，命中其子控件，按下和抬起使用同一接收窗口。</summary>
internal sealed class WindowTouchPointer
{
    private readonly IntPtr _window;
    private readonly uint _pid;
    private readonly Dictionary<OriginalTouchInputInjector.MouseButton, Press> _pressed = new();
    private PointI _lastPoint;
    private IntPtr _lastClickTarget;
    private OriginalTouchInputInjector.MouseButton _lastClickButton;
    private PointI _lastClickPoint;
    private long _lastClickDownAt;
    private bool _lastClickReleased;

    private sealed class Press(IntPtr target)
    {
        public IntPtr Target { get; } = target;
        public int References { get; set; } = 1;
    }

    public WindowTouchPointer(IntPtr window)
    {
        _window = window;
        GetWindowThreadProcessId(window, out _pid);
    }

    private bool IsTargetAlive => _window != IntPtr.Zero && _pid != 0 && IsWindow(_window) &&
        GetWindowThreadProcessId(_window, out var owner) != 0 && owner == _pid;

    private bool IsTargetForeground => IsTargetAlive && GetForegroundWindow() == _window;

    private bool IsReceiver(IntPtr target) => IsTargetAlive && IsWindow(target) &&
        (target == _window || IsChild(_window, target));

    private IntPtr HitTest(int x, int y)
    {
        if (!IsTargetAlive) return IntPtr.Zero;
        if (!OriginalTouchInputInjector.TryGetClientAreaOnScreen(_window, out var left, out var top, out var right, out var bottom) ||
            x < left || x >= right || y < top || y >= bottom) return IntPtr.Zero;
        var target = _window;
        // ChildWindowFromPointEx ignores the overlay (a separate top-level window).
        for (var depth = 0; depth < 64; depth++)
        {
            var point = new PointI { X = x, Y = y };
            if (!ScreenToClient(target, ref point)) return IntPtr.Zero;
            var child = ChildWindowFromPointEx(target, point, 1 | 2 | 4); // invisible, disabled, transparent
            if (child == IntPtr.Zero || child == target || !IsReceiver(child)) break;
            target = child;
        }
        return target;
    }

    private IntPtr MoveReceiver(int x, int y)
    {
        var pressed = _pressed.Values.Select(p => p.Target).FirstOrDefault(IsReceiver);
        return pressed != IntPtr.Zero ? pressed : HitTest(x, y);
    }

    public bool Move(int x, int y)
    {
        if (!IsTargetForeground) return false;
        var target = MoveReceiver(x, y);
        if (target == IntPtr.Zero || !SetCursorPos(x, y)) return false;
        if (!WithinDoubleClickArea(x, y)) _lastClickTarget = IntPtr.Zero;
        _lastPoint = new PointI { X = x, Y = y };
        return Post(target, 0x0200, KeyFlags(), x, y);
    }

    public bool Button(OriginalTouchInputInjector.MouseButton button, int x, int y, bool down, bool detectDoubleClick = true)
    {
        if (down)
        {
            if (!IsTargetForeground) return false;
            if (_pressed.TryGetValue(button, out var existing))
            {
                if (!IsReceiver(existing.Target)) { _pressed.Remove(button); return false; }
                existing.References++;
                return true;
            }
            var target = HitTest(x, y);
            if (target == IntPtr.Zero) return false;
            _lastPoint = new PointI { X = x, Y = y };
            _pressed[button] = new Press(target);
            var message = ButtonMessage(button, true);
            if (detectDoubleClick)
            {
                var now = Environment.TickCount64;
                var doubleClick = _pressed.Count == 1 && _lastClickReleased && _lastClickTarget == target &&
                    _lastClickButton == button && now - _lastClickDownAt <= GetDoubleClickTime() &&
                    WithinDoubleClickArea(x, y) && (GetClassLong(target, -26 /* GCL_STYLE */) & 8 /* CS_DBLCLKS */) != 0;
                // Windows replaces the second DOWN with DBLCLK; it does not send both.
                if (doubleClick) { message += 2; _lastClickTarget = IntPtr.Zero; }
                else
                {
                    _lastClickTarget = target;
                    _lastClickButton = button;
                    _lastClickPoint = _lastPoint;
                    _lastClickDownAt = now;
                }
                _lastClickReleased = false;
            }
            if (Post(target, message, KeyFlags(), x, y)) return true;
            _pressed.Remove(button);
            _lastClickTarget = IntPtr.Zero;
            return false;
        }

        if (!_pressed.TryGetValue(button, out var pressed)) return false;
        if (--pressed.References > 0) return true;
        _pressed.Remove(button);
        if (_lastClickTarget == pressed.Target && _lastClickButton == button) _lastClickReleased = true;
        // A focus change must release the original receiver, never a new foreground window.
        return Post(pressed.Target, ButtonMessage(button, false), KeyFlags(), x, y);
    }

    public bool Wheel(int delta, int x, int y, bool horizontal = false)
    {
        if (!IsTargetForeground) return false;
        var target = HitTest(x, y);
        return Post(target, horizontal ? 0x020Eu : 0x020Au,
            KeyFlags() | ((uint)(ushort)delta << 16), x, y, screenCoordinates: true);
    }

    public void Forward(IntPtr source, int message, IntPtr wParam, IntPtr lParam)
    {
        if (!IsTargetForeground || !OriginalTouchInputInjector.TryGetMouseMessagePosition(source, message, lParam, out var x, out var y)) return;
        _lastPoint = new PointI { X = x, Y = y };
        var button = message switch
        {
            0x0201 or 0x0202 or 0x0203 => OriginalTouchInputInjector.MouseButton.Left,
            0x0204 or 0x0205 or 0x0206 => OriginalTouchInputInjector.MouseButton.Right,
            0x0207 or 0x0208 or 0x0209 => OriginalTouchInputInjector.MouseButton.Middle,
            _ => (OriginalTouchInputInjector.MouseButton?)null
        };
        if (button is { } b)
        {
            // The OS has already classified physical double-clicks; never classify them again.
            _lastClickTarget = IntPtr.Zero;
            var up = message is 0x0202 or 0x0205 or 0x0208;
            if (message is 0x0203 or 0x0206 or 0x0209)
            {
                var target = HitTest(x, y);
                if (target == IntPtr.Zero) return;
                _pressed[b] = new Press(target);
                if (!Post(target, (uint)message, KeyFlags(), x, y)) _pressed.Remove(b);
            }
            else Button(b, x, y, !up, detectDoubleClick: false);
        }
        else if (message is 0x020A or 0x020E)
            Wheel(unchecked((short)(wParam.ToInt64() >> 16)), x, y, message == 0x020E);
        else if (message == 0x0200)
            Post(MoveReceiver(x, y), (uint)message, unchecked((uint)wParam.ToInt64()) | KeyFlags(), x, y);
        else if (message is >= 0x020B and <= 0x020D)
            Post(MoveReceiver(x, y), (uint)message, unchecked((uint)wParam.ToInt64()), x, y);
    }

    public void ReleaseAll()
    {
        _lastClickTarget = IntPtr.Zero;
        foreach (var button in _pressed.Keys.ToArray())
        {
            var target = _pressed[button].Target;
            _pressed.Remove(button);
            Post(target, ButtonMessage(button, false), KeyFlags(), _lastPoint.X, _lastPoint.Y);
        }
    }

    private bool WithinDoubleClickArea(int x, int y)
        => Math.Abs((long)x - _lastClickPoint.X) <= Math.Max(1, GetSystemMetrics(36)) / 2 &&
           Math.Abs((long)y - _lastClickPoint.Y) <= Math.Max(1, GetSystemMetrics(37)) / 2;

    private uint KeyFlags()
    {
        uint flags = 0;
        if (_pressed.ContainsKey(OriginalTouchInputInjector.MouseButton.Left)) flags |= 1;
        if (_pressed.ContainsKey(OriginalTouchInputInjector.MouseButton.Right)) flags |= 2;
        if (_pressed.ContainsKey(OriginalTouchInputInjector.MouseButton.Middle)) flags |= 16;
        if ((GetKeyState(0x10) & 0x8000) != 0) flags |= 4;
        if ((GetKeyState(0x11) & 0x8000) != 0) flags |= 8;
        return flags;
    }

    private bool Post(IntPtr target, uint message, uint flags, int x, int y, bool screenCoordinates = false)
    {
        if (!IsReceiver(target)) return false;
        var point = new PointI { X = x, Y = y };
        if (!screenCoordinates && !ScreenToClient(target, ref point)) return false;
        var position = unchecked((int)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16)));
        return PostMessage(target, message, new IntPtr(unchecked((int)flags)), new IntPtr(position));
    }

    private static uint ButtonMessage(OriginalTouchInputInjector.MouseButton button, bool down) => button switch
    {
        OriginalTouchInputInjector.MouseButton.Left => down ? 0x0201u : 0x0202u,
        OriginalTouchInputInjector.MouseButton.Right => down ? 0x0204u : 0x0205u,
        _ => down ? 0x0207u : 0x0208u
    };

    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, PointI point, uint flags);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref PointI point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "GetClassLongW")] private static extern uint GetClassLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
