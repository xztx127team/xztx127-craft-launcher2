using System.Globalization;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>Pure input policies; values are validated before UI or persistence changes.</summary>
public static class InputExperiencePolicy
{
    public static bool TryParseMemoryPercent(string? text, out double percent)
        => (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out percent) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out percent)) &&
            double.IsFinite(percent) && percent >= 0.1 && percent <= 100;

    public static bool TryValidateMemoryPercentRange(string? minimumText, string? maximumText, out double minimum, out double maximum)
    {
        var validMinimum = TryParseMemoryPercent(minimumText, out minimum);
        var validMaximum = TryParseMemoryPercent(maximumText, out maximum);
        return validMinimum && validMaximum && minimum <= maximum;
    }

    public static bool TryParseMemoryMb(string? text, bool unitsEnabled, out int mb)
    {
        mb = 0;
        var input = (text ?? "").Trim();
        if (!unitsEnabled) return int.TryParse(input, NumberStyles.Integer, CultureInfo.CurrentCulture, out mb) && mb > 0;
        var match = Regex.Match(input, @"^(?<number>\d+(?:[.,]\d+)?)\s*(?<unit>MB|GB|M|G)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        var numeric = match.Groups["number"].Value;
        if (!decimal.TryParse(numeric, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out var value) &&
            !decimal.TryParse(numeric, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return false;
        if (value > int.MaxValue) return false;
        var unit = match.Groups["unit"].Value;
        if (unit.StartsWith("G", StringComparison.OrdinalIgnoreCase)) value *= 1024;
        if (value < 1 || value > int.MaxValue || decimal.Truncate(value) != value) return false;
        mb = (int)value;
        return true;
    }

    public static int FirstInvalidFileNameIndex(string? text)
    {
        if (string.IsNullOrEmpty(text)) return -1;
        const string invalid = "<>:\"/\\|?*";
        for (var i = 0; i < text.Length; i++) if (text[i] < 32 || invalid.Contains(text[i])) return i;
        if (text.EndsWith(' ') || text.EndsWith('.')) return text.Length - 1;
        var stem = text.Split('.')[0];
        if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return 0;
        return -1;
    }

    public static string JoinPastedLines(string text)
        => string.Join(" ", text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => line.Trim()));
}
