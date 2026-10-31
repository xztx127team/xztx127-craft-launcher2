using System.IO;
using System.Security.Cryptography;
using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>性能计数的纯计算部分，不读取系统、不启动任何进程。</summary>
public static class PerformanceMetricsMath
{
    public static double? ProcessCpuPercent(long previousCpuTicks, long currentCpuTicks,
        long elapsedClockTicks, long clockFrequency, int logicalProcessorCount)
    {
        if (previousCpuTicks < 0 || currentCpuTicks < previousCpuTicks || elapsedClockTicks <= 0 ||
            clockFrequency <= 0 || logicalProcessorCount <= 0) return null;
        var elapsedSeconds = (double)elapsedClockTicks / clockFrequency;
        var cpuSeconds = ((double)currentCpuTicks - previousCpuTicks) / TimeSpan.TicksPerSecond;
        var result = cpuSeconds / elapsedSeconds / logicalProcessorCount * 100;
        return double.IsFinite(result) ? Math.Clamp(result, 0, 100) : null;
    }

    /// <summary>GetSystemTimes 的 kernel 包含 idle；user + kernel 是全机总时间。</summary>
    public static double? SystemCpuPercent(ulong oldIdle, ulong oldKernel, ulong oldUser,
        ulong idle, ulong kernel, ulong user)
    {
        if (idle < oldIdle || kernel < oldKernel || user < oldUser) return null;
        var total = (double)(kernel - oldKernel) + (user - oldUser);
        var inactive = (double)(idle - oldIdle);
        if (total <= 0 || inactive > total) return null;
        return Math.Clamp((1 - inactive / total) * 100, 0, 100);
    }

    /// <summary>相同名称、不同目录的实例互不混淆；身份中不包含明文路径。</summary>
    public static string CreateInstanceId(string versionId, string gameDirectory, int processId)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return $"external:{processId}";
        var directory = gameDirectory.Trim();
        if (OperatingSystem.IsWindows())
        {
            // 处理相对目录和 . / ..；这里只规范文本，不枚举或读取目录。
            try { directory = Path.GetFullPath(directory); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        var normalized = directory.Replace('/', '\\').TrimEnd('\\')
            .Normalize(NormalizationForm.FormC).ToUpperInvariant();
        var identity = normalized + "\n" + versionId.Normalize(NormalizationForm.FormC);
        return "java:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static PerformanceGameTotals Summarize(IEnumerable<PerformanceGameSample> source)
    {
        var games = source.ToArray();
        var cpuReady = games.All(game => game.CpuPercent is { } cpu && double.IsFinite(cpu) && cpu is >= 0 and <= 100);
        var memoryReady = games.All(game => game.WorkingSetBytes is >= 0);
        long? memory = null;
        if (memoryReady)
        {
            try { memory = games.Aggregate(0L, (sum, game) => checked(sum + game.WorkingSetBytes!.Value)); }
            catch (OverflowException) { memoryReady = false; }
        }
        return new PerformanceGameTotals(games.Length,
            cpuReady ? Math.Clamp(games.Sum(game => game.CpuPercent!.Value), 0, 100) : null,
            memory,
            cpuReady ? null : "部分游戏尚无 CPU 数据，暂不显示不完整合计。",
            memoryReady ? null : "部分游戏的物理内存无法读取，暂不显示不完整合计。");
    }

    public static double? MemoryUsedPercent(long? usedBytes, long? totalBytes)
        => usedBytes is >= 0 && totalBytes is > 0 && usedBytes <= totalBytes
            ? usedBytes.Value / (double)totalBytes.Value * 100 : null;
}
