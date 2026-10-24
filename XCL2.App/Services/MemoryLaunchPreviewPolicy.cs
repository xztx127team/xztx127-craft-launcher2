using System.Globalization;

namespace XCL2.App.Services;

/// <summary>
/// 在启动器计算的堆内存上应用自定义 JVM 参数。调用方使用 LauncherService 的同一分词器；
/// 参数按命令行顺序处理，后面的 -Xms/-Xmx（及 HotSpot 别名）覆盖前面的值。
/// </summary>
public static class MemoryLaunchPreviewPolicy
{
    private const long Mb = 1024L * 1024;

    public sealed record Preview(long InitialBytes, long MaximumBytes, bool HasOverride,
        string? Error, string? Uncertainty)
    {
        public string InitialMbText => (InitialBytes / (decimal)Mb).ToString("0.##", CultureInfo.CurrentCulture);
        public string MaximumMbText => (MaximumBytes / (decimal)Mb).ToString("0.##", CultureInfo.CurrentCulture);
    }

    public static Preview Apply(MemoryOptimizerService.LaunchMemory allocation, IReadOnlyList<string> arguments)
    {
        var initial = allocation.MinMemoryMb * Mb;
        var maximum = allocation.MaxMemoryMb * Mb;
        var overridden = false;
        string? error = null, uncertainty = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            // 模块等参数的独立取值不是另一个 JVM 选项。
            if (argument is "--add-opens" or "--add-exports" or "--add-reads" or "--add-modules" or
                "--module-path" or "-p" or "--upgrade-module-path" or "--limit-modules" or
                "--patch-module" or "--enable-native-access" or "-cp" or "-classpath" or "--class-path")
            {
                i++;
                continue;
            }
            if (argument.StartsWith('@'))
            {
                uncertainty = "JVM 参数包含 @参数文件，文件中的堆内存覆盖未计入预览。";
                continue;
            }

            string? value = null;
            var isInitial = false;
            if (argument.StartsWith("-Xms", StringComparison.Ordinal))
            { value = argument[4..]; isInitial = true; }
            else if (argument.StartsWith("-Xmx", StringComparison.Ordinal))
                value = argument[4..];
            else if (argument.StartsWith("-XX:InitialHeapSize=", StringComparison.Ordinal))
            { value = argument["-XX:InitialHeapSize=".Length..]; isInitial = true; }
            else if (argument.StartsWith("-XX:MaxHeapSize=", StringComparison.Ordinal))
                value = argument["-XX:MaxHeapSize=".Length..];
            if (value == null) continue;

            if (!TryParseBytes(value, out var bytes))
            {
                error = $"内存参数「{argument}」无效，请填写整数和可选 K/M/G 单位。";
                continue;
            }
            if (bytes < Mb)
                error = $"内存参数「{argument}」小于 1 MB，请检查是否遗漏 M/G 单位。";
            if (isInitial) initial = bytes;
            else maximum = bytes;
            overridden = true;
        }

        if (initial <= 0 || maximum <= 0)
            error ??= "初始内存和最大内存必须大于 0。";
        if (initial > maximum)
            error ??= "初始内存大于最大内存，当前配置无法启动，请调整内存或自定义 JVM 参数。";
        return new Preview(initial, maximum, overridden, error, uncertainty);
    }

    private static bool TryParseBytes(string text, out long bytes)
    {
        bytes = 0;
        if (text.Length == 0) return false;
        var suffix = char.ToUpperInvariant(text[^1]);
        long multiplier = suffix switch { 'K' => 1024, 'M' => Mb, 'G' => 1024 * Mb, _ => 1 };
        var digits = multiplier == 1 ? text : text[..^1];
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value > long.MaxValue / multiplier)
            return false;
        bytes = value * multiplier;
        return true;
    }
}
