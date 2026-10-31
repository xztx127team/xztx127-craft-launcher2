using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace XCL2.App.Services;

/// <summary>
/// 旧版专用的原生触摸接收窗口。WM_POINTER 在这里终止，再作为 TouchDevice 送给
/// 现有 WPF 控件；不经过 DefWindowProc 的触摸转鼠标步骤，也不让 WISP 先移动光标。
/// 低级鼠标钩子只能过滤消息，不能替代这一步对系统光标位置的保护。
/// </summary>
internal sealed class LegacyTouchInputSurface : IDisposable
{
    private const int WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247;
    private const int WM_POINTERCAPTURECHANGED = 0x024C;
    private const uint PT_TOUCH = 2, PT_PEN = 3, PT_MOUSE = 4;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004, POINTER_FLAG_CANCELED = 0x00008000;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_LAYERED = 0x00080000;
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    private const string ClassName = "XCL2.LegacyTouchInputSurface";
    private static readonly WindowProc Callback = NativeWindowProc;
    private static readonly Dictionary<IntPtr, LegacyTouchInputSurface> Windows = new();
    private static readonly object ClassLock = new();
    private static bool _registered;
    private readonly Window _owner;
    private readonly HwndSource _source;
    private readonly Func<bool> _canReceive;
    private readonly Action<int, IntPtr, IntPtr> _mouseMessage;
    private readonly Action _mouseCaptureLost;
    private readonly Action _failedInput;
    private readonly Dictionary<uint, PointerTouchDevice> _contacts = new();
    private (int X, int Y, int Width, int Height)? _bounds;
    private bool _visible;
    private bool _failed;
    internal IntPtr Handle { get; private set; }
    public bool HasContacts => _contacts.Count != 0;
    public static bool IsNativeContact(TouchDevice device) => device is PointerTouchDevice;
    public bool OwnsContact(TouchDevice device) => _contacts.Values.Any(contact => ReferenceEquals(contact, device));

    public LegacyTouchInputSurface(Window owner, HwndSource source, Func<bool> canReceive,
        Action<int, IntPtr, IntPtr> mouseMessage, Action mouseCaptureLost, Action failedInput)
    {
        _owner = owner;
        _source = source;
        _canReceive = canReceive;
        _mouseMessage = mouseMessage;
        _mouseCaptureLost = mouseCaptureLost;
        _failedInput = failedInput;
        lock (ClassLock)
        {
            if (!_registered)
            {
                var wc = new WNDCLASSEX
                {
                    Size = (uint)Marshal.SizeOf<WNDCLASSEX>(), Procedure = Callback,
                    Instance = GetModuleHandle(null), ClassName = ClassName,
                    Background = GetStockObject(4) // BLACK_BRUSH; alpha 1 keeps the input area hit-testable.
                };
                if (RegisterClassEx(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                _registered = true;
            }
        }
        Handle = CreateWindowEx(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED,
            ClassName, "XCL2 legacy touch input", WS_POPUP, 0, 0, 1, 1,
            source.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        Windows.Add(Handle, this);
        if (!SetLayeredWindowAttributes(Handle, 0, 1, 2))
        {
            var error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error);
        }
    }

    public void Show(int x, int y, int width, int height)
    {
        if (Handle == IntPtr.Zero || _failed || width <= 0 || height <= 0) return;
        var bounds = (x, y, width, height);
        if (_visible && _bounds == bounds) return;
        // A moved/resized client area is a new coordinate frame, not a finger movement.
        if (_bounds.HasValue && _bounds != bounds) CancelContacts();
        if (SetWindowPos(Handle, new IntPtr(-1), x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW))
        {
            _visible = true;
            _bounds = bounds;
        }
        else StopOnFailure(new Win32Exception(Marshal.GetLastWin32Error()));
    }

    public void Hide()
    {
        CancelContacts();
        ReleaseMouse();
        if (Handle != IntPtr.Zero && _visible) ShowWindow(Handle, 0);
        _visible = false;
    }

    public bool CaptureMouse()
    {
        if (Handle == IntPtr.Zero) return false;
        SetCapture(Handle);
        return GetCapture() == Handle;
    }

    public void ReleaseMouse()
    {
        if (Handle != IntPtr.Zero && GetCapture() == Handle) ReleaseCapture();
    }

    public void CancelContacts()
    {
        var contacts = _contacts.Values.ToArray();
        _contacts.Clear();
        foreach (var contact in contacts) contact.Cancel();
    }

    private void Cancel(uint id)
    {
        if (_contacts.Remove(id, out var contact)) contact.Cancel();
    }

    private IntPtr ProcessMessage(int message, IntPtr wParam, IntPtr lParam)
    {
        // MA_NOACTIVATE / PA_NOACTIVATE: the game must keep keyboard focus.
        if (message is 0x0021 or 0x024B) return new IntPtr(3);
        if (message is 0x0020 or 0x0014) return new IntPtr(1); // keep game cursor; background already supplied
        if (message == 0x001F) { CancelContacts(); ReleaseMouse(); } // WM_CANCELMODE
        if (message == 0x0215) _mouseCaptureLost(); // WM_CAPTURECHANGED
        var id = unchecked((uint)wParam.ToInt64()) & 0xffff;
        if (message == WM_POINTERCAPTURECHANGED) { Cancel(id); return IntPtr.Zero; }
        if (message is WM_POINTERDOWN or WM_POINTERUPDATE or WM_POINTERUP or 0x0249 or 0x024A or 0x024E or 0x024F)
        {
            // Mouse-in-pointer is not enabled here, but preserve it if another component enabled it.
            if (GetPointerType(id, out var type) && type == PT_MOUSE)
                return DefWindowProc(Handle, message, wParam, lParam);
            // Unknown/failed/canceled touch must also be consumed, never promoted as a fallback.
            if (_failed || !_visible || !_canReceive() || !GetPointerInfo(id, out var info) ||
                info.Type is not (PT_TOUCH or PT_PEN) || (info.Flags & POINTER_FLAG_CANCELED) != 0)
            {
                Cancel(id);
                return IntPtr.Zero;
            }
            // Raw contact coordinates avoid prediction overshoot when the finger stops or lifts.
            var point = new Point(info.RawPixel.X, info.RawPixel.Y);
            if (message == WM_POINTERDOWN)
            {
                Cancel(id);
                var contact = new PointerTouchDevice(id, _owner, _source);
                _contacts.Add(id, contact);
                contact.Down(point);
            }
            else if (message == WM_POINTERUPDATE && _contacts.TryGetValue(id, out var moving))
            {
                if ((info.Flags & POINTER_FLAG_INCONTACT) != 0) moving.Move(point);
                else Cancel(id);
            }
            else if (message == WM_POINTERUP && _contacts.Remove(id, out var ending)) ending.Up(point);
            return IntPtr.Zero;
        }
        if (message is >= 0x0200 and <= 0x020E)
        {
            // Preserve origin/signature while still inside the native message callback. Only transform
            // coordinates; the overlay decides whether this is physical input or its own injection.
            var translated = lParam;
            if (message is not (0x020A or 0x020E))
            {
                var bits = lParam.ToInt64();
                var point = new POINT { X = unchecked((short)bits), Y = unchecked((short)(bits >> 16)) };
                if (!ClientToScreen(Handle, ref point) || !ScreenToClient(_source.Handle, ref point)) return IntPtr.Zero;
                translated = new IntPtr(unchecked((int)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16))));
            }
            _mouseMessage(message, wParam, translated);
            return IntPtr.Zero;
        }
        return DefWindowProc(Handle, message, wParam, lParam);
    }

