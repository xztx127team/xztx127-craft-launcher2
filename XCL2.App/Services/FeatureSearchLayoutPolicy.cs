namespace XCL2.App.Services;

/// <summary>搜索入口的用户偏好（WPF 设备无关像素）；预览和落盘共用同一份数值约束。</summary>
public readonly record struct FeatureSearchLayout(double Width, double Height, double OffsetX, double OffsetY, double OpacityPercent)
{
    public static FeatureSearchLayout Default => new(300, 36, 0, 0, 70);
    public FeatureSearchLayout Normalize() => new(
        Clamp(Width, 160, 720, 300), Clamp(Height, 28, 120, 36),
        Clamp(OffsetX, -10000, 10000, 0), Clamp(OffsetY, -10000, 10000, 0),
        Clamp(OpacityPercent, 20, 100, 70));
    private static double Clamp(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public readonly record struct FeatureSearchPlacement(double X, double Y, double Width, double Height);

/// <summary>固定按下锚点，越过拖动阈值后直接跟随指针。</summary>
internal sealed class FeatureSearchPointerDrag
{
    private double _anchorX, _anchorY;
    public bool IsActive { get; private set; }
    public bool IsDragging { get; private set; }
    public void Begin(double x, double y) { _anchorX = x; _anchorY = y; IsActive = true; IsDragging = false; }
    public (double X, double Y)? Move(double x, double y, double thresholdX = 4, double thresholdY = 4)
    {
        if (!IsActive) return null;
        var dx = x - _anchorX; var dy = y - _anchorY;
        if (!IsDragging && Math.Abs(dx) < thresholdX && Math.Abs(dy) < thresholdY) return null;
        IsDragging = true;
        return (dx, dy);
    }
    public void End() => IsActive = IsDragging = false;
}

public static class FeatureSearchLayoutPolicy
{
    // 原来的 -7 向下移动 5 个 WPF 像素。
    public const double FloatingTop = -2;

    public static FeatureSearchPlacement Resolve(FeatureSearchLayout layout, bool floating, double availableWidth, double availableHeight)
    {
        layout = layout.Normalize();
        double hostWidth = double.IsFinite(availableWidth) && availableWidth > 0 ? availableWidth : 300;
        double hostHeight = double.IsFinite(availableHeight) && availableHeight > 0 ? availableHeight : 36;
        double width = Math.Min(layout.Width, hostWidth);
        double height = Math.Min(layout.Height, hostHeight);
        double top = floating ? FloatingTop : 0;
        double defaultX = floating ? 0 : hostWidth - width;
        return new(Math.Clamp(defaultX + layout.OffsetX, 0, hostWidth - width),
            Math.Clamp(top + layout.OffsetY, top, Math.Max(top, hostHeight - height)), width, height);
    }

    public static FeatureSearchLayout FromPlacement(FeatureSearchPlacement placement, FeatureSearchLayout previous,
        bool floating, double availableWidth, double availableHeight)
    {
        var size = (previous with { Width = placement.Width, Height = placement.Height }).Normalize();
        double defaultX = floating ? 0 : Math.Max(0, availableWidth - Math.Min(size.Width, availableWidth));
        var requested = size with { OffsetX = placement.X - defaultX, OffsetY = placement.Y - (floating ? FloatingTop : 0) };
        var resolved = Resolve(requested, floating, availableWidth, availableHeight);
        return (requested with { OffsetX = resolved.X - defaultX, OffsetY = resolved.Y - (floating ? FloatingTop : 0) }).Normalize();
    }
}
