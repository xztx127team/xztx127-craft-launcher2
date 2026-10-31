// Minimal settings boundary for the platform-independent formatter/filter tests.
namespace XCL2.App.Models;
public sealed partial class AppConfig
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
    public bool PerformanceOverlayShowSystemCpu { get; set; } = true;
    public bool PerformanceOverlayShowSystemMemory { get; set; } = true;
    public bool PerformanceOverlayShowMemoryPercent { get; set; } = true;
    public bool PerformanceOverlayShowLauncher { get; set; }
    public bool PerformanceOverlayShowGames { get; set; } = true;
    public bool PerformanceOverlayShowGameTotals { get; set; }
    public bool PerformanceOverlayShowGameCpu { get; set; } = true;
    public bool PerformanceOverlayShowGameMemory { get; set; } = true;
    public bool PerformanceOverlayShowProcessId { get; set; }
    public string PerformanceOverlayInstanceName { get; set; } = "";
    public string PerformanceOverlayInstanceId { get; set; } = "";
}
