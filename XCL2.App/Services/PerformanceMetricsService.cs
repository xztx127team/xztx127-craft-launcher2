using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 按需读取真实性能数据。没有常驻定时器；可见界面负责低频调用，关闭后便不再采样。
/// 系统调用、进程查询和基岩版发现均在后台运行，串行化防止计数基线互相覆盖。
/// </summary>
public sealed class PerformanceMetricsService
{
    private const string FirstSampleReason = "首次采样，等待下一次计算 CPU。";
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private readonly Dictionary<int, CpuBaseline> _processBaselines = new();
    private SystemTimes? _systemBaseline;
    private long _lastCompletedTimestamp;
    private long _bedrockRefreshTimestamp;
    private IReadOnlyList<PerformanceGameTarget> _bedrockTargets = Array.Empty<PerformanceGameTarget>();
    private string? _discoveryWarning;
    private PerformanceHardwareService? _hardware;

    private sealed record CpuBaseline(long CpuTicks, long StartedAtTicks, long Timestamp);
    private readonly record struct SystemTimes(ulong Idle, ulong Kernel, ulong User);

    /// <summary>请先在进程列表所属的 UI 线程取得 Running 快照；这里不访问任何原生计数。</summary>
    public static IReadOnlyList<PerformanceGameTarget> CreateJavaTargets(IEnumerable<GameProcessInfo> games)
        => games.Select(game => new PerformanceGameTarget(game.Pid,
            PerformanceMetricsMath.CreateInstanceId(game.VersionId, game.GameDir, game.Pid),
            game.VersionId, "Java") { TrackedProcess = game.Process })
            .Where(game => game.ProcessId > 0).DistinctBy(game => game.ProcessId).ToArray();

