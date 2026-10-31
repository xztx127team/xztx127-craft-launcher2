using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using XCL2.App.Services;

internal static class LegacyGameCursorProbeCases
{
    public static async Task Run(Window foregroundAfter, Action<bool, string> check)
    {
        // A native GLFW-named test window exercises production backend detection.
        // Its WM_SETCURSOR handler is a controlled model, not a loaded GLFW library.
        using var native = new CursorWindow();
        var probe = new LegacyGameCursorProbe();
        var previousCursor = GetCursor();
        try
        {
            native.ShowAndActivate();
            await Task.Delay(100);
            check(GetForegroundWindow() == native.Handle, "cursor probe test window is foreground");
            check(GetCursorPos(out var before), "read cursor coordinates before mode refresh");
            var movesBefore = native.MouseMoves;
            native.Gameplay = true;
            var requestsBefore = native.CursorRequests;
            check(probe.TryRefresh(native.Handle), "foreground GLFW gameplay cursor refresh succeeds");
            check(native.CursorRequests == requestsBefore + 1 && GetCursor() == IntPtr.Zero,
                "probe lets the game choose its hidden gameplay cursor");
            check(native.LastHitTest == 1 && native.LastMouseMessage == 0x0200,
                "probe asks for the current client cursor using WM_SETCURSOR");
            check(native.MouseMoves == movesBefore && GetCursorPos(out var after) &&
                after.X == before.X && after.Y == before.Y,
                "cursor refresh neither posts WM_MOUSEMOVE nor changes pointer coordinates");
            requestsBefore = native.CursorRequests;
            check(!probe.TryRefresh(native.Handle) && native.CursorRequests == requestsBefore,
                "successive cursor samples are throttled without another game message");

            native.Gameplay = false;
            probe.Reset();
            check(probe.TryRefresh(native.Handle), "menu cursor refresh succeeds after reset");
            check(native.CursorRequests == requestsBefore + 1 && GetCursor() == native.Arrow,
                "probe lets the game choose its visible menu arrow instead of forcing a mode");
            check(native.MouseMoves == movesBefore && GetCursorPos(out after) &&
                after.X == before.X && after.Y == before.Y,
                "menu cursor refresh also leaves camera coordinates untouched");

            foregroundAfter.Activate();
            await Task.Delay(100);
            var foreground = new WindowInteropHelper(foregroundAfter).Handle;
            check(GetForegroundWindow() == foreground, "restore test-game foreground before inactive probe");
            probe.Reset();
            requestsBefore = native.CursorRequests;
            check(!probe.TryRefresh(native.Handle) && native.CursorRequests == requestsBefore,
                "a background game receives no cursor refresh request");
            check(!probe.TryRefresh(foreground), "a non-GLFW foreground window cannot be probed");
            check(!probe.TryRefresh(IntPtr.Zero), "a missing game window cannot be probed");
        }
        finally
        {
            SetCursor(previousCursor);
            foregroundAfter.Activate();
        }
    }

    private sealed class CursorWindow : IDisposable
    {
        private const string ClassName = "GLFWXcl2CursorProbeRegression";
        private static readonly WindowProc Callback = Dispatch;
        private static readonly Dictionary<IntPtr, CursorWindow> Windows = new();
        private static bool _registered;
        public IntPtr Handle { get; private set; }
        public IntPtr Arrow { get; }
        public bool Gameplay { get; set; }
        public int CursorRequests { get; private set; }
        public int MouseMoves { get; private set; }
        public int LastHitTest { get; private set; }
        public int LastMouseMessage { get; private set; }

        public CursorWindow()
        {
            Arrow = LoadCursor(IntPtr.Zero, new IntPtr(32512));
            if (Arrow == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!_registered)
            {
                var wc = new WNDCLASSEX { Size = (uint)Marshal.SizeOf<WNDCLASSEX>(), Procedure = Callback,
                    Instance = GetModuleHandle(null), ClassName = ClassName };
                if (RegisterClassEx(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                _registered = true;
            }
            Handle = CreateWindowEx(0, ClassName, "GLFW cursor probe MODEL", 0x00CF0000,
                20, 20, 240, 140, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Windows.Add(Handle, this);
        }

        public void ShowAndActivate() { ShowWindow(Handle, 5); SetForegroundWindow(Handle); }

        private static IntPtr Dispatch(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
        {
            if (Windows.TryGetValue(hwnd, out var window))
            {
                if (message == 0x0020)
                {
                    window.CursorRequests++;
                    window.LastHitTest = unchecked((short)lParam.ToInt64());
                    window.LastMouseMessage = (int)((lParam.ToInt64() >> 16) & 0xffff);
                    SetCursor(window.Gameplay ? IntPtr.Zero : window.Arrow);
                    return new IntPtr(1);
                }
                if (message == 0x0200) window.MouseMoves++;
            }
            return DefWindowProc(hwnd, message, wParam, lParam);
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            DestroyWindow(Handle);
            Windows.Remove(Handle);
            Handle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint Size, Style;
        public WindowProc Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public IntPtr SmallIcon;
    }
    private delegate IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int show);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr GetCursor();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
}
