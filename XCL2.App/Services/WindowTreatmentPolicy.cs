using System.Windows;

namespace XCL2.App.Services;

public static class WindowTreatmentPolicy
{
    /// <summary>该窗口是否应当被所有全局窗口外观处理跳过。</summary>
    public static bool IsExempt(Window? window)
    {
        if (window == null) return true;
        try
        {
            
            if (window.AllowsTransparency) return true;
        }
        catch { /* 极端情况下读 DP 失败，按不跳过处理，保持原有行为 */ }

        // 显式声明豁免的窗口（即使将来因为某些原因不再需要 AllowsTransparency，
        // 也不希望被全局处理插手）。
        return window is IGlobalWindowTreatmentExempt;
    }
}

/// <summary>
/// 显式声明"不要对我套用任何全局窗口外观处理"的标记接口，语义见
/// <see cref="WindowTreatmentPolicy"/>。TouchOverlayWindow 实现它。
/// </summary>
public interface IGlobalWindowTreatmentExempt
{
}
