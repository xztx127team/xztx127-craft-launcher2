using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace XCL2.App.Services;

/// <summary>Standalone Windows confirmations can be answered without restoring the launcher.</summary>
public static class NativeApprovalService
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxNative(IntPtr owner, string message, string title, uint flags);
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int count);

    public static bool ShouldUseNative
    {
        get
        {
            var main = Application.Current?.MainWindow;
            return ConfigService.Active?.Config.NativeBackgroundApprovals != false
                && (main == null || !main.IsVisible || !main.IsActive || main.WindowState == WindowState.Minimized);
        }
    }

    public static bool Confirm(string message, string title, Window? owner = null)
        => Ask(message, title, false, owner) == 6;

    public static int Ask(string message, string title, bool allowCancel, Window? owner = null)
    {
        var cancellation = AiAssistantService.RequestCancellation.Value;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() =>
            {
                var previous = AiAssistantService.RequestCancellation.Value;
                AiAssistantService.RequestCancellation.Value = cancellation;
                try { return Ask(message, title, allowCancel, owner); }
                finally { AiAssistantService.RequestCancellation.Value = previous; }
            });
        owner ??= Application.Current?.Windows.Cast<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible && w.WindowState != WindowState.Minimized);
        var hwnd = owner is { IsVisible: true } && owner.WindowState != WindowState.Minimized
            ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
        // Default No/Cancel. The message is foregrounded independently of the launcher.
        var flags = (allowCancel ? 0x3u | 0x200u : 0x4u | 0x100u) | 0x20u | 0x10000u;
        if (hwnd == IntPtr.Zero) flags |= 0x40000u | 0x2000u;
        if (cancellation.IsCancellationRequested) return allowCancel ? 2 : 7;
        IntPtr dialog = IntPtr.Zero;
        IntPtr hook = IntPtr.Zero;
        void Deny() { if (dialog != IntPtr.Zero) PostMessage(dialog, 0x0111 /* WM_COMMAND */, (IntPtr)(allowCancel ? 2 : 7), IntPtr.Zero); }
        HookProc capture = (code, window, data) =>
        {
            if (code == 5 /* HCBT_ACTIVATE */ && dialog == IntPtr.Zero)
            {
                var name = new System.Text.StringBuilder(64);
                if (GetClassName(window, name, name.Capacity) > 0 && name.ToString() == "#32770")
                {
                    dialog = window;
                    if (cancellation.IsCancellationRequested) Deny();
                }
            }
            return CallNextHookEx(hook, code, window, data);
        };
        // Capture this dialog, rather than searching by title (simultaneous approvals may
        // have the same title). Cancelling a turn selects No/Cancel, never authorizes work.
        if (cancellation.CanBeCanceled) hook = SetWindowsHookEx(5 /* WH_CBT */, capture, IntPtr.Zero, GetCurrentThreadId());
        using var registration = cancellation.Register(Deny);
        try
        {
            var result = MessageBoxNative(hwnd, message, title, flags);
            if (result == 0) ToastService.ShowWarning("无法显示 Windows 确认框，已取消本次操作。");
            return result;
        }
        finally
        {
            registration.Dispose();
            dialog = IntPtr.Zero;
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            GC.KeepAlive(capture);
        }
    }
}
