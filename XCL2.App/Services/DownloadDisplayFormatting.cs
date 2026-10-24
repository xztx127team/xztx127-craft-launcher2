using System.Globalization;
using XCL2.App.Models;

namespace XCL2.App.Services;

internal static class DownloadDisplayFormatting
{
    public static string FormatCapacity(long bytes, DownloadDisplayUnit unit,
        Func<long, string>? automaticFormatter = null, bool uniformSpacing = true)
    {
        bytes = Math.Max(0, bytes);
        if (!Enum.IsDefined(unit)) unit = DownloadDisplayUnit.Automatic;
        if (unit == DownloadDisplayUnit.Automatic && automaticFormatter != null) return automaticFormatter(bytes);
        if (unit == DownloadDisplayUnit.Automatic)
            unit = bytes >= 1073741824 ? DownloadDisplayUnit.GB : bytes >= 1048576 ? DownloadDisplayUnit.MB
                : bytes >= 1024 ? DownloadDisplayUnit.KB : DownloadDisplayUnit.B;
        var divisor = unit switch { DownloadDisplayUnit.GB => 1073741824d, DownloadDisplayUnit.MB => 1048576d,
            DownloadDisplayUnit.KB => 1024d, _ => 1d };
        var amount = bytes / divisor;
        var value = amount is > 0 and < 0.01 ? "< " + 0.01.ToString("0.00", CultureInfo.CurrentCulture)
            : amount.ToString(unit == DownloadDisplayUnit.B ? "0" : "0.##", CultureInfo.CurrentCulture);
        return value + (uniformSpacing ? "\u00A0" : " ") + unit;
    }

    public static long KiBToBytes(double value)
        => !double.IsFinite(value) || value <= 0 ? 0 : value >= long.MaxValue / 1024d ? long.MaxValue : (long)(value * 1024d);
}
