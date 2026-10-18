namespace XCL2.App.Services;

/// <summary>鼠标抓取优先于系统残留光标；未知样本不切换模式。</summary>
public static class TouchInputModePolicy
{
    /// <summary>SDL 的触摸抑制不是菜单证据，尤其不能用残留箭头隐藏正在操作的控制区。</summary>
    public static bool? ClassifyModern(bool confined, bool wholeDesktop, bool hasCursor,
        bool showing, bool suppressed, bool allowFullscreenCapture)
    {
        if (suppressed) return confined && !wholeDesktop ? false : null;
        if (hasCursor && showing) return true;
        if (!showing && confined && (!wholeDesktop || allowFullscreenCapture)) return false;
        return null;
    }

    public static bool? Classify(bool confined, bool wholeDesktop, bool hasCursor,
        bool showing, bool suppressed, bool allowFullscreenCapture,
        bool worldJoined = false, bool cursorCentered = false, bool recenteringObserved = false)
    {
        // 触摸/WPF 可能留下箭头句柄甚至 SHOWING 标志，客户区抓取仍表示游戏视角。
        if (confined && !wholeDesktop) return false;
        // SUPPRESSED 表示 Windows 因触摸隐藏光标，不能拿箭头句柄推断游戏菜单。
        if (suppressed) return null;
        // 加载画面也会隐藏光标。无局部约束的全屏/LWJGL 路径必须已经收到
        // 客户端世界数据且光标在客户区中心，或真实菜单定位后的持续原生回中。
        // 回中一旦确认，实际视角输入暂时使指针离开中心不应退回加载菜单。
        if (!showing) return (worldJoined && cursorCentered) || recenteringObserved ? false : null;
        if (hasCursor && showing && (!confined || wholeDesktop)) return true;
        return null;
    }
}
