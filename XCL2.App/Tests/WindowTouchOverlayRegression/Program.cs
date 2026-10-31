using System.Windows;
using XCL2.App.Models;
using XCL2.App.Services;
using XCL2.App.Views;

var assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
var app = new Application();
Application.Current = app;
var config = new AppConfig();
var externalPid = Environment.ProcessId == 42 ? 43 : 42;
KnownWindowInfo Window(int handle, int? pid = null, TouchGameBackend backend = TouchGameBackend.Unknown)
{
    var target = new KnownWindowInfo(new IntPtr(handle), pid ?? externalPid, $"Window {handle}", "sample", false);
    TouchGameWindow.NativeWindows[target.Handle] = (target.Pid, backend);
    return target;
}
void Reset()
{
    WindowTouchOverlayService.CloseAll();
    app.Dispatcher.Drain();
    TestOverlay.Created.Clear();
    TestOverlay.DuringNextShow = null;
    TouchGameWindow.NativeWindows.Clear();
}
TouchOverlayState State(KnownWindowInfo window) => WindowTouchOverlayService.GetStatus(window).State;
void Enable(KnownWindowInfo window)
{
    WindowTouchOverlayService.Enable(window, config);
    app.Dispatcher.Drain();
}

var a = Window(101);
var b = Window(102);
WindowTouchOverlayService.Enable(a, config);
WindowTouchOverlayService.Enable(a, config);
Check(State(a) == TouchOverlayState.Starting && WindowTouchOverlayService.HasActive,
    "Queued startup must expose Starting and count as an active session.");
app.Dispatcher.Drain();
Check(State(a) == TouchOverlayState.Enabled && TestOverlay.Created.Count == 1,
    "Repeated startup requests for the same PID/HWND must create only one overlay.");
Enable(a);
Check(TestOverlay.Created.Count == 1, "Enabled targets must not be duplicated.");
Enable(b);
Check(State(a) == TouchOverlayState.Enabled && State(b) == TouchOverlayState.Enabled,
    "Two windows in one process must retain independent sessions.");
Check(TestOverlay.Created.All(w => w.RebindPid == 0 && w.ForceTouchpad && w.DesktopWindow),
    "Unknown windows must use visible desktop pointer controls without PID-based rebinding.");
Check(WindowTouchOverlayService.ManagedWindows.Count == 2,
    "The manager can retain both sessions after the discovery list is hidden.");
var first = TestOverlay.Created[0];
WindowTouchOverlayService.Detach(a);
Check(State(a) == TouchOverlayState.Disabled && State(b) == TouchOverlayState.Enabled,
    "Closing one window's overlay must not affect another window in the same process.");
Check(!first.CanTakeOver() && first.ShutdownCalls == 1,
    "Detach must revoke input before closing the overlay.");
WindowTouchOverlayService.Detach(a);
Check(first.ShutdownCalls == 1, "Detach must be idempotent.");

Reset();
a = Window(201);
WindowTouchOverlayService.Enable(a, config);
WindowTouchOverlayService.Detach(a);
app.Dispatcher.Drain();
Check(State(a) == TouchOverlayState.Disabled && TestOverlay.Created.Count == 0,
    "Canceling a queued startup must prevent any overlay from being constructed.");
WindowTouchOverlayService.Enable(a, config);
WindowTouchOverlayService.Detach(a);
WindowTouchOverlayService.Enable(a, config);
app.Dispatcher.Drain();
Check(State(a) == TouchOverlayState.Enabled && TestOverlay.Created.Count == 1,
    "A canceled startup callback must not replace or remove a newer request.");

Reset();
a = Window(301);
TestOverlay.DuringNextShow = _ => throw new InvalidOperationException("Synthetic Show failure");
Enable(a);
Check(State(a) == TouchOverlayState.Failed && !WindowTouchOverlayService.HasActive,
    "Show failure must release input and expose a retryable failure.");
Check(WindowTouchOverlayService.GetStatus(a).Error == "Synthetic Show failure" &&
    !TestOverlay.Created[0].CanTakeOver() && TestOverlay.Created[0].ShutdownCalls == 1,
    "Failed startup must preserve its cause and shut down partial UI.");
Enable(a);
Check(State(a) == TouchOverlayState.Enabled && TestOverlay.Created.Count == 2,
    "A failed target must be retryable without reopening the manager.");

Reset();
a = Window(401);
b = Window(402);
Enable(a);
Enable(b);
first = TestOverlay.Created[0];
TouchGameWindow.NativeWindows.Remove(a.Handle);
Check(!first.CanTakeOver(), "Destroyed HWND must immediately lose its input permission.");
first.PollTarget();
Check(State(a) == TouchOverlayState.Disabled && State(b) == TouchOverlayState.Enabled,
    "The follow timer must remove the destroyed window session, preserving its sibling.");
Check(first.Target == a.Handle && first.RebindPid == 0,
    "A surviving sibling HWND must never become the destroyed session's target.");

Reset();
a = Window(501);
Enable(a);
first = TestOverlay.Created[0];
first.DeferClosed = true;
WindowTouchOverlayService.Detach(a);
Enable(a);
first.CompleteClose();
Check(State(a) == TouchOverlayState.Enabled && TestOverlay.Created[1].CanTakeOver(),
    "Delayed Closed from an old overlay must not remove a replacement session.");
TouchGameWindow.NativeWindows[a.Handle] = (externalPid + 1, TouchGameBackend.Unknown);
Check(!TestOverlay.Created[1].CanTakeOver(), "A reused HWND belonging to a new PID must reject input.");
TestOverlay.Created[1].PollTarget();
Check(State(a) == TouchOverlayState.Disabled, "HWND ownership changes must remove the stale session.");

