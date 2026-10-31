using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>不依赖操作系统的 JVM 堆内存计算；系统采样及配置优先级由 MemoryOptimizerService 处理。</summary>
public static class MemoryAllocationPolicy
{
    public sealed record Options(
        MemoryAllocationMode Mode = MemoryAllocationMode.FixedRange,
        int MinimumMb = 500,
        int MaximumMb = 8192,
        double MinimumPercent = 5,
        double MaximumPercent = 70,
        int? FixedMb = null);

    public sealed record Allocation(int MinimumMb, int MaximumMb);

    public static Options Normalize(Options value)
    {
        var minimum = Math.Clamp(value.MinimumMb, 1, int.MaxValue);
        var maximum = Math.Max(minimum, value.MaximumMb);
        var minimumPercent = double.IsFinite(value.MinimumPercent)
            ? Math.Clamp(value.MinimumPercent, 0.1, 100) : 5;
        var maximumPercent = double.IsFinite(value.MaximumPercent)
            ? Math.Clamp(value.MaximumPercent, minimumPercent, 100) : Math.Max(minimumPercent, 70);
        return value with
        {
            Mode = Enum.IsDefined(value.Mode) ? value.Mode : MemoryAllocationMode.FixedRange,
            MinimumMb = minimum,
            MaximumMb = maximum,
            MinimumPercent = minimumPercent,
            MaximumPercent = maximumPercent,
            FixedMb = value.FixedMb is > 0 ? value.FixedMb : null
        };
    }

    /// <summary>
    /// 每次启动重新用总物理内存计算。范围模式默认为 500MB–8GB，并以总内存的 70% 作为
    /// 动态上限；百分比模式精确使用设置的总内存百分比。可用内存仅用于显示，不参与计算。
    /// 极小内存机器以真实物理上限兜底，不能为满足下限而分配超过物理内存的初始堆。
    /// </summary>
    public static Allocation Calculate(ulong totalPhysicalMb, Options options)
    {
        if (totalPhysicalMb == 0) throw new ArgumentOutOfRangeException(nameof(totalPhysicalMb));
        var value = Normalize(options);
        var physicalLimit = (int)Math.Min(totalPhysicalMb, (ulong)int.MaxValue);
        if (value.FixedMb is > 0)
        {
            var fixedMb = Math.Clamp(value.FixedMb.Value, 1, physicalLimit);
            return new Allocation(fixedMb, fixedMb);
        }

        int minimum;
        int maximum;
        if (value.Mode == MemoryAllocationMode.TotalPercentage)
        {
            minimum = PercentOf(physicalLimit, value.MinimumPercent);
            maximum = PercentOf(physicalLimit, value.MaximumPercent);
        }
        else
        {
            minimum = Math.Min(value.MinimumMb, physicalLimit);
            maximum = Math.Min(value.MaximumMb, Math.Max(minimum, PercentOf(physicalLimit, 70)));
        }
        maximum = Math.Clamp(maximum, 1, physicalLimit);
        minimum = Math.Clamp(minimum, 1, maximum);
        return new Allocation(minimum, maximum);
    }

    private static int PercentOf(int totalMb, double percent)
        => Math.Max(1, (int)Math.Floor(totalMb * (percent / 100.0)));
}
