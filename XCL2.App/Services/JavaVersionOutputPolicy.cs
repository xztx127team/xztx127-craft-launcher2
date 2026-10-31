using System.Globalization;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

internal static class JavaVersionOutputPolicy
{
    internal static string? ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = Regex.Match(output,
            "(?:openjdk|java)\\s+version\\s+\"(?<version>[0-9][^\"]*)\"|(?:openjdk|java)\\s+(?<version>[0-9][0-9._+\\-]*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return match.Success ? match.Groups["version"].Value : null;
    }

    internal static int? ParseMajor(string? output)
    {
        return ParseMajorValue(ParseVersion(output));
    }

    // Callers which already extracted the version must not feed a quoted value
    // back into the process-output parser, which intentionally requires a Java banner.
    internal static int? ParseMajorValue(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || !Regex.IsMatch(version,
                @"^[0-9][0-9A-Za-z._+\-]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)) return null;
        var parts = version.Split('.', '_', '-', '+');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)) return null;
        return first == 1 && parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var second)
            ? second : first;
    }
}
