// Only the operating-system/UI boundary is replaced. The linked production service owns all session state.
namespace System.Windows
{
    public sealed class Application
    {
        public static Application? Current { get; set; }
        public TestDispatcher Dispatcher { get; } = new();
    }

    public sealed class TestDispatcher
    {
        private readonly Queue<Action> _pending = new();
        public bool HasShutdownStarted { get; set; }
        public bool HasThreadAccess { get; set; } = true;
        public bool CheckAccess() => HasThreadAccess;
        public bool ThrowOnNextBeginInvoke { get; set; }
        public Action? BeforeNextBeginInvoke { get; set; }
        public object BeginInvoke(Action callback)
        {
            var before = BeforeNextBeginInvoke;
            BeforeNextBeginInvoke = null;
            var shouldThrow = ThrowOnNextBeginInvoke;
            ThrowOnNextBeginInvoke = false;
            before?.Invoke();
            if (shouldThrow) throw new InvalidOperationException("Synthetic dispatcher shutdown race");
            _pending.Enqueue(callback);
            return callback;
        }
        public void Drain()
        {
            var budget = 100;
            while (_pending.TryDequeue(out var next))
            {
                if (--budget == 0) throw new InvalidOperationException("Dispatcher did not settle.");
                next();
            }
        }
    }
}

namespace XCL2.App.Models
{
    public sealed partial class AppConfig
    {
        public double TouchOverlayButtonScale { get; set; } = 1;
        public double TouchOverlayOpacityPercent { get; set; } = 85;
        public double TouchOverlayLookSensitivity { get; set; } = 1.4;
    }
}

namespace XCL2.App.Services
{
    public enum TouchOverlayState { Disabled, Starting, Enabled, Failed }
    public readonly record struct TouchOverlayStatus(TouchOverlayState State, string? Error);
    public sealed record KnownWindowInfo(IntPtr Handle, int Pid, string Title, string ProcessName, bool IsForeground);

    internal static class TouchGameWindow
    {
        public static readonly Dictionary<IntPtr, (int Pid, TouchGameBackend Backend)> NativeWindows = new();
        public static bool BelongsTo(IntPtr hwnd, int pid)
            => hwnd != IntPtr.Zero && pid > 0 && NativeWindows.TryGetValue(hwnd, out var window) && window.Pid == pid;
        public static TouchGameBackend Detect(IntPtr hwnd)
            => NativeWindows.TryGetValue(hwnd, out var window) ? window.Backend : TouchGameBackend.Unknown;
    }

    public static class ErrorPresenter
    {
        public static readonly List<string> Errors = new();
        public static void LogTechnicalDetail(string message) => Errors.Add(message);
    }
}

namespace XCL2.App.Views
{
    internal abstract class TestOverlay : ITouchOverlayWindow
    {
        public static readonly List<TestOverlay> Created = new();
        public static Action<TestOverlay>? DuringNextShow;
        public readonly IntPtr Target;
        public readonly int RebindPid;
        public readonly bool ForceTouchpad;
        public readonly bool DesktopWindow;
        public readonly Func<bool> CanTakeOver;
        public int ShowCalls, ShutdownCalls;
        public bool DeferClosed;
        private bool _closed;
        public event EventHandler? Closed;

        protected TestOverlay(IntPtr target, int pid, Func<bool> canTakeOver, bool forceTouchpad, bool desktopWindow)
        {
            Target = target;
            RebindPid = pid;
            CanTakeOver = canTakeOver;
            ForceTouchpad = forceTouchpad;
            DesktopWindow = desktopWindow;
            Created.Add(this);
        }
        public void Show()
        {
            ShowCalls++;
            var action = DuringNextShow;
            DuringNextShow = null;
            action?.Invoke(this);
        }
        public void ShutdownOverlay()
        {
            ShutdownCalls++;
            if (!DeferClosed) CompleteClose();
        }
        public void CompleteClose()
        {
            if (_closed) return;
            _closed = true;
            Closed?.Invoke(this, EventArgs.Empty);
        }
        // Simulates the existing overlay's follow timer without enumerating any replacement HWND.
        public void PollTarget() { if (!CanTakeOver()) ShutdownOverlay(); }
    }

    internal sealed class TouchOverlayWindow(IntPtr gameHwnd, double buttonScale, double opacityPercent,
        double sensitivity, int gamePid, Func<bool> canTakeOver, bool forceTouchpad = false)
        : TestOverlay(gameHwnd, gamePid, canTakeOver, forceTouchpad, desktopWindow: false) { }

    internal sealed class OriginalTouchOverlayWindow(IntPtr gameHwnd, double buttonScale, double opacityPercent,
        double sensitivity, int gamePid, Func<bool> canTakeOver, bool forceTouchpad = false, bool desktopWindow = false)
        : TestOverlay(gameHwnd, gamePid, canTakeOver, forceTouchpad, desktopWindow) { }
}
