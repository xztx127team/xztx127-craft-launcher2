using XCL2.App.Models;

namespace XCL2.App.Services;

public readonly record struct WindowPlacementBounds(int Left, int Top, int Width, int Height);

/// <summary>Convert saved DIP geometry to a visible rectangle without replacing valid saved sizes.</summary>
public static class WindowPlacementGeometry
{
    public static WindowPlacementBounds Resolve(LauncherWindowPlacement placement,
        WindowPlacementBounds workArea, double dpiScale, double minimumWidth, double minimumHeight)
    {
        var dpi = Positive(dpiScale, 1);
        var areaWidth = Math.Max(1, workArea.Width);
        var areaHeight = Math.Max(1, workArea.Height);
        var width = Math.Min(areaWidth, Math.Max(Positive(minimumWidth, 1) * dpi,
            Positive(placement.Width, LauncherWindowPlacement.DefaultWidth) * dpi));
        var height = Math.Min(areaHeight, Math.Max(Positive(minimumHeight, 1) * dpi,
            Positive(placement.Height, LauncherWindowPlacement.DefaultHeight) * dpi));
        var pixelWidth = Math.Clamp((int)Math.Round(width), 1, areaWidth);
        var pixelHeight = Math.Clamp((int)Math.Round(height), 1, areaHeight);
        var x = workArea.Left + (areaWidth - pixelWidth) * Fraction(placement.HorizontalPosition);
        var y = workArea.Top + (areaHeight - pixelHeight) * Fraction(placement.VerticalPosition);
        return new((int)Math.Round(x), (int)Math.Round(y), pixelWidth, pixelHeight);
    }

    public static double RelativePosition(double offset, double available)
        => available <= 0 ? 0 : Math.Clamp(offset / available, 0, 1);

    private static double Positive(double value, double fallback)
        => double.IsFinite(value) && value > 0 ? value : fallback;

    private static double Fraction(double value)
        => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : .5;
}
