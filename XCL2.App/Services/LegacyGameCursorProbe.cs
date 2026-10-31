using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>
/// Ask the foreground GLFW window to refresh its own cursor image before observing it.
/// The input overlay receives WM_SETCURSOR instead of the game, so the global cursor can
/// retain an earlier image. GLFW's client-area handler only applies its existing cursor
/// mode; this does not change that mode, move the pointer, click, or activate a window.
/// </summary>
internal sealed class LegacyGameCursorProbe
{
    private const uint WM_SETCURSOR = 0x0020;
    private const int HTCLIENT = 1, WM_MOUSEMOVE = 0x0200;
    private const uint SMTO_BLOCK = 0x0001, SMTO_ABORTIFHUNG = 0x0002, SMTO_ERRORONEXIT = 0x0020;
    private const uint TimeoutMilliseconds = 20;
    private const long RefreshIntervalMilliseconds = 100, FailureCooldownMilliseconds = 1000;
    private IntPtr _window;
    private long _nextAttemptAt;

    /// <returns>
    /// True only when this call completed a refresh for the same foreground game process.
    /// A throttled, unsupported, failed or stale request returns false and is not fresh evidence.
    /// </returns>
    public bool TryRefresh(IntPtr game)
    {
        if (game == IntPtr.Zero || GetForegroundWindow() != game) return false;
        var now = Environment.TickCount64;
        if (_window != game)
        {
            _window = game;
            _nextAttemptAt = 0;
        }
        if (now < _nextAttemptAt) return false;
        _nextAttemptAt = now + RefreshIntervalMilliseconds;
        // WM_SETCURSOR is intentionally limited to the backend whose handler is known.
        // SDL and LWJGL2 retain their existing passive cursor detection.
        if (TouchGameWindow.Detect(game) != TouchGameBackend.Glfw ||
            GetWindowThreadProcessId(game, out var pid) == 0 || pid == 0 ||
            GetForegroundWindow() != game) return false;

        var completed = SendMessageTimeout(game, WM_SETCURSOR, game,
            new IntPtr(HTCLIENT | (WM_MOUSEMOVE << 16)),
            SMTO_BLOCK | SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT, TimeoutMilliseconds, out var handled) != IntPtr.Zero;
        // BLOCK prevents the bounded cross-process call from reentering the overlay's input
        // handlers. A hung/exiting game cannot block every mode sample or establish evidence.
        if (!completed || handled == UIntPtr.Zero)
        {
            _nextAttemptAt = Environment.TickCount64 + FailureCooldownMilliseconds;
            return false;
        }
        return GetForegroundWindow() == game &&
            GetWindowThreadProcessId(game, out var currentPid) != 0 && currentPid == pid;
    }

    public void Reset()
    {
        _window = IntPtr.Zero;
        _nextAttemptAt = 0;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out UIntPtr result);
}
