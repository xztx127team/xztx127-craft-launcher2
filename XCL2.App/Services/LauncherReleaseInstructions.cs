using System.Globalization;

namespace XCL2.App.Services;

/// <summary>Only directives explicitly enabled by a release's setup.ini change update behavior.</summary>
internal sealed record LauncherReleaseInstructions(bool Minor = false, bool Required = false, DateOnly? Deadline = null)
{
    internal static LauncherReleaseInstructions Default { get; } = new();
    internal bool IsRequired(DateOnly today) => Required || (Deadline.HasValue && today > Deadline.Value);
    internal bool HasRequirement => Required || Deadline.HasValue;

    internal static LauncherReleaseInstructions Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16384) return Default;
        var minor = false;
        var required = false;
        DateOnly? deadline = null;
        foreach (var raw in text.TrimStart('\uFEFF').Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';' or '[') continue;
            var separator = line.IndexOf('=');
            if (separator < 1) continue;
            var key = line[..separator].Trim().ToLowerInvariant();
            var value = line[(separator + 1)..].Trim();
            switch (key)
            {
                case "b": minor = value.Equals("on", StringComparison.OrdinalIgnoreCase); break;
                case "c": required = value.Equals("on", StringComparison.OrdinalIgnoreCase); break;
                case "d":
                    var parts = value.Split('=', 2, StringSplitOptions.TrimEntries);
                    deadline = parts.Length == 2 && parts[0].Equals("on", StringComparison.OrdinalIgnoreCase)
                        && DateOnly.TryParseExact(parts[1], new[] { "yyyy/MM/dd", "yyyy/M/d", "yyyy/MM/d", "yyyy/M/dd" },
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
                    break;
            }
        }
        return new(minor, required, deadline);
    }
}

internal enum LauncherReminderMode { Normal, Snooze, Paused, BadgeOnly }

internal sealed record LauncherReminderPreference(LauncherReminderMode Mode = LauncherReminderMode.Normal,
    DateTimeOffset? StartsAt = null, DateTimeOffset? EndsAt = null)
{
    internal LauncherReminderMode EffectiveMode(DateTimeOffset now)
    {
        if (StartsAt.HasValue && now < StartsAt.Value || EndsAt.HasValue && now >= EndsAt.Value)
            return LauncherReminderMode.Normal;
        return Enum.IsDefined(Mode) ? Mode : LauncherReminderMode.Normal;
    }

    internal bool SuppressAutomaticReminder(DateTimeOffset now, LauncherReleaseInstructions instructions, DateOnly today)
        => !instructions.IsRequired(today) && EffectiveMode(now) is LauncherReminderMode.Snooze or LauncherReminderMode.Paused;
}
