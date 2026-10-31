namespace XCL2.App.Services;

/// <summary>旧后端的透明光标仍可能带 CURSOR_SHOWING；失败时保留原生可见状态。</summary>
internal static class LegacyTouchCursorVisibilityPolicy
{
    public static bool IsVisible(bool nativeShowing, bool hasCursor, bool? hasVisiblePixels)
        => nativeShowing && hasCursor && hasVisiblePixels != false;

    /// <summary>
    /// 只有非空且绘制确认为全透明的形状，才提供独立于触摸抑制的隐藏证据。
    /// 这里只规范光标状态；是否进入世界仍由世界阶段、约束及原生回中判断。
    /// </summary>
    public static (bool Showing, bool Suppressed) Resolve(bool nativeShowing, bool nativeSuppressed,
        bool hasCursor, bool? hasVisiblePixels)
    {
        var confirmedBlank = hasCursor && hasVisiblePixels == false;
        return (IsVisible(nativeShowing, hasCursor, hasVisiblePixels), nativeSuppressed && !confirmedBlank);
    }

    // 只比较 RGB。GDI 可以改变 DIB 的保留/alpha 字节，这不代表光标真的可见。
    public static bool ChangesRgb(ReadOnlySpan<int> pixels, int background)
    {
        foreach (var pixel in pixels)
            if ((pixel & 0x00ffffff) != (background & 0x00ffffff)) return true;
        return false;
    }
}
