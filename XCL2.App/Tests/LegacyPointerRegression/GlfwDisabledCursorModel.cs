using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

/// <summary>
/// A test model of GLFW's traditional (raw mouse motion disabled) cursor consumer.
/// This is deliberately not GLFW or Minecraft: it verifies that received absolute
/// WM_MOUSEMOVE positions actually produce relative camera motion. Counting posted
/// messages alone cannot detect zero movement or a move canceled by a recenter echo.
/// </summary>
internal sealed class GlfwDisabledCursorModel : IDisposable
{
    private readonly IntPtr _window;
    private readonly HwndSource _source;
    private readonly HwndSourceHook _hook;
    private Point _last;
    public Vector CameraDelta { get; private set; }
    public int MoveMessages { get; private set; }
    public int NonzeroMoves { get; private set; }
    public Point Center { get; private set; }

    public GlfwDisabledCursorModel(Window window)
    {
        _window = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_window)!;
        _hook = OnMessage;
        _source.AddHook(_hook);
        Recenter();
    }

    public void ResetMeasurement()
    {
        CameraDelta = default;
        MoveMessages = NonzeroMoves = 0;
    }

    /// <summary>
    /// Models a poll/frame boundary: the backend updates its last cursor position
    /// before warping the native cursor. A later warp message must contribute zero.
    /// Call only after draining the input for the preceding frame.
    /// </summary>
    public void Recenter()
    {
        if (!GetClientRect(_window, out var rect)) throw new Win32Exception();
        Center = new Point((rect.Right - rect.Left) / 2, (rect.Bottom - rect.Top) / 2);
        _last = Center;
        var screen = new POINT { X = (int)Center.X, Y = (int)Center.Y };
        if (!ClientToScreen(_window, ref screen) || !SetCursorPos(screen.X, screen.Y))
            throw new Win32Exception();
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0200) return IntPtr.Zero;
        var bits = lParam.ToInt64();
        var point = new Point(unchecked((short)bits), unchecked((short)(bits >> 16)));
        var movement = point - _last;
        CameraDelta += movement;
        MoveMessages++;
        if (movement.LengthSquared != 0) NonzeroMoves++;
        _last = point;
        handled = true;
        return IntPtr.Zero;
    }

    public void Dispose() => _source.RemoveHook(_hook);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
}