    /// <summary>CPU 百分比按整机逻辑核总算力计算。长时间暂停后重新建立基线，不显示暂停期均值。</summary>
    public async Task<PerformanceSnapshot> SampleAsync(IReadOnlyList<PerformanceGameTarget> games,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(games);
        // 调用方可安全更新自己的列表；后台只持有不可变目标快照。
        var targets = games.Where(game => game.ProcessId > 0).DistinctBy(game => game.ProcessId).ToArray();
        await _sampleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => SampleCore(targets, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally { _sampleGate.Release(); }
    }

    private PerformanceSnapshot SampleCore(IReadOnlyList<PerformanceGameTarget> targets, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var now = Stopwatch.GetTimestamp();
        if (_lastCompletedTimestamp != 0 && (double)(now - _lastCompletedTimestamp) / Stopwatch.Frequency > 15)
        {
            _systemBaseline = null;
            _processBaselines.Clear();
        }

        var cpu = ReadSystemCpu(out var cpuReason);
        ReadMemory(out var total, out var available, out var memoryReason);
        token.ThrowIfCancellationRequested();
        RefreshBedrockTargets(now);
        var logicalProcessors = ReadLogicalProcessorCount();
        var launcher = ReadProcess(new PerformanceGameTarget(Environment.ProcessId, "launcher", "XCL2", "Launcher"),
            logicalProcessors);
        var allTargets = targets.Concat(_bedrockTargets).DistinctBy(game => game.ProcessId).ToArray();
        var games = new List<PerformanceGameSample>(allTargets.Length);
        var active = new HashSet<int> { Environment.ProcessId };
        foreach (var target in allTargets)
        {
            token.ThrowIfCancellationRequested();
            active.Add(target.ProcessId);
            games.Add(ReadProcess(target, logicalProcessors));
        }
        foreach (var pid in _processBaselines.Keys.Where(pid => !active.Contains(pid)).ToArray())
            _processBaselines.Remove(pid);
        _lastCompletedTimestamp = Stopwatch.GetTimestamp();
        return new PerformanceSnapshot(DateTimeOffset.Now, logicalProcessors, cpu, total, available,
            launcher.CpuPercent, launcher.WorkingSetBytes, games.ToArray(), cpuReason, memoryReason,
            launcher.CpuUnavailableReason, launcher.MemoryUnavailableReason, _discoveryWarning)
        { Hardware = (_hardware ??= new PerformanceHardwareService()).Sample() };
    }

    public async Task ReleaseHardwareAsync()
    {
        await _sampleGate.WaitAsync().ConfigureAwait(false);
        try { _hardware?.Dispose(); _hardware = null; }
        finally { _sampleGate.Release(); }
    }

    private PerformanceGameSample ReadProcess(PerformanceGameTarget target, int logicalProcessors)
    {
        try
        {
            // 使用原跟踪句柄验证仍是同一次启动，不能仅凭 PID 认定身份。
            if (target.TrackedProcess?.HasExited == true)
                return Unavailable(target, "进程已退出。");
            using var process = Process.GetProcessById(target.ProcessId);
            if (process.HasExited) return Unavailable(target, "进程已退出。");
            if (target.Edition == "Bedrock" &&
                !process.ProcessName.StartsWith("Minecraft.Windows", StringComparison.OrdinalIgnoreCase))
                return Unavailable(target, "游戏进程已退出或进程身份已变化。");

            long? memory = null;
            string? memoryReason = null;
            try { memory = Math.Max(0, process.WorkingSet64); }
            catch (Exception ex) when (IsReadFailure(ex)) { memoryReason = ReadFailureReason(ex); }

            double? cpu = null;
            string? cpuReason;
            try
            {
                var startedAt = process.StartTime.ToUniversalTime().Ticks;
                var cpuTicks = process.TotalProcessorTime.Ticks;
                var timestamp = Stopwatch.GetTimestamp();
                if (_processBaselines.TryGetValue(target.ProcessId, out var previous) && previous.StartedAtTicks == startedAt)
                {
                    cpu = PerformanceMetricsMath.ProcessCpuPercent(previous.CpuTicks, cpuTicks,
                        timestamp - previous.Timestamp, Stopwatch.Frequency, logicalProcessors);
                    cpuReason = cpu.HasValue ? null : "CPU 计数暂不可用，等待下一次采样。";
                }
                else cpuReason = FirstSampleReason;
                _processBaselines[target.ProcessId] = new CpuBaseline(cpuTicks, startedAt, timestamp);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                _processBaselines.Remove(target.ProcessId);
                cpuReason = ReadFailureReason(ex);
            }
            // 读取期间退出也不把不再运行的实例当作有效资源占用。
            if (process.HasExited || target.TrackedProcess?.HasExited == true)
                return Unavailable(target, "进程已退出。");
            return new PerformanceGameSample(target.ProcessId, target.InstanceId, target.DisplayName,
                target.Edition, cpu, memory, cpuReason, memoryReason);
        }
        catch (Exception ex) when (IsReadFailure(ex)) { return Unavailable(target, ReadFailureReason(ex)); }
    }

    private PerformanceGameSample Unavailable(PerformanceGameTarget target, string reason)
    {
        _processBaselines.Remove(target.ProcessId);
        return new PerformanceGameSample(target.ProcessId, target.InstanceId, target.DisplayName,
            target.Edition, null, null, reason, reason);
    }

    private void RefreshBedrockTargets(long timestamp)
    {
        if (_bedrockRefreshTimestamp != 0 &&
            (double)(timestamp - _bedrockRefreshTimestamp) / Stopwatch.Frequency < 5) return;
        _bedrockRefreshTimestamp = timestamp;
        try
        {
            // 复用现有精确 Minecraft.Windows 命名族识别，不把普通 Java 程序当作游戏。
            _bedrockTargets = BedrockLaunchService.GetRunningClientProcesses().Select(game =>
                new PerformanceGameTarget(game.ProcessId, "bedrock:" + game.ProcessName.ToUpperInvariant(),
                    string.IsNullOrWhiteSpace(game.WindowTitle) ? game.ProcessName : game.WindowTitle, "Bedrock"))
                .ToArray();
            _discoveryWarning = null;
        }
        catch (Exception ex) when (IsReadFailure(ex) || ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _discoveryWarning = "基岩版进程暂时无法发现；已跟踪的 Java 游戏仍正常采样。";
        }
    }

    private double? ReadSystemCpu(out string? reason)
    {
        try
        {
            // GetSystemTimes 在多处理器组机器上只包含当前组，不能把部分组伪装为整机。
            if (GetActiveProcessorGroupCount() > 1)
            {
                _systemBaseline = null;
                reason = "当前系统有多个处理器组，系统 CPU 接口无法提供完整的整机数据。";
                return null;
            }
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            {
                _systemBaseline = null;
                reason = "无法读取系统 CPU 计数。";
                return null;
            }
            var current = new SystemTimes(idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64());
            var value = _systemBaseline is { } old
                ? PerformanceMetricsMath.SystemCpuPercent(old.Idle, old.Kernel, old.User,
                    current.Idle, current.Kernel, current.User) : null;
            reason = value.HasValue ? null : _systemBaseline.HasValue
                ? "系统 CPU 计数暂不可用，等待下一次采样。" : FirstSampleReason;
            _systemBaseline = current;
            return value;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _systemBaseline = null;
            reason = "当前系统不支持 Windows CPU 计数。";
            return null;
        }
    }

    private static int ReadLogicalProcessorCount()
    {
        try
        {
            const ushort AllProcessorGroups = 0xffff;
            var processors = GetActiveProcessorCount(AllProcessorGroups);
            if (processors is > 0 and <= int.MaxValue) return (int)processors;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
        return Math.Max(1, Environment.ProcessorCount);
    }

    private static void ReadMemory(out long? total, out long? available, out string? reason)
    {
        total = available = null;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        try
        {
            if (!GlobalMemoryStatusEx(ref memory) || memory.TotalPhysical == 0 || memory.TotalPhysical > long.MaxValue)
            {
                reason = "无法读取系统物理内存。";
                return;
            }
            total = (long)memory.TotalPhysical;
            available = (long)Math.Min(memory.AvailablePhysical, memory.TotalPhysical);
            reason = null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { reason = "当前系统不支持 Windows 物理内存计数。"; }
    }

    private static bool IsReadFailure(Exception error)
        => error is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException
            or System.Security.SecurityException or UnauthorizedAccessException;

    private static string ReadFailureReason(Exception error) => error switch
    {
        Win32Exception { NativeErrorCode: 5 } or UnauthorizedAccessException or System.Security.SecurityException
            => "没有读取此进程性能数据的权限。",
        ArgumentException or InvalidOperationException => "进程已退出或暂时无法读取。",
        _ => "此进程暂时不提供性能数据。"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out NativeFileTime idleTime, out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
