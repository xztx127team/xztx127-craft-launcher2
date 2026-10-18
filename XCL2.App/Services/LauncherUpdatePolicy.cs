using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>启动器更新的纯决策逻辑，不访问网络、配置文件或界面。</summary>
internal static class LauncherUpdatePolicy
{
    internal static bool ShouldCheckOnStartup(bool checkOnStartup) => checkOnStartup;

    internal static bool CanInstall(bool updatesEnabled, bool userConfirmed)
        => updatesEnabled && userConfirmed;

    internal static Version? ExtractVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var match = Regex.Match(tag, @"\d+(?:\.\d+){1,3}");
        return match.Success && Version.TryParse(match.Value, out var version) ? Normalize(version) : null;
    }

    internal static bool IsNewer(Version remote, Version local) => Normalize(remote) > Normalize(local);

    private static Version Normalize(Version version)
        => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    internal static string? PickAssetName(IEnumerable<string>? names, bool is64BitOperatingSystem)
    {
        var architecture = is64BitOperatingSystem ? "x64" : "x86";
        return names?.FirstOrDefault(name =>
            !string.IsNullOrWhiteSpace(name) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            name.Contains(architecture, StringComparison.OrdinalIgnoreCase));
    }
}
