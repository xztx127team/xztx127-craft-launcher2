namespace XCL2.App.Services;

/// <summary>鼠标抓取优先于系统残留光标；未知样本不切换模式。</summary>
public static class TouchInputModePolicy
{
    // A look gesture already locks its mode. A synchronous GLFW cursor refresh cannot change
    // that decision and may stall input for its entire timeout; menu-to-look recovery still probes.
    public static bool ShouldProbeLegacyMode(bool currentMenu, bool activeContact)
        => currentMenu || !activeContact;

    /// <summary>
    /// 原生抓取或已满足稳定期的一次性 Joined 恢复可保留按住的控制键进入视角。
    /// Joined 恢复不是永久世界状态；不能单凭触摸抑制启用，也不能抢走已有菜单拖动。
    /// 已确认的原生抓取仍可结束菜单手势；调用方保留控制键，并从当前触点重设视角基线。
    /// </summary>
    public static bool AllowLegacyLookDuringContact(bool currentMenu, bool? observedMenu, bool nativeLookConfirmed,
        bool joinedRecoveryConfirmed = false, bool activeMenuGesture = false)
        => currentMenu && observedMenu == false &&
           (nativeLookConfirmed || joinedRecoveryConfirmed && !activeMenuGesture);

    /// <summary>SDL 的触摸抑制不是菜单证据，尤其不能用残留箭头隐藏正在操作的控制区。</summary>
    public static bool? ClassifyModern(bool confined, bool wholeDesktop, bool hasCursor,
        bool showing, bool suppressed, bool allowFullscreenCapture)
    {
        if (confined && !wholeDesktop) return false;
        if (suppressed) return null;
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
