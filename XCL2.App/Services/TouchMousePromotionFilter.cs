using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>
/// 拦截当前旧版游戏覆盖区域及已捕获手势的触摸/笔合成鼠标事件，防止一根手指同时产生触摸和鼠标输入。
/// 菜单定位和视角移动由悬浮层发送带签名的输入；标题栏、其他窗口和隐藏后的游戏区域放行。
/// </summary>
internal static class TouchMousePromotionFilter
{
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    private static uint ProcessIdOf(IntPtr window) { GetWindowThreadProcessId(window, out var pid); return pid; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public UIntPtr dwExtraInfo;
    }
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private sealed record Target(IntPtr GameHwnd, IntPtr OverlayHwnd, Dispatcher Dispatcher, Action<ushort>? KeyDown, uint GamePid)
    {
        public LegacyTouchInputDelivery Delivery { get; } = new();
        public long MenuSamples, LookSamples, InjectedMoves, FailedInjections, HookMoves;
        public long WindowSignedMoves, ReceiptMoves, ForwardedMoves, RejectedMoves, IssuedLookMoves;
        public long NextDiagnosticAt = Environment.TickCount64 + 5000;
        public long LastLoggedSamples;
    }

    private static readonly object Sync = new();
    // 委托终身存活；回调只读取一个不可变快照，不等待 UI 锁。
    private static readonly LowLevelMouseProc Proc = HookCallback;
    private static IntPtr _hook;
    private static IntPtr _keyboardHook;
    private static readonly LowLevelMouseProc KeyboardProc = KeyboardHookCallback;
    private static readonly HashSet<uint> PhysicalKeys = new();
    private static Target? _target;
    private static Thread? _thread;
    private static Dispatcher? _dispatcher;
    private static bool _stopping;
    private static long _nextInstallAttemptAt;
    private static long _contactsProtectedUntil;

    // Cached installation state only: Windows may silently remove the hook. Never use this to authorize input.
    public static bool IsEnabled => Volatile.Read(ref _hook) != IntPtr.Zero && Volatile.Read(ref _target) != null;

