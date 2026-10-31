using System.Globalization;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>Windows 的 GPU Engine 实例包含 PID、LUID 与引擎编号；不凭 Compute 字样把 GPU 当作 NPU。</summary>
public static class PerformanceHardwareMath
{
    private static readonly Regex Engine = new(@"luid_0x(?<high>[0-9a-f]+)_0x(?<low>[0-9a-f]+)_phys_(?<physical>\d+)_eng_(?<engine>\d+)_engtype_", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Adapter = new(@"luid_0x(?<high>[0-9a-f]+)_0x(?<low>[0-9a-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Luid(uint high, uint low) => $"{high:x8}{low:x8}";
    public static string? AdapterId(string instance)
    {
        var match = Adapter.Match(instance);
        return match.Success && uint.TryParse(match.Groups["high"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var high)
            && uint.TryParse(match.Groups["low"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low)
            ? Luid(high, low) : null;
    }

    public static double? BusiestEngine(IEnumerable<KeyValuePair<string, double>> samples, string adapterId)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples)
        {
            if (!double.IsFinite(sample.Value) || sample.Value < 0 || AdapterId(sample.Key) != adapterId) continue;
            var match = Engine.Match(sample.Key);
            if (!match.Success) continue;
            var engine = match.Groups["physical"].Value + ":" + match.Groups["engine"].Value;
            values[engine] = values.GetValueOrDefault(engine) + sample.Value;
        }
        return values.Count == 0 ? null : Math.Clamp(values.Values.Max(), 0, 100);
    }

    public static long? AdapterMemory(IEnumerable<KeyValuePair<string, double>> samples, string adapterId)
    {
        var values = samples.Where(item => AdapterId(item.Key) == adapterId && double.IsFinite(item.Value) && item.Value >= 0).Select(item => item.Value).ToArray();
        if (values.Length == 0) return null;
        var total = values.Sum();
        return total <= long.MaxValue ? (long)total : null;
    }
}