Reset();
a = Window(601);
b = Window(602);
Enable(a);
WindowTouchOverlayService.Enable(b, config);
WindowTouchOverlayService.CloseAll();
app.Dispatcher.Drain();
Check(!WindowTouchOverlayService.HasActive && WindowTouchOverlayService.ManagedWindows.Count == 0,
    "CloseAll must clear running and queued sessions.");
Check(TestOverlay.Created.Count == 1 && !TestOverlay.Created[0].CanTakeOver(),
    "CloseAll must revoke existing input and cancel pending construction.");
WindowTouchOverlayService.CloseAll();
Check(TestOverlay.Created[0].ShutdownCalls == 1, "Repeated global cleanup must be harmless.");

Reset();
a = Window(701, Environment.ProcessId);
Check(WindowTouchOverlayService.UnavailableReason(a) != null, "Launcher-owned windows must be unavailable.");
Enable(a);
Check(State(a) == TouchOverlayState.Failed && TestOverlay.Created.Count == 0,
    "The service must reject launcher windows even if called without the manager's guard.");
a = Window(702);
TouchGameWindow.NativeWindows.Remove(a.Handle);
Enable(a);
Check(State(a) == TouchOverlayState.Failed && TestOverlay.Created.Count == 0,
    "A target closed between discovery and activation must fail without creating UI.");

Reset();
a = Window(801, backend: TouchGameBackend.Glfw);
Enable(a);
Check(TestOverlay.Created.Single() is TouchOverlayWindow &&
    WindowTouchOverlayService.GetStatus(a).Error == TouchRoutePolicy.RuntimeRawInputAdvice,
    "Explicit GLFW targets must preserve the legacy input route and runtime advice.");
b = Window(802, backend: TouchGameBackend.Sdl);
Enable(b);
Check(TestOverlay.Created.Last() is OriginalTouchOverlayWindow { DesktopWindow: false, ForceTouchpad: true },
    "SDL targets must preserve the modern route without desktop-only behavior.");

Reset();
a = Window(901);
TestOverlay.DuringNextShow = _ =>
{
    WindowTouchOverlayService.Detach(a);
    throw new InvalidOperationException("Failure after cancel");
};
Enable(a);
Check(State(a) == TouchOverlayState.Disabled, "A failure after cancellation must not resurrect a failed entry.");

Reset();
a = Window(1001);
TestOverlay.DuringNextShow = _ => TouchGameWindow.NativeWindows.Remove(a.Handle);
Enable(a);
Check(State(a) == TouchOverlayState.Disabled && !WindowTouchOverlayService.HasActive,
    "A target disappearing during Show must never be published as Enabled.");

Reset();
a = Window(1101);
Enable(a);
first = TestOverlay.Created.Single();
app.Dispatcher.HasThreadAccess = false;
WindowTouchOverlayService.Detach(a);
Check(State(a) == TouchOverlayState.Disabled && !first.CanTakeOver() && first.ShutdownCalls == 0,
    "A background detach must revoke input immediately while deferring UI teardown.");
app.Dispatcher.HasThreadAccess = true;
app.Dispatcher.Drain();
Check(first.ShutdownCalls == 1, "Deferred UI teardown must close exactly the detached overlay.");

Reset();
a = Window(1201);
app.Dispatcher.HasShutdownStarted = true;
WindowTouchOverlayService.Enable(a, config);
Check(State(a) == TouchOverlayState.Disabled && !WindowTouchOverlayService.HasActive,
    "Startup during dispatcher shutdown must not leave a pending session.");
app.Dispatcher.HasShutdownStarted = false;

Reset();
a = Window(1301);
app.Dispatcher.ThrowOnNextBeginInvoke = true;
WindowTouchOverlayService.Enable(a, config);
app.Dispatcher.Drain();
Check(State(a) == TouchOverlayState.Disabled && !WindowTouchOverlayService.HasActive && TestOverlay.Created.Count == 0,
    "A BeginInvoke shutdown race must cancel startup instead of leaving Starting or throwing to the caller.");

Reset();
a = Window(1401);
app.Dispatcher.ThrowOnNextBeginInvoke = true;
app.Dispatcher.BeforeNextBeginInvoke = () =>
{
    WindowTouchOverlayService.Detach(a);
    WindowTouchOverlayService.Enable(a, config);
};
WindowTouchOverlayService.Enable(a, config);
Check(State(a) == TouchOverlayState.Starting && WindowTouchOverlayService.ManagedWindows.Count == 1,
    "Canceling an old failed dispatch must preserve the newer queued session for the same PID/HWND.");
app.Dispatcher.Drain();
Check(State(a) == TouchOverlayState.Enabled && TestOverlay.Created.Count == 1 && TestOverlay.Created[0].CanTakeOver(),
    "The replacement request must still start normally after old-request dispatch failure.");

Reset();
a = Window(1501);
Enable(a);
first = TestOverlay.Created.Single();
app.Dispatcher.HasThreadAccess = false;
app.Dispatcher.ThrowOnNextBeginInvoke = true;
WindowTouchOverlayService.Detach(a);
Check(State(a) == TouchOverlayState.Disabled && !first.CanTakeOver(),
    "A teardown dispatch failure must still revoke permission and remove the session without throwing.");
app.Dispatcher.HasThreadAccess = true;
first.PollTarget();

Reset();
Console.WriteLine($"PASS: {assertions} window touch overlay lifecycle assertions (production service linked; native touch/UI still requires Windows validation).");