    /// <summary>使用独立的消息线程安装钩子，避免启动器的布局、定时刷新拖住系统鼠标输入。</summary>
    public static void Enable(IntPtr gameHwnd, IntPtr overlayHwnd, Action<ushort>? keyDown = null)
    {
        if (gameHwnd == IntPtr.Zero || overlayHwnd == IntPtr.Zero) return;
        try
        {
            var gamePid = ProcessIdOf(gameHwnd);
            if (gamePid == 0) return;
            lock (Sync)
            {
                if (_stopping) return;
                var target = _target;
                if (target == null || target.GameHwnd != gameHwnd || target.OverlayHwnd != overlayHwnd)
                {
                    Volatile.Write(ref _contactsProtectedUntil, 0);
                    Volatile.Write(ref _target, new Target(gameHwnd, overlayHwnd, Dispatcher.CurrentDispatcher, keyDown, gamePid));
                }
                if (_thread != null || Environment.TickCount64 < _nextInstallAttemptAt) return;
                _nextInstallAttemptAt = Environment.TickCount64 + 2000;

                var thread = new Thread(RunHook)
                {
                    IsBackground = true,
                    Name = "XCL2 Touch Mouse Filter"
                };
                thread.SetApartmentState(ApartmentState.STA);
                _thread = thread;
                try { thread.Start(); }
                catch { _thread = null; throw; }
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogTechnicalDetail($"[触摸鼠标拦截器安装失败，不影响游戏运行]\n{ex}");
        }
    }

    /// <summary>触点捕获后移出窗口也须拦截；最后抬起后的短暂尾事件仍属于同一手势。</summary>
    public static void ProtectContacts(IntPtr overlayHwnd, bool active)
    {
        if (overlayHwnd == IntPtr.Zero || Volatile.Read(ref _target)?.OverlayHwnd != overlayHwnd) return;
        // 使用可续期租约，触点取消/驱动漏报抬起也不会留下永久保护。
        Volatile.Write(ref _contactsProtectedUntil, Environment.TickCount64 + (active ? 1500 : 250));
    }

    public static bool RegisterLook(IntPtr game, ulong signature)
    {
        var target = Volatile.Read(ref _target);
        if (target == null || !IsCurrentTarget(target, game)) return false;
        target.Delivery.BeginLook(signature, Environment.TickCount64);
        Interlocked.Increment(ref target.IssuedLookMoves);
        return true;
    }

    public static void CancelLook(IntPtr game, ulong signature)
    {
        var target = Volatile.Read(ref _target);
        if (target?.GameHwnd == game) target.Delivery.CancelLook(signature);
    }

    private static bool IsCurrentTarget(Target? target, IntPtr game)
        => target != null && game != IntPtr.Zero && target.GameHwnd == game && target.GamePid != 0 &&
           ProcessIdOf(game) == target.GamePid && GetForegroundWindow() == game && IsWindowVisible(target.OverlayHwnd);

    public static LegacyMouseMessageKind ClassifyMessage(IntPtr game, IntPtr overlay, int message,
        int x, int y, uint time, ulong extra, uint device, uint origin)
    {
        var target = Volatile.Read(ref _target);
        // The collapsed overlay still has a physical "show" button. It needs ordinary hardware
        // classification after Disable(), while unsigned or old camera packets remain rejected.
        if (target == null || target.OverlayHwnd != overlay || !IsCurrentTarget(target, game))
        {
            if (target != null && target.GameHwnd == game && target.OverlayHwnd == overlay) target.Delivery.Clear();
            return LegacyTouchMousePolicy.Classify(extra, device, origin);
        }
        var kind = target.Delivery.Classify(message, x, y, time, extra, device, origin, Environment.TickCount64, out var recovered);
        RecordMessageClassification(game, message, kind, recovered, LegacyTouchMousePolicy.IsInjected(extra));
        return kind;
    }

    public static bool ContactsProtected(IntPtr overlay)
        => Volatile.Read(ref _target)?.OverlayHwnd == overlay &&
           Environment.TickCount64 < Volatile.Read(ref _contactsProtectedUntil);

    public static void ClearReceipts(IntPtr game)
    {
        var target = Volatile.Read(ref _target);
        if (target?.GameHwnd == game) target.Delivery.Clear();
    }

    public static void RecordPointerSample(IntPtr game, bool menu)
    {
        var target = Volatile.Read(ref _target);
        if (target?.GameHwnd != game) return;
        if (menu) Interlocked.Increment(ref target.MenuSamples);
        else Interlocked.Increment(ref target.LookSamples);
    }

    public static void RecordLookInjection(IntPtr game, bool success)
    {
        var target = Volatile.Read(ref _target);
        if (target?.GameHwnd != game) return;
        if (success) Interlocked.Increment(ref target.InjectedMoves);
        else Interlocked.Increment(ref target.FailedInjections);
    }

    public static void RecordMessageClassification(IntPtr game, int message, LegacyMouseMessageKind kind,
        bool receipt, bool signed)
    {
        var target = Volatile.Read(ref _target);
        if (target?.GameHwnd != game || message != 0x0200) return;
        if (signed) Interlocked.Increment(ref target.WindowSignedMoves);
        if (receipt && !signed) Interlocked.Increment(ref target.ReceiptMoves);
        if (kind == LegacyMouseMessageKind.Rejected) Interlocked.Increment(ref target.RejectedMoves);
    }

    public static void RecordForwarded(IntPtr game, int message)
    {
        var target = Volatile.Read(ref _target);
        if (target?.GameHwnd == game && message == 0x0200) Interlocked.Increment(ref target.ForwardedMoves);
    }

    /// <summary>仅在实际操作后每五秒汇总计数，不记录坐标、按键内容或逐帧日志。</summary>
    public static void LogDiagnostics(IntPtr game, bool menuMode)
    {
        var target = Volatile.Read(ref _target);
        var now = Environment.TickCount64;
        if (target?.GameHwnd != game || now < target.NextDiagnosticAt) return;
        target.NextDiagnosticAt = now + 5000;
        var menu = Interlocked.Read(ref target.MenuSamples);
        var look = Interlocked.Read(ref target.LookSamples);
        if (menu + look == target.LastLoggedSamples) return;
        target.LastLoggedSamples = menu + look;
        LauncherLogService.AppendLine($"[旧版触屏诊断] 模式={(menuMode ? "菜单" : "视角")}，菜单样本={menu}，视角样本={look}，" +
            $"注入成功/失败={Interlocked.Read(ref target.InjectedMoves)}/{Interlocked.Read(ref target.FailedInjections)}，" +
            $"签名钩子={Interlocked.Read(ref target.HookMoves)}，窗口签名={Interlocked.Read(ref target.WindowSignedMoves)}，" +
            $"凭证恢复={Interlocked.Read(ref target.ReceiptMoves)}，转发={Interlocked.Read(ref target.ForwardedMoves)}，" +
            $"视角登记={Interlocked.Read(ref target.IssuedLookMoves)}，" +
            $"拦截={Interlocked.Read(ref target.RejectedMoves)}");
    }

    private static void RunHook()
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            lock (Sync)
            {
                _dispatcher = dispatcher;
                if (_stopping) return;
            }

            Volatile.Write(ref _hook, SetWindowsHookEx(WH_MOUSE_LL, Proc, IntPtr.Zero, 0));
            if (Volatile.Read(ref _hook) == IntPtr.Zero)
            {
                LauncherLogService.AppendLine("[触屏模式] 触摸合成鼠标拦截器安装失败，视角可能会乱转。");
                return;
            }
            PhysicalKeys.Clear();
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, KeyboardProc, IntPtr.Zero, 0);
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogTechnicalDetail($"[触摸鼠标拦截器异常，不影响游戏运行]\n{ex}");
        }
        finally
        {
            var hook = Volatile.Read(ref _hook);
            if (hook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(hook); } catch { /* 线程结束时系统也会清理钩子 */ }
            }
            if (_keyboardHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_keyboardHook); } catch { }
                _keyboardHook = IntPtr.Zero;
            }
            PhysicalKeys.Clear();
            lock (Sync)
            {
                Volatile.Write(ref _hook, IntPtr.Zero);
                _dispatcher = null;
                _thread = null;
            }
        }
    }

    /// <summary>暂停指定悬浮层的拦截，不反复装卸钩子；不传句柄时结束专用线程。</summary>
    public static void Disable(IntPtr overlayHwnd = default)
    {
        Dispatcher? dispatcher = null;
        lock (Sync)
        {
            if (overlayHwnd != IntPtr.Zero && overlayHwnd != _target?.OverlayHwnd) return;
            _target?.Delivery.Clear();
            Volatile.Write(ref _target, null);
            Volatile.Write(ref _contactsProtectedUntil, 0);
            if (overlayHwnd == IntPtr.Zero)
            {
                _stopping = true;
                dispatcher = _dispatcher;
            }
        }
        try { dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send); }
        catch { /* 应用退出时消息线程可能已经结束 */ }
    }

    private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((ulong)data.dwExtraInfo != TouchInputInjector.InjectedSignature)
                {
                    var message = wParam.ToInt32();
                    if (message is 0x0101 or 0x0105) PhysicalKeys.Remove(data.vkCode);
                    else if ((message is 0x0100 or 0x0104) && PhysicalKeys.Add(data.vkCode))
                    {
                        var target = Volatile.Read(ref _target);
                        if (target?.KeyDown != null && GetForegroundWindow() == target.GameHwnd)
                            _ = target.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (ReferenceEquals(target, Volatile.Read(ref _target)) && GetForegroundWindow() == target.GameHwnd)
                                    target.KeyDown((ushort)data.vkCode);
                            }), DispatcherPriority.Input);
                    }
                }
            }
            catch { /* 键盘观察不拦截任何输入。 */ }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var extra = (ulong)data.dwExtraInfo;
                var target = Volatile.Read(ref _target);
                if (target != null && LegacyTouchMousePolicy.IsInjected(extra) &&
                    GetForegroundWindow() == target.GameHwnd && IsWindowVisible(target.OverlayHwnd))
                {
                    var message = wParam.ToInt32();
                    // Only preserve origin metadata here. Delivery happens once at the receiving
                    // window, even if Windows silently removes this hook after a timeout.
                    target.Delivery.ObserveHook(message, data.pt.x, data.pt.y, data.time, extra, Environment.TickCount64);
                    if (message == 0x0200) Interlocked.Increment(ref target.HookMoves);
                }
                if (target != null && LegacyTouchMousePolicy.IsTouch(extra))
                {
                    // WindowFromPoint 会受透明分层窗口、鼠标抓取和游戏 ClipCursor 影响。
                    // 此覆盖层只覆盖客户区，按几何范围判断才不会漏掉虚拟键上的落指。
                    var inside = GetWindowRect(target.OverlayHwnd, out var bounds) &&
                        data.pt.x >= bounds.Left && data.pt.x < bounds.Right &&
                        data.pt.y >= bounds.Top && data.pt.y < bounds.Bottom;
                    if (LegacyTouchMousePolicy.ShouldSuppressPromotion(extra,
                        GetForegroundWindow() == target.GameHwnd, IsWindowVisible(target.OverlayHwnd),
                        inside, Environment.TickCount64 < Volatile.Read(ref _contactsProtectedUntil)))
                        return (IntPtr)1;
                }
            }
            catch { /* 输入钩子发生异常时放行 */ }
        }
        return CallNextHookEx(Volatile.Read(ref _hook), nCode, wParam, lParam);
    }
}
