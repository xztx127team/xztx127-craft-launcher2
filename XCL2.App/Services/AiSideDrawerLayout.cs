namespace XCL2.App.Services;

/// <summary>抽屉只占右侧一小块；拖动/缩放后始终留在当前内容区。</summary>
public readonly record struct AiSideDrawerBounds(double X, double Y, double Width, double Height);

public static class AiSideDrawerLayout
{
    public static AiSideDrawerBounds Place(double availableWidth, double availableHeight, bool docked, double x = 0, double y = 0)
    {
        var width = Math.Max(0, double.IsFinite(availableWidth) ? availableWidth : 0);
        var height = Math.Max(0, double.IsFinite(availableHeight) ? availableHeight : 0);
        var panelWidth = Math.Min(Math.Max(0, width - 16), Math.Clamp(width * .55, 360, 408));
        var panelHeight = Math.Min(760, Math.Max(0, height - 24));
        var maxX = Math.Max(0, width - panelWidth - 8);
        var maxY = Math.Max(0, height - panelHeight - 8);
        return new(docked ? maxX : Math.Clamp(double.IsFinite(x) ? x : 8, 0, maxX),
            docked ? Math.Min(12, maxY) : Math.Clamp(double.IsFinite(y) ? y : 12, 0, maxY), panelWidth, panelHeight);
    }
}
