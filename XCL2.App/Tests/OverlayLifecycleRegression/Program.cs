using System.Windows;
using System.Windows.Threading;
using XCL2.App.Views;

internal static class Program
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var host = new MainWindow(); OverlayDialogService.Register(host);
        var escape = new Dialog(); var escTask = OverlayDialogService.ShowModalAsync(escape);
        Check((escTask.CreationOptions & TaskCreationOptions.RunContinuationsAsynchronously) != 0, "Modal completions never inline caller continuations");
        OverlayDialogService.RequestDismissTopByEscape();
        Check(escTask.IsCompleted && escape.Notifications == 1 && escape.Disposals == 1, "Esc notifies and disposes once");
        escape.Close(); Check(escape.Notifications == 1, "Late close cannot notify twice");
        var background = new Dialog(); var bgTask = OverlayDialogService.ShowModalAsync(background, dismissOnBackgroundClick: true);
        OverlayDialogService.RequestDismissTopByBackgroundClick();
        Check(bgTask.IsCompleted && background.Disposals == 1, "Background close cleans resources");
        var guarded = new GuardedDialog();
        var guardedTask = OverlayDialogService.ShowModalAsync(guarded, dismissOnBackgroundClick: true);
        Check(OverlayDialogService.TopHandlesDismissal, "A guarded top overlay takes Esc before input revert");
        OverlayDialogService.RequestDismissTopByEscape();
        Check(guarded.DismissRequests == 1 && !guardedTask.IsCompleted, "Esc delegates to the guard without discarding the draft");
        OverlayDialogService.RequestDismissTopByBackgroundClick();
        Check(guarded.DismissRequests == 2 && !guardedTask.IsCompleted, "Background dismissal uses the same guard");
        var confirmation = new Dialog(); var confirmationTask = OverlayDialogService.ShowModalAsync(confirmation);
        Check(!OverlayDialogService.TopHandlesDismissal, "A confirmation above a guarded editor owns its own Esc");
        OverlayDialogService.RequestDismissTopByEscape();
        Check(confirmationTask.IsCompleted && !guardedTask.IsCompleted && guarded.DismissRequests == 2,
            "Esc closes only the confirmation and does not recurse into the editor guard");
        guarded.Close();
        Check(guardedTask.IsCompleted && guarded.Notifications == 1 && guarded.Disposals == 1,
            "Explicit save or discard closes a guarded overlay once");
        var lockedGuard = new GuardedDialog();
        var lockedTask = OverlayDialogService.ShowModalAsync(lockedGuard, dismissOnEsc: false);
        OverlayDialogService.RequestDismissTopByEscape();
        OverlayDialogService.RequestDismissTopByBackgroundClick();
        Check(!lockedTask.IsCompleted && lockedGuard.DismissRequests == 0, "Dismissal flags still protect guarded overlays");
        lockedGuard.Close();
        var parent = new Dialog(); var parentTask = OverlayDialogService.ShowModalAsync(parent);
        var child = new Dialog(); var childTask = OverlayDialogService.ShowModalAsync(child);
        parent.Close();
        Check(parentTask.IsCompleted && !childTask.IsCompleted && parent.Notifications == 1, "Hidden parent closes without closing child");
        child.Close(); Check(childTask.IsCompleted && child.Disposals == 1, "Child closes normally after parent removal");
        var broken = new Dialog { ThrowOnDispose = true }; var brokenTask = OverlayDialogService.ShowModalAsync(broken);
        broken.OverlayClosed += (_, _) => throw new InvalidOperationException("test cleanup failure");
        broken.Close(); Check(brokenTask.IsCompleted && broken.Notifications == 1 && broken.Disposals == 1, "Cleanup exceptions cannot strand modal waiters");
        var waiting = new Dialog(); var waitingTask = OverlayDialogService.ShowModalAsync(waiting);
        host.DelayDismiss = true; waiting.Close();
        Check(!waitingTask.IsCompleted && OverlayDialogService.HasActiveOverlay, "Exiting overlay remains active until settled");
        var next = new GuardedDialog(); var nextTask = OverlayDialogService.ShowModalAsync(next);
        host.CloseHostForTest();
        Check(waitingTask.IsCompleted && nextTask.IsCompleted && waiting.Disposals == 1 && next.Disposals == 1,
            "Host exit completes stacked and exiting entries");
        host.CompletePendingDismiss();
        Check(waiting.Notifications == 1 && next.Notifications == 1 && !OverlayDialogService.HasActiveOverlay,
            "Late animation completion is harmless after host exit");
        app.Shutdown();
        Console.WriteLine("Overlay lifecycle checks passed (service linked; native animation still requires Windows manual checks).");
    }
    private class Dialog : OverlayDialogControl, IDisposable
    {
        public int Notifications, Disposals;
        public bool ThrowOnDispose;
        public Dialog() => OverlayClosed += (_, _) => Notifications++;
        public void Dispose() { Disposals++; if (ThrowOnDispose) throw new InvalidOperationException("test dispose failure"); }
    }
    private sealed class GuardedDialog : Dialog, IOverlayDialogDismissGuard
    {
        public int DismissRequests;
        public void RequestDismiss() => DismissRequests++;
    }
}

namespace XCL2.App.Views
{
    public sealed class MainWindow : Window
    {
        public XCL2.App.Services.ConfigService ConfigService { get; } = new();
        public bool DelayDismiss;
        private Action? _pending;
        public void OverlayRenderEntry(object content, bool animateIn, Action? onShown = null) => onShown?.Invoke();
        public void OverlayDismissEntry(object content, Action complete) { if (DelayDismiss) _pending = complete; else complete(); }
        public void OverlayHideRoot() { }
        public void CloseHostForTest() => OnClosed(EventArgs.Empty);
        public void CompletePendingDismiss() { var pending = _pending; _pending = null; pending?.Invoke(); }
    }
    internal sealed class PluginOverlayView(IOverlayDialog dialog) : IDisposable
    {
        public void ActivateExtensions() { }
        public void Dispose() { }
    }
}
namespace XCL2.App.Services
{
    public sealed class ConfigService { public Config Config { get; } = new(); public void Save() { } }
    public sealed class Config { public bool ShowEscCloseHint { get; set; } }
    public static class ErrorPresenter { public static void LogFallback(string operation, Exception ex) { } }
    public static class ToastService
    {
        public static void DismissActionPrompt(string key) { }
        public static void ShowActionPrompt(string text, string primaryText, Action primaryAction, string secondaryText,
            Action secondaryAction, string key, bool dismissible) { }
    }
}
namespace XCL2.App.Services.Plugins
{
    internal static class PluginUiRegistry { public static void Publish(IOverlayDialog dialog, bool open, bool? result = null) { } }
}
