using System.IO;

namespace XCL2.App.Services;

public enum ModManagementState { Loading, SelectionRequired, Failed, Empty, FilterEmpty, Ready }

/// <summary>Shared decisions for the installed-mod list and its empty states.</summary>
public static class ModManagementPresentation
{
    public static (string MinecraftVersion, string Loader) DownloadFilters(string? minecraftVersion, string? loader)
    {
        // The scanned instance already resolved its base version. Keep genuine
        // legacy identifiers such as b1.7.3 instead of dropping their filter.
        var normalizedLoader = loader?.Trim().ToLowerInvariant() ?? "";
        return (minecraftVersion?.Trim() ?? "", normalizedLoader is "fabric" or "forge" or "neoforge" or "quilt"
            ? normalizedLoader : "");
    }

    public static ModManagementState Resolve(bool loading, bool hasSelection, bool failed, int installedCount, int visibleCount)
    {
        if (loading) return ModManagementState.Loading;
        if (failed) return ModManagementState.Failed;
        if (!hasSelection) return ModManagementState.SelectionRequired;
        if (installedCount == 0) return ModManagementState.Empty;
        return visibleCount == 0 ? ModManagementState.FilterEmpty : ModManagementState.Ready;
    }

    // Installation and management must use the same game directory as launch.
    public static string GameDirectory(string minecraftDirectory, string? versionId, bool isolateByDefault,
        IReadOnlyDictionary<string, bool> isolationOverrides)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return minecraftDirectory;
        var isolated = InstanceIsolationPolicy.Resolve(minecraftDirectory, versionId, isolateByDefault, isolationOverrides);
        return isolated ? Path.Combine(minecraftDirectory, "versions", versionId) : minecraftDirectory;
    }

    /// <summary>
    /// Watch the closest existing ancestor without creating folders on a read.
    /// This also catches the very first mods folder being copied into an instance.
    /// </summary>
    public static string? WatchDirectory(string modsDirectory)
    {
        var candidate = modsDirectory;
        while (!Directory.Exists(candidate))
        {
            var parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase)) return null;
            candidate = parent;
        }
        return candidate;
    }

    public static bool AffectsMods(string changedPath, string modsDirectory)
    {
        var path = Path.GetFullPath(changedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var mods = Path.GetFullPath(modsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(path, mods, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(mods + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || mods.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
