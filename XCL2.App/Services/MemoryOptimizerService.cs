using System.Runtime.InteropServices;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>采集物理内存，解析手动/实例覆盖优先级，并在每次启动时计算动态 JVM 堆内存。</summary>
public static class MemoryOptimizerService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public sealed record PhysicalMemory(ulong TotalMb, ulong AvailableMb);
    public sealed record Recommendation(
        int RecommendedMinMemoryMb,
        int RecommendedMaxMemoryMb,
        ulong AvailPhysMb,
        ulong TotalPhysMb,
        string Explanation);
    public sealed record LaunchMemory(int MinMemoryMb, int MaxMemoryMb, bool IsDynamic, string? Explanation);

    public static PhysicalMemory? ReadPhysicalMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        try
        {
            if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys < 1024 * 1024) return null;
            return new PhysicalMemory(status.ullTotalPhys / 1024 / 1024,
                Math.Min(status.ullAvailPhys, status.ullTotalPhys) / 1024 / 1024);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    public static MemoryAllocationPolicy.Options ReadOptions(AppConfig cfg) => new(
        cfg.MemoryAllocationMode, cfg.DynamicMemoryMinimumMb, cfg.DynamicMemoryMaximumMb,
        cfg.DynamicMemoryMinimumPercent, cfg.DynamicMemoryMaximumPercent, cfg.FixedMemoryAllocationMb);

    /// <summary>仅规范化新动态字段，不更改开关和已有手动值。</summary>
    public static void NormalizeConfig(AppConfig cfg)
    {
        var value = MemoryAllocationPolicy.Normalize(ReadOptions(cfg));
        cfg.MemoryAllocationMode = value.Mode;
        cfg.DynamicMemoryMinimumMb = value.MinimumMb;
        cfg.DynamicMemoryMaximumMb = value.MaximumMb;
        cfg.DynamicMemoryMinimumPercent = value.MinimumPercent;
        cfg.DynamicMemoryMaximumPercent = value.MaximumPercent;
        cfg.FixedMemoryAllocationMb = value.FixedMb;
        if (!Enum.IsDefined(cfg.MemoryDisplayBasis)) cfg.MemoryDisplayBasis = MemoryDisplayBasis.Total;
    }

    public static Recommendation? Calculate(AppConfig cfg)
    {
        var memory = ReadPhysicalMemory();
        return memory == null ? null : Calculate(cfg, memory);
    }

    /// <summary>兼容旧调用方；reserveMb 仅为旧配置兼容参数，新策略统一按总物理内存计算。</summary>
    public static Recommendation? Calculate(int reserveMb)
        => Calculate(ConfigService.Active?.Config ?? new AppConfig());

    public static Recommendation Calculate(AppConfig cfg, PhysicalMemory memory)
        => Calculate(ReadOptions(cfg), memory, cfg.MemoryDisplayBasis);

    public static Recommendation Calculate(MemoryAllocationPolicy.Options options, PhysicalMemory memory,
        MemoryDisplayBasis displayBasis = MemoryDisplayBasis.Total)
    {
        var value = MemoryAllocationPolicy.Normalize(options);
        var allocation = MemoryAllocationPolicy.Calculate(memory.TotalMb, value);
        var displayed = displayBasis == MemoryDisplayBasis.Available
            ? $"当前剩余内存 {memory.AvailableMb}MB" : $"系统总内存 {memory.TotalMb}MB";
        var mode = value.FixedMb is > 0
            ? $"固定分配 {value.FixedMb}MB"
            : value.Mode == MemoryAllocationMode.TotalPercentage
                ? $"按总内存的 {value.MinimumPercent:0.##}%–{value.MaximumPercent:0.##}%"
                : $"动态范围 {value.MinimumMb}–{value.MaximumMb}MB（上限同时参考总内存的 70%）";
        if (value.FixedMb is > 0 && allocation.MaximumMb != value.FixedMb.Value)
            mode += $"；指定值超过本机总物理内存，本次按 {allocation.MaximumMb}MB 计算";
        var explanation = $"{displayed}；{mode}，本次 -Xms{allocation.MinimumMb}M / -Xmx{allocation.MaximumMb}M。" +
            "百分比始终按总物理内存计算；切换剩余/总内存显示不改变分配。实际启动时会重新计算。";
        return new Recommendation(allocation.MinimumMb, allocation.MaximumMb,
            memory.AvailableMb, memory.TotalMb, explanation);
    }

    /// <summary>实例显式手动内存优先；实例明确开启动态时才覆盖其手动值。采样失败保留手动配置。</summary>
    public static LaunchMemory ResolveLaunchMemory(AppConfig cfg, InstanceSettings? instanceSettings = null)
        => ResolveLaunchMemory(cfg, instanceSettings, ReadPhysicalMemory());

    public static LaunchMemory ResolveLaunchMemory(AppConfig cfg, InstanceSettings? instanceSettings, PhysicalMemory? memory)
    {
        var min = instanceSettings?.MinMemoryMb ?? cfg.MinMemoryMb;
        var max = instanceSettings?.MaxMemoryMb ?? cfg.MaxMemoryMb;
        // 手动参数保持原样，非法组合由统一 JVM 诊断提示用户；不悄悄改写用户输入。
        var explicitManual = instanceSettings?.MinMemoryMb.HasValue == true || instanceSettings?.MaxMemoryMb.HasValue == true;
        var dynamic = instanceSettings?.EnableMemoryOptimization ?? (cfg.EnableMemoryOptimization && !explicitManual);
        if (!dynamic || memory == null || memory.TotalMb == 0)
            return new LaunchMemory(min, max, false, null);
        var recommendation = Calculate(cfg, memory);
        return new LaunchMemory(recommendation.RecommendedMinMemoryMb, recommendation.RecommendedMaxMemoryMb,
            true, recommendation.Explanation);
    }

    /// <summary>仅限制自动生成的 32 位 Java 堆；手动和显式固定值留给 JVM 参数诊断解释。</summary>
    public static LaunchMemory ConstrainForJava(AppConfig cfg, LaunchMemory allocation, int? bitness)
    {
        if (bitness != 32 || !allocation.IsDynamic || cfg.FixedMemoryAllocationMb is > 0 || allocation.MaxMemoryMb <= 1536)
            return allocation;
        return allocation with
        {
            MinMemoryMb = Math.Min(allocation.MinMemoryMb, 1536),
            MaxMemoryMb = 1536,
            Explanation = allocation.Explanation + " 检测到 32 位 Java，自动分配上限已调整为 1536MB。"
        };
    }
}
