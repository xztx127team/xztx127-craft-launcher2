using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>Old LWJGL/GLFW bridge: menu coordinates and relative camera motion are separate transports.</summary>
internal sealed class LegacyTouchInputBridge(IntPtr game, LegacyTouchWorldEvidence worldEvidence)
{
    private readonly LegacyTouchMotion _motion = new();
    public LegacyTouchCursorEvidence CursorEvidence { get; } = new();
    private (int X, int Y, bool Down)? _pendingMenu;
    private int _failures;
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    public void Reset() { _motion.Reset(); _pendingMenu = null; }
    public void MoveLook(double x, double y)
    {
        if (GetForegroundWindow() != game || !double.IsFinite(x) || !double.IsFinite(y)) { Reset(); return; }
        _motion.Add(x, y);
    }
    // 由独立输入定时器合并投递，不依赖悬浮层重绘；子像素保留到下次采样。
    public void FlushLook()
    {
        if (!_motion.Pending) return;
        if (GetForegroundWindow() != game) { Reset(); return; }
        if (!_motion.Take(out var dx, out var dy)) return;
        if (!TouchInputInjector.TryMouseMoveRelative(dx, dy))
        {
            Reset();
            if (++_failures == 1) LauncherLogService.AppendLine("[旧版触屏] Windows 拒绝输入注入。请让启动器与游戏使用相同权限级别，并确认游戏在前台。");
        }
        else _failures = 0;
    }
    public bool MoveMenu(int x, int y, bool down)
    {
        _pendingMenu = null;
        var moved = GetForegroundWindow() == game && TouchInputInjector.MenuPointerMove(game, x, y, down,
            worldEvidence.Phase == LegacyTouchWorldPhase.Joined, CursorEvidence);
        if (moved) CursorEvidence.MenuMoved(x, y, Environment.TickCount64);
        return moved;
    }
    public bool QueueMenu(int x, int y, bool down)
    {
        if (GetForegroundWindow() != game) return false;
        _pendingMenu = (x, y, down);
        return true;
    }
    public bool FlushMenu()
    {
        if (_pendingMenu is not { } point) return true;
        return MoveMenu(point.X, point.Y, point.Down);
    }
    public bool MenuButton(int x, int y, bool down)
    {
        var moved = FlushMenu();
        // 释放始终发给原窗口；失败的位移不能导致遗留按下。
        return (!down || (moved && GetForegroundWindow() == game)) && TouchInputInjector.MenuLeftButton(game, x, y, down);
    }
}
