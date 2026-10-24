using System.Globalization;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>悬浮窗独立的配置边界；读取坏配置时也不会创建不可见或高频刷新的窗口。</summary>
public sealed record PerformanceHardwareDisplay(bool Show, bool ShowUtilization, string UtilizationFormat,
    bool ShowDedicatedMemory, string DedicatedMemoryUnit, bool ShowSharedMemory, string SharedMemoryUnit);

public static class PerformanceOverlaySettings
{
    public static double FontSize(AppConfig cfg) => FiniteClamp(cfg.PerformanceOverlayFontSize, 10, 26, 14);
    public static double Opacity(AppConfig cfg) => FiniteClamp(cfg.PerformanceOverlayOpacityPercent, 25, 100, 90) / 100;
    public static int RefreshMilliseconds(AppConfig cfg) => Math.Clamp(cfg.PerformanceOverlayRefreshMilliseconds, 500, 10000);
    public static bool Detailed(AppConfig cfg) => cfg.PerformanceOverlayStyle == "Detailed";
    public static int WindowWidth(AppConfig cfg) => cfg.PerformanceOverlayWindowWidth <= 0
        ? (Detailed(cfg) ? 420 : 320) : Math.Clamp(cfg.PerformanceOverlayWindowWidth, 280, 600);
    public static string GameSort(AppConfig cfg)
        => cfg.PerformanceOverlayGameSort is "Cpu" or "Memory" ? cfg.PerformanceOverlayGameSort : "Name";
    public static string ColorScheme(AppConfig cfg)
        => cfg.PerformanceOverlayColorScheme is "Dark" or "Light" ? cfg.PerformanceOverlayColorScheme : "Theme";
    public static string MemoryUnit(AppConfig cfg)
        => cfg.PerformanceOverlayMemoryUnit is "MB" or "GB" ? cfg.PerformanceOverlayMemoryUnit : "Auto";

    public static string HardwareUtilizationFormat(string? format)
        => format is "PercentInteger" or "Ratio" ? format : "Percent";
    public static string HardwareMemoryUnit(string? unit, AppConfig cfg)
        => unit == null ? MemoryUnit(cfg) switch { "MB" => "MiB", "GB" => "GiB", _ => "Auto" }
            : unit is "B" or "KiB" or "MiB" or "GiB" ? unit : "Auto";

    public static PerformanceHardwareDisplay HardwareDisplay(AppConfig cfg, string kind)
    {
        var gpu = kind == "GPU";
        return new(gpu ? cfg.PerformanceOverlayShowGpu : cfg.PerformanceOverlayShowNpu,
            gpu ? cfg.PerformanceOverlayGpuShowUtilization : cfg.PerformanceOverlayNpuShowUtilization,
            HardwareUtilizationFormat(gpu ? cfg.PerformanceOverlayGpuUtilizationFormat : cfg.PerformanceOverlayNpuUtilizationFormat),
            (gpu ? cfg.PerformanceOverlayGpuShowDedicatedMemory : cfg.PerformanceOverlayNpuShowDedicatedMemory) ?? cfg.PerformanceOverlayShowDeviceMemory,
            HardwareMemoryUnit(gpu ? cfg.PerformanceOverlayGpuDedicatedMemoryUnit : cfg.PerformanceOverlayNpuDedicatedMemoryUnit, cfg),
            (gpu ? cfg.PerformanceOverlayGpuShowSharedMemory : cfg.PerformanceOverlayNpuShowSharedMemory) ?? cfg.PerformanceOverlayShowDeviceMemory,
            HardwareMemoryUnit(gpu ? cfg.PerformanceOverlayGpuSharedMemoryUnit : cfg.PerformanceOverlayNpuSharedMemoryUnit, cfg));
    }

    public static string FormatHardwareUtilization(double? percent, string? format = "Percent")
    {
        if (percent is not { } value || !double.IsFinite(value) || value < 0 || value > 100) return "暂不可用";
        return HardwareUtilizationFormat(format) switch
        {
            "Ratio" => (value / 100).ToString("0.000", CultureInfo.CurrentCulture) + "（0–1）",
            "PercentInteger" => value.ToString("0", CultureInfo.CurrentCulture) + "%",
            _ => value.ToString("0.0", CultureInfo.CurrentCulture) + "%"
        };
    }
    public static string FormatHardwareMemory(long? bytes, string? unit = "Auto")
    {
        if (bytes is null or < 0) return "暂不可用";
        var chosen = unit is "B" or "KiB" or "MiB" or "GiB" ? unit : bytes.Value switch
        { >= 1073741824 => "GiB", >= 1048576 => "MiB", >= 1024 => "KiB", _ => "B" };
        var divisor = chosen switch { "KiB" => 1024d, "MiB" => 1048576d, "GiB" => 1073741824d, _ => 1d };
        return (chosen == "B" ? bytes.Value.ToString("0", CultureInfo.CurrentCulture) : (bytes.Value / divisor).ToString("0.00", CultureInfo.CurrentCulture)) + " " + chosen;
    }

    public static double FiniteClamp(double value, double minimum, double maximum, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    public static string FormatMemory(long? bytes, string unit = "Auto")
    {
        if (bytes is null or < 0) return "暂不可用";
        var mb = bytes.Value / 1048576d;
        return unit == "GB" || (unit != "MB" && mb >= 1024)
            ? (mb / 1024).ToString("0.00", CultureInfo.CurrentCulture) + " GB"
            : mb.ToString("0", CultureInfo.CurrentCulture) + " MB";
    }

    public static string FormatCpu(double? percent)
        => percent is { } value && double.IsFinite(value) && value is >= 0 and <= 100
            ? value.ToString("0.0", CultureInfo.CurrentCulture) + "%" : "采样中 / 不可用";

    public static IReadOnlyList<PerformanceGameSample> SelectGames(PerformanceSnapshot snapshot, AppConfig cfg)
    {
        var games = cfg.PerformanceOverlayAllInstances ? snapshot.Games : snapshot.Games
            .Where(game => string.Equals(game.InstanceId, cfg.PerformanceOverlayInstanceId, StringComparison.Ordinal));
        // 未知读数排在后面；相同读数使用名称和 PID 保持稳定，不影响已选实例的身份。
        var ordered = GameSort(cfg) switch
        {
            "Cpu" => games.OrderByDescending(game => game.CpuPercent is { } cpu && double.IsFinite(cpu) ? cpu : -1),
            "Memory" => games.OrderByDescending(game => game.WorkingSetBytes ?? -1),
            _ => games.OrderBy(game => game.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        };
        return ordered.ThenBy(game => game.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(game => game.ProcessId).ToArray();
    }
}
