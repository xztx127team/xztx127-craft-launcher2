using System.Windows;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>
/// 用户明确选中的窗口会话。以 PID + HWND 隔离，不登记 GameProcessManager，
/// 不获取进程关闭权限，也不在目标消失后改绑同进程的其他窗口。
/// </summary>
public static class WindowTouchOverlayService
{
    private sealed class Entry(KnownWindowInfo window)
    {
        public KnownWindowInfo Window { get; } = window;
        public TouchOverlayState State = TouchOverlayState.Starting;
        public string? Error;
        public volatile bool Allowed = true;
        public bool ClosingAfterFailure;
        public ITouchOverlayWindow? Overlay;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<(int Pid, IntPtr Handle), Entry> Entries = new();
    private static (int, IntPtr) Key(KnownWindowInfo window) => (window.Pid, window.Handle);

    public static event Action? Changed;
    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { /* 状态订阅不影响输入释放。 */ }
    }

    public static TouchOverlayStatus GetStatus(KnownWindowInfo window)
    {
        lock (Gate)
            return Entries.TryGetValue(Key(window), out var entry)
                ? new(entry.State, entry.Error) : new(TouchOverlayState.Disabled, null);
    }

    /// <summary>收起“所有窗口”列表后，用户仍可关闭已经开启的会话。</summary>
    public static IReadOnlyList<KnownWindowInfo> ManagedWindows
    {
        get { lock (Gate) return Entries.Values.Select(e => e.Window).ToArray(); }
    }

    public static bool HasActive
    {
        get { lock (Gate) return Entries.Values.Any(e => e.State is TouchOverlayState.Starting or TouchOverlayState.Enabled); }
    }

    public static string? UnavailableReason(KnownWindowInfo window)
    {
        if (window.Pid == Environment.ProcessId)
            return "请选择其他应用窗口；启动器自身窗口不需要附加触摸板。";
        return TouchGameWindow.BelongsTo(window.Handle, window.Pid)
            ? null : "此窗口已关闭，请刷新列表后重新选择。";
    }

    public static void Enable(KnownWindowInfo window, AppConfig config)
    {
        var entry = new Entry(window);
        var key = Key(window);
        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var previous) &&
                previous.State is TouchOverlayState.Starting or TouchOverlayState.Enabled) return;
            Entries[key] = entry;
        }
        RaiseChanged();

        void Start()
        {
            if (!entry.Allowed) return;
            lock (Gate)
                if (!Entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry)) return;
            try
            {
                if (UnavailableReason(window) is { } reason) throw new InvalidOperationException(reason);
                bool CanTakeOver() => entry.Allowed && TouchGameWindow.BelongsTo(window.Handle, window.Pid);
                var backend = TouchGameWindow.Detect(window.Handle);
                // gamePid=0 禁止现有游戏悬浮层按 PID 自动换绑；许可回调负责精确 HWND 的存活检查。
                var supportsPointer = !WindowsCompatibilityService.Current.IsWindows
                    || WindowsCompatibilityService.Current.SupportsPointerInput;
                ITouchOverlayWindow overlay = supportsPointer && backend is (TouchGameBackend.Glfw or TouchGameBackend.Lwjgl2)
                    ? new TouchOverlayWindow(window.Handle, config.TouchOverlayButtonScale,
                        config.TouchOverlayOpacityPercent, config.TouchOverlayLookSensitivity, 0, CanTakeOver,
                        forceTouchpad: true)
                    : new OriginalTouchOverlayWindow(window.Handle, config.TouchOverlayButtonScale,
                        config.TouchOverlayOpacityPercent, config.TouchOverlayLookSensitivity, 0, CanTakeOver,
                        forceTouchpad: true, desktopWindow: backend == TouchGameBackend.Unknown);
                entry.Overlay = overlay;
                overlay.Closed += (_, _) =>
                {
                    entry.Allowed = false;
                    lock (Gate)
                    {
                        if (!entry.ClosingAfterFailure && Entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                            Entries.Remove(key);
                    }
                    RaiseChanged();
                };
                if (!CanTakeOver()) { overlay.ShutdownOverlay(); return; }
                overlay.Show();
                if (!CanTakeOver()) { overlay.ShutdownOverlay(); return; }
                lock (Gate)
                {
                    if (!Entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry)) return;
                    entry.State = TouchOverlayState.Enabled;
                    entry.Error = TouchRoutePolicy.NeedsRuntimeRawInputAdvice(backend, false, null)
                        ? TouchRoutePolicy.RuntimeRawInputAdvice : null;
                }
                RaiseChanged();
            }
            catch (Exception ex)
            {
                entry.Allowed = false;
                entry.ClosingAfterFailure = true;
                try { entry.Overlay?.ShutdownOverlay(); } catch { }
                lock (Gate)
                {
                    // 保留开启失败供重试；已取消或被替换的会话不能重新出现。
                    if (Entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                    {
                        entry.Overlay = null;
                        entry.State = TouchOverlayState.Failed;
                        entry.Error = ex.Message;
                    }
                }
                ErrorPresenter.LogTechnicalDetail($"[窗口触摸板开启失败] {ex}");
                RaiseChanged();
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            CancelStart(entry);
            return;
        }
        // 将启动排到 UI 队列，允许重复点击去重与尚未创建窗口时取消。
        try { _ = dispatcher.BeginInvoke(new Action(Start)); }
        catch (InvalidOperationException ex)
        {
            CancelStart(entry);
            ErrorPresenter.LogTechnicalDetail($"[窗口触摸板启动已取消] {ex}");
        }
    }

    private static void CancelStart(Entry entry)
    {
        lock (Gate)
        {
            var key = Key(entry.Window);
            if (Entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                Entries.Remove(key);
        }
        CloseEntry(entry);
        RaiseChanged();
    }

    public static void Detach(KnownWindowInfo window)
    {
        Entry? entry;
        lock (Gate) Entries.Remove(Key(window), out entry);
        if (entry != null) CloseEntry(entry);
        RaiseChanged();
    }

    private static void CloseEntry(Entry entry)
    {
        // 先撤销所有输入入口，再在所属 UI 线程释放捕获、钩子和按下状态。
        entry.Allowed = false;
        var overlay = entry.Overlay;
        if (overlay == null) return;
        void Close() { try { overlay.ShutdownOverlay(); } catch { } }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            try { if (!dispatcher.HasShutdownStarted) _ = dispatcher.BeginInvoke(new Action(Close)); }
            catch (InvalidOperationException) { /* Dispatcher 已退出；输入许可已撤销。 */ }
        }
        else Close();
    }

    public static void CloseAll()
    {
        Entry[] entries;
        lock (Gate)
        {
            entries = Entries.Values.ToArray();
            Entries.Clear();
        }
        foreach (var entry in entries) CloseEntry(entry);
        RaiseChanged();
    }
}
