// Minimal settings boundary for the platform-independent formatter/filter tests.
namespace XCL2.App.Models;
public sealed class AppConfig
{
    public double PerformanceOverlayFontSize { get; set; }
    public double PerformanceOverlayOpacityPercent { get; set; }
    public int PerformanceOverlayRefreshMilliseconds { get; set; }
    public string PerformanceOverlayStyle { get; set; } = "Compact";
    public int PerformanceOverlayWindowWidth { get; set; }
    public string PerformanceOverlayGameSort { get; set; } = "Name";
    public string PerformanceOverlayColorScheme { get; set; } = "Theme";
    public string PerformanceOverlayMemoryUnit { get; set; } = "Auto";
    public bool PerformanceOverlayAllInstances { get; set; } = true;
    public string PerformanceOverlayInstanceId { get; set; } = "";
}
