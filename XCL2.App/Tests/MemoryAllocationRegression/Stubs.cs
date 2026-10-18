using XCL2.App.Models;

namespace XCL2.App.Models
{
    public sealed class AppConfig
    {
        public int MinMemoryMb { get; set; } = 1024;
        public int MaxMemoryMb { get; set; } = 4096;
        public bool EnableMemoryOptimization { get; set; }
        public MemoryAllocationMode MemoryAllocationMode { get; set; }
        public int DynamicMemoryMinimumMb { get; set; } = 500;
        public int DynamicMemoryMaximumMb { get; set; } = 8192;
        public double DynamicMemoryMinimumPercent { get; set; } = 5;
        public double DynamicMemoryMaximumPercent { get; set; } = 70;
        public int? FixedMemoryAllocationMb { get; set; }
        public MemoryDisplayBasis MemoryDisplayBasis { get; set; }
    }
}

namespace XCL2.App.Services
{
    public sealed class InstanceSettings
    {
        public int? MinMemoryMb { get; set; }
        public int? MaxMemoryMb { get; set; }
        public bool? EnableMemoryOptimization { get; set; }
    }
    public sealed class ConfigService
    {
        public static ConfigService? Active { get; set; }
        public AppConfig Config { get; set; } = new();
    }
}
