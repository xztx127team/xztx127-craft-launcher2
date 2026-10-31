namespace XCL2.App.Models;

public partial class AppConfig
{
    // Display-only preferences. Manual values never change a device's clocks or firmware.
    public Dictionary<string, PerformanceHardwareDetailOptions> PerformanceOverlayHardwareDetails { get; set; } = new();
}

public sealed class PerformanceHardwareDetailOptions
{
    public bool Show { get; set; }
    public bool ShowManufacturer { get; set; } = true;
    public bool ShowDefaultRate { get; set; }
    public bool ShowStandardRate { get; set; }
    public bool ShowSpeed { get; set; } = true;
    public bool ShowMemoryChannels { get; set; }
    public bool ShowMemoryCapacity { get; set; }
    public string Manufacturer { get; set; } = "";
    public string DefaultRate { get; set; } = "";
    public string StandardRate { get; set; } = "";
    public string Speed { get; set; } = "";
    public string MemoryChannels { get; set; } = "";
    public string MemoryCapacity { get; set; } = "";
}
