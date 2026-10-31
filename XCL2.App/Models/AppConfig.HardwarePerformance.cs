namespace XCL2.App.Models;

public partial class AppConfig
{
    public bool PerformanceOverlayShowGpu { get; set; } = true;
    public bool PerformanceOverlayShowNpu { get; set; } = true;
    // Kept for older configurations. Null per-device switches inherit this value until per-device preferences are saved.
    public bool PerformanceOverlayShowDeviceMemory { get; set; } = true;
    public bool PerformanceOverlayGpuShowUtilization { get; set; } = true;
    public bool PerformanceOverlayNpuShowUtilization { get; set; } = true;
    public string PerformanceOverlayGpuUtilizationFormat { get; set; } = "Percent";
    public string PerformanceOverlayNpuUtilizationFormat { get; set; } = "Percent";
    public bool? PerformanceOverlayGpuShowDedicatedMemory { get; set; }
    public bool? PerformanceOverlayGpuShowSharedMemory { get; set; }
    public bool? PerformanceOverlayNpuShowDedicatedMemory { get; set; }
    public bool? PerformanceOverlayNpuShowSharedMemory { get; set; }
    // Null preserves the old global MB/GB preference. Explicit units use binary, correctly labelled units.
    public string? PerformanceOverlayGpuDedicatedMemoryUnit { get; set; }
    public string? PerformanceOverlayGpuSharedMemoryUnit { get; set; }
    public string? PerformanceOverlayNpuDedicatedMemoryUnit { get; set; }
    public string? PerformanceOverlayNpuSharedMemoryUnit { get; set; }
    public bool PerformanceOverlayShowDisk { get; set; }
    public bool PerformanceOverlayShowNetwork { get; set; }
}