    private static IntPtr NativeWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        if (!Windows.TryGetValue(hwnd, out var window)) return DefWindowProc(hwnd, message, wParam, lParam);
        try { return window.ProcessMessage(message, wParam, lParam); }
        catch (Exception ex)
        {
            // Never unwind through user32 or promote the failed pointer frame into a mouse jump.
            window.StopOnFailure(ex);
            return IntPtr.Zero;
        }
    }

    private void StopOnFailure(Exception error)
    {
        if (_failed) return;
        _failed = true;
        try { CancelContacts(); ReleaseMouse(); } catch { /* cleanup must not unwind through user32 */ }
        try { ErrorPresenter.LogTechnicalDetail($"[旧版原生触摸输入失败]\n{error}"); } catch { }
        try { _ = _owner.Dispatcher.BeginInvoke(_failedInput); } catch { /* dispatcher already closed */ }
    }

    public void Dispose()
    {
        Hide();
        var handle = Handle;
        if (handle == IntPtr.Zero) return;
        DestroyWindow(handle);
        Windows.Remove(handle);
        Handle = IntPtr.Zero;
    }

    private sealed class PointerTouchDevice(uint pointerId, Window owner, HwndSource source)
        : TouchDevice(unchecked((int)(0x40000000u | pointerId)))
    {
        private Point _screen;
        private TouchAction _action;

        public override TouchPoint GetTouchPoint(IInputElement? relativeTo)
        {
            var point = (relativeTo as Visual ?? owner).PointFromScreen(_screen);
            return new TouchPoint(this, point, new Rect(point, new Size(1, 1)), _action);
        }

        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement? relativeTo)
            => new() { GetTouchPoint(relativeTo) };

        public void Down(Point point)
        {
            _screen = point;
            _action = TouchAction.Down;
            SetActiveSource(source);
            Activate();
            ReportDown();
        }

        public void Move(Point point)
        {
            // Parallel digitizers report held fingers in every frame. Re-reporting stationary
            // controls needlessly runs WPF hit testing/capture synchronization for each finger.
            if (!IsActive || point == _screen) return;
            _screen = point;
            _action = TouchAction.Move;
            ReportMove();
        }

        public void Up(Point point)
        {
            if (!IsActive) return;
            _screen = point;
            _action = TouchAction.Up;
            try { ReportUp(); }
            finally { if (IsActive) Deactivate(); }
        }

        public void Cancel()
        {
            if (!IsActive) return;
            _action = TouchAction.Up;
            // No ReportUp: cancel/focus loss must release keys without synthesizing a tap/attack.
            Deactivate();
        }
    }

    private delegate IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct POINTER_INFO
    {
        public uint Type, Id, FrameId, Flags;
        public IntPtr SourceDevice, Target;
        public POINT Pixel, Himetric, RawPixel, RawHimetric;
        public uint Time, HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WNDCLASSEX
    {
        public uint Size, Style;
        public WindowProc? Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public IntPtr SmallIcon;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint color, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetPointerType(uint id, out uint type);
    [DllImport("user32.dll")] private static extern bool GetPointerInfo(uint id, out POINTER_INFO info);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
}
