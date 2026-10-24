using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>Old LWJGL/GLFW bridge: menu coordinates and relative camera motion are separate transports.</summary>
internal sealed class LegacyTouchInputBridge(IntPtr game, LegacyTouchWorldEvidence worldEvidence)
{
    private readonly LegacyTouchMotion _motion = new();
    public LegacyTouchCursorEvidence CursorEvidence { get; } = new();
    private (int X, int Y, bool Down, bool ForceMenu)? _pendingMenu;
    private int _failures;
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    /// <summary>
    /// 结束普通手势时只丢弃尚未发送的采样。FlushLook 的 SendInput 已返回，但对应的
    /// WM_MOUSEMOVE 可能还在覆盖层消息队列中；发送端登记的凭证必须保留到消费或过期。
    /// </summary>
    public void ResetPendingInput() { _motion.Reset(); _pendingMenu = null; }

    /// <summary>失焦、关闭、切换模式或会话失效时，同时撤销在途输入的凭证。</summary>
    public void Reset() { ResetPendingInput(); TouchMousePromotionFilter.ClearReceipts(game); }
    public void MoveLook(double x, double y)
    {
        if (GetForegroundWindow() != game || !double.IsFinite(x) || !double.IsFinite(y)) { Reset(); return; }
        TouchMousePromotionFilter.RecordPointerSample(game, menu: false);
        _motion.Add(x, y);
    }
    // 由独立输入批次合并投递，不依赖悬浮层重绘；子像素保留到下次采样。
    public void FlushLook()
    {
        if (!_motion.Pending) return;
        if (GetForegroundWindow() != game) { Reset(); return; }
        if (!_motion.Take(out var dx, out var dy)) return;
        var success = TouchInputInjector.TryMouseMoveRelative(dx, dy, game);
        TouchMousePromotionFilter.RecordLookInjection(game, success);
        if (!success)
        {
            Reset();
            if (++_failures == 1) LauncherLogService.AppendLine("[旧版触屏] Windows 拒绝输入注入。请让启动器与游戏使用相同权限级别，并确认游戏在前台。");
        }
        else _failures = 0;
    }
    public bool MoveMenu(int x, int y, bool down, bool forceMenu = false)
    {
        TouchMousePromotionFilter.RecordPointerSample(game, menu: true);
        _pendingMenu = null;
        var moved = GetForegroundWindow() == game && TouchInputInjector.MenuPointerMove(game, x, y, down,
            worldEvidence.Phase == LegacyTouchWorldPhase.Joined, CursorEvidence, forceMenu);
        if (moved) CursorEvidence.MenuMoved(x, y, Environment.TickCount64);
        return moved;
    }
    public bool QueueMenu(int x, int y, bool down, bool forceMenu = false)
    {
        if (GetForegroundWindow() != game) return false;
        // 延迟发送必须保留落指时的明确菜单选择，不能在 Flush 时退回原生猜测。
        _pendingMenu = (x, y, down, forceMenu);
        return true;
    }
    public bool FlushMenu()
    {
        if (_pendingMenu is not { } point) return true;
        return MoveMenu(point.X, point.Y, point.Down, point.ForceMenu);
    }
    public bool MenuButton(int x, int y, bool down)
    {
        var moved = FlushMenu();
        // 释放始终发给原窗口；失败的位移不能导致遗留按下。
        return (!down || (moved && GetForegroundWindow() == game)) && TouchInputInjector.MenuLeftButton(game, x, y, down);
    }
}
