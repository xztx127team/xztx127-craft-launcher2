using XCL2.App.Models;

namespace XCL2.App.Services;

internal static class JavaRegistrationRefreshPolicy
{
    /// <summary>Keep stable IDs/custom names, but replace stale probe metadata on refresh.</summary>
    internal static int RefreshExisting(IEnumerable<InstalledJava> registered,
        IEnumerable<(string Path, string? Version)> detected, Func<string, bool> fileExists)
    {
        var versions = detected.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => JavaVersionOutputPolicy.ParseMajorValue(group.Last().Version),
                StringComparer.OrdinalIgnoreCase);
        var changed = 0;
        foreach (var entry in registered)
        {
            // Preserve references to removable/offline runtimes; the UI marks them
            // missing rather than silently removing an instance's selected Java ID.
            var exists = fileExists(entry.JavawPath);
            if (exists && !versions.ContainsKey(entry.JavawPath)) continue;
            var major = exists ? versions[entry.JavawPath] : null;
            if (entry.MajorVersion == major) continue;
            var defaultName = entry.MajorVersion is > 0 ? $"Java {entry.MajorVersion}" : "Java (版本未知)";
            if (entry.Name == defaultName)
                entry.Name = major is > 0 ? $"Java {major}" : "Java (版本未知)";
            entry.MajorVersion = major;
            changed++;
        }
        return changed;
    }
}
