using System.Runtime.InteropServices;

namespace XCL2.App.Services;

/// <summary>
/// GLFW 的传统鼠标路径读取 WM_MOUSEMOVE。SendInput 命中置顶悬浮层时，
/// 只转交自身注入和明确的硬件鼠标消息。GLFW/LWJGL 回中已经更新基线，
/// 不转发系统回声/触摸同步消息，不用于 SDL，也不伪造 WM_INPUT。
/// </summary>
internal static class GlfwMouseMessageRelay
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    public static bool Forward(IntPtr overlay, IntPtr game, int message, IntPtr wParam, IntPtr lParam,
        LegacyMouseMessageKind kind)
    {
        if (GetForegroundWindow() != game || kind == LegacyMouseMessageKind.Rejected) return false;
        var injected = kind == LegacyMouseMessageKind.Injected;
        // 移动含物理鼠标和我们注入的位移；按钮只转发我们的事件，
        // 否则点击悬浮层按键会同时触发游戏攻击。
        var move = message == 0x0200 && LegacyTouchMousePolicy.CanForwardGameplayMove(kind, contactActive: false);
        var button = message is 0x0201 or 0x0202 or 0x0204 or 0x0205 or 0x0207 or 0x0208;
        var wheel = message is 0x020A or 0x020E;
        if (!move && !(injected && (button || wheel))) return false;
        var translated = lParam;
        if (!wheel)
        {
            var packed = lParam.ToInt64();
            var point = new POINT { X = unchecked((short)(packed & 0xffff)), Y = unchecked((short)((packed >> 16) & 0xffff)) };
            if (!ClientToScreen(overlay, ref point) || !ScreenToClient(game, ref point)) return false;
            translated = new IntPtr(unchecked((int)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16))));
        }
        // 异步投递，不在触摸 UI 线程等待游戏渲染线程，也不把同一事件交回 WPF 二次生成拖动。
        var forwarded = PostMessage(game, (uint)message, wParam, translated);
        if (forwarded) TouchMousePromotionFilter.RecordForwarded(game, message);
        return forwarded;
    }
}
