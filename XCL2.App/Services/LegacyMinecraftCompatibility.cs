using System.Globalization;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>旧客户端的身份参数是位置参数，不能在它们前面拼上现代客户端的通用参数。</summary>
internal static class LegacyMinecraftCompatibility
{
    internal static bool UsesPositionalIdentity(string? argumentTemplate)
        => !string.IsNullOrWhiteSpace(argumentTemplate) && Regex.IsMatch(argumentTemplate,
            @"^\s*\$\{auth_player_name\}\s+\$\{auth_session\}(?:\s|$)",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static string BuildAuthSession(string uuid, string? accessToken, bool authenticated)
        => authenticated && !string.IsNullOrWhiteSpace(accessToken) && accessToken != "0" && !string.IsNullOrWhiteSpace(uuid)
            ? $"token:{accessToken}:{uuid.Replace("-", "")}" : "0";

    /// <summary>仅在元数据没有 javaVersion 时使用；快照等无法确定的编号不瞎猜。</summary>
    internal static int? EstimateJavaMajor(string? minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion)) return null;
        var match = Regex.Match(minecraftVersion, @"^(?<major>1|2[6-9]|[3-9]\d)\.(?<minor>\d+)(?:\.(?<patch>\d+))?(?:$|[-_ ])",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) return null;
        var patch = int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : 0;
        if (major >= 26 || minor > 26 || minor == 26 && patch >= 1) return 25;
        if (minor > 20 || minor == 20 && patch >= 5) return 21;
        if (minor >= 18) return 17;
        if (minor == 17) return 16;
        return 8;
    }
}
