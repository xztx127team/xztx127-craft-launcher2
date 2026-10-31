using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>A normalized launcher version; a final release sorts after every RC of the same core.</summary>
internal readonly record struct LauncherReleaseVersion(Version Core, int? ReleaseCandidate, bool OfficialPrerelease = false, string? PreviewIdentifier = null) : IComparable<LauncherReleaseVersion>
{
    internal bool IsPreview => OfficialPrerelease || ReleaseCandidate.HasValue || PreviewIdentifier != null;
    public int CompareTo(LauncherReleaseVersion other)
    {
        var core = Core.CompareTo(other.Core);
        if (core != 0) return core;
        if (IsPreview != other.IsPreview) return IsPreview ? -1 : 1;
        if (!IsPreview) return 0;
        return ComparePreviewIdentifiers(ReleaseCandidate.HasValue ? "rc." + ReleaseCandidate : PreviewIdentifier ?? "",
            other.ReleaseCandidate.HasValue ? "rc." + other.ReleaseCandidate : other.PreviewIdentifier ?? "");
    }

    private static int ComparePreviewIdentifiers(string left, string right)
    {
        var a = Regex.Matches(left, @"[0-9]+|[A-Za-z]+").Select(match => match.Value).ToArray();
        var b = Regex.Matches(right, @"[0-9]+|[A-Za-z]+").Select(match => match.Value).ToArray();
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumber = char.IsDigit(a[i][0]); var bNumber = char.IsDigit(b[i][0]);
            int comparison;
            if (aNumber && bNumber)
            {
                var x = a[i].TrimStart('0'); var y = b[i].TrimStart('0');
                comparison = x.Length.CompareTo(y.Length);
                if (comparison == 0) comparison = string.CompareOrdinal(x, y);
            }
            else if (aNumber != bNumber) comparison = aNumber ? -1 : 1;
            else comparison = string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase);
            if (comparison != 0) return comparison;
        }
        return a.Length.CompareTo(b.Length);
    }
    public override string ToString()
        => (Core.Revision > 0 ? Core.ToString(4) : Core.ToString(3)) +
           (ReleaseCandidate.HasValue ? "-rc" + ReleaseCandidate.Value : PreviewIdentifier != null ? "-" + PreviewIdentifier : OfficialPrerelease ? "（快照版）" : "");
}

/// <summary>启动器更新的纯决策逻辑，不访问网络、配置文件或界面。</summary>
internal static class LauncherUpdatePolicy
{
    internal static bool ShouldCheckOnStartup(bool checkOnStartup) => checkOnStartup;

    internal static bool ShouldCheckOnStartup(bool checkOnStartup, bool previewProgram, bool skipThisStartup)
        => !skipThisStartup && (checkOnStartup || previewProgram);

    internal static bool CanInstall(bool updatesEnabled, bool userConfirmed)
        => updatesEnabled && userConfirmed;

    internal static bool ShouldPromptForStableUpdate(bool automatic, LauncherReminderMode mode,
        DateTimeOffset? snoozedUntil, DateTimeOffset now)
        => !automatic || mode == LauncherReminderMode.Normal && (!snoozedUntil.HasValue || snoozedUntil.Value <= now);

    internal static Version? ExtractVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var match = Regex.Match(tag, @"\d+(?:\.\d+){1,3}");
        return match.Success && Version.TryParse(match.Value, out var version) ? Normalize(version) : null;
    }

    internal static bool IsNewer(Version remote, Version local) => Normalize(remote) > Normalize(local);

    internal static LauncherReleaseVersion? ParseReleaseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        // Build metadata does not affect precedence. RC digits are numeric, not a string comparison.
        var value = tag.Trim().Split('+', 2)[0];
        var match = Regex.Match(value,
            @"^[vV]?(?<core>\d+(?:\.\d+){1,3})(?:(?:-rc[.-]?(?<rc>\d+))|(?:-rc\((?<rc>\d+)\))|(?:-rc（(?<rc>\d+)）))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success && Version.TryParse(match.Groups["core"].Value, out var core))
        {
            int? candidate = null;
            if (match.Groups["rc"].Success)
            {
                if (!int.TryParse(match.Groups["rc"].Value, out var number)) return null;
                candidate = number;
            }
            return new(Normalize(core), candidate);
        }
        // Historical stable tags used -fixN. Do not turn malformed/unknown prerelease tags into finals.
        var fix = Regex.Match(value, @"^[vV]?(?<core>\d+(?:\.\d+){1,3})-fix\d+$", RegexOptions.IgnoreCase);
        return fix.Success && Version.TryParse(fix.Groups["core"].Value, out var fixedCore)
            ? new LauncherReleaseVersion(Normalize(fixedCore), null) : null;
    }

    internal static bool IsNewer(LauncherReleaseVersion remote, LauncherReleaseVersion local) => remote.CompareTo(local) > 0;

    internal static bool IsSuppressedPreviewTag(string tag, IEnumerable<string>? suppressed)
        => suppressed?.Any(value => string.Equals(value?.Trim(), tag.Trim(), StringComparison.OrdinalIgnoreCase)) == true;

    internal static LauncherReleaseVersion? ParseReleaseVersion(string? tag, bool githubPrerelease)
    {
        var parsed = ParseReleaseVersion(tag);
        if (parsed.HasValue) return parsed.Value with { OfficialPrerelease = githubPrerelease };
        // Unknown suffixes are previews only when GitHub explicitly marks the release as a prerelease.
        if (!githubPrerelease || string.IsNullOrWhiteSpace(tag)) return null;
        var match = Regex.Match(tag.Trim(), @"^[vV]?(?<core>\d+(?:\.\d+){1,3})-(?<preview>[0-9A-Za-z._-]+)(?:\+[0-9A-Za-z._-]+)?$",
            RegexOptions.CultureInvariant);
        return match.Success && Version.TryParse(match.Groups["core"].Value, out var core)
            ? new LauncherReleaseVersion(Normalize(core), null, true, match.Groups["preview"].Value) : null;
    }

    internal static string? PickReleaseTag(IEnumerable<(string Tag, bool Prerelease, bool Draft)> releases,
        bool includePreviews, bool previewOnly = false)
    {
        if (previewOnly && !includePreviews) return null;
        var candidates = releases.Where(release => !release.Draft)
            .Select(release => (release.Tag, Version: ParseReleaseVersion(release.Tag, release.Prerelease)))
            .Where(item => item.Version.HasValue).ToArray();
        var latestFinal = candidates.Where(item => !item.Version!.Value.IsPreview)
            .Select(item => item.Version).OrderByDescending(version => version).FirstOrDefault();
        return candidates.Where(item => previewOnly
                ? item.Version!.Value.IsPreview && (!latestFinal.HasValue || IsNewer(item.Version.Value, latestFinal.Value))
                : includePreviews || !item.Version!.Value.IsPreview)
            .OrderByDescending(item => item.Version!.Value).Select(item => item.Tag).FirstOrDefault();
    }

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
