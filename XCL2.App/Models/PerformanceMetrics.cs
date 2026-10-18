namespace XCL2.App.Models;

/// <summary>只包含展示身份，不传递命令行、账号或游戏目录。</summary>
public sealed record PerformanceGameTarget(
    int ProcessId, string InstanceId, string DisplayName, string Edition = "Java")
{
    // 已跟踪进程的句柄保留其真实生命周期，避免退出后 PID 被其他程序复用。
    // 只在采样后台线程访问，不作为展示数据或序列化属性。
    internal System.Diagnostics.Process? TrackedProcess { get; init; }
}

/// <summary>进程 CPU 为占整台电脑逻辑处理器总算力的百分比，范围 0–100。</summary>
public sealed record PerformanceGameSample(
    int ProcessId,
    string InstanceId,
    string DisplayName,
    string Edition,
    double? CpuPercent,
    long? WorkingSetBytes,
    string? CpuUnavailableReason,
    string? MemoryUnavailableReason);

/// <summary>工作集为操作系统报告的物理内存，不等同于 JVM 堆上限或游戏分配内存。</summary>
public sealed record PerformanceSnapshot(
    DateTimeOffset SampledAt,
    int LogicalProcessorCount,
    double? SystemCpuPercent,
    long? TotalMemoryBytes,
    long? AvailableMemoryBytes,
    double? LauncherCpuPercent,
    long? LauncherWorkingSetBytes,
    IReadOnlyList<PerformanceGameSample> Games,
    string? SystemCpuUnavailableReason,
    string? MemoryUnavailableReason,
    string? LauncherCpuUnavailableReason,
    string? LauncherMemoryUnavailableReason,
    string? DiscoveryWarning)
{
    public long? UsedMemoryBytes => TotalMemoryBytes.HasValue && AvailableMemoryBytes.HasValue
        ? Math.Max(0, TotalMemoryBytes.Value - AvailableMemoryBytes.Value) : null;
}

/// <summary>无法读取某个进程时不把它当作零，也不把部分合计冒充全部游戏。</summary>
public sealed record PerformanceGameTotals(
    int GameCount, double? CpuPercent, long? WorkingSetBytes,
    string? CpuUnavailableReason, string? MemoryUnavailableReason);
