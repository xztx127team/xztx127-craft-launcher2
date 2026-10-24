namespace XCL2.App.Models;

public partial class AppConfig
{
    // Default preserves the existing Compact/Detailed presentation. Template changes arrangement only.
    public string PerformanceOverlayLayoutMode { get; set; } = "Default";
    public string PerformanceOverlayTextTemplate { get; set; } = "{system_cpu} · {system_memory}\n{gpu}\n{npu}\n{launcher}\n{game_totals}\n{games}\n{disk}\n{network}\n{hardware_details}";
    // Preserve keeps intentional line breaks. SingleLine removes all; EachField starts every selected item on a line.
    public string PerformanceOverlayLineBreakMode { get; set; } = "Preserve";
    public bool PerformanceOverlayAutoWrap { get; set; } = true;
    /// <summary>Ordinary interactive display by default; persisted explicit choices remain unchanged.</summary>
    public bool PerformanceOverlayClickThrough { get; set; } = false;
}
