using System.Globalization;

namespace XCL2.App.Services.Worlds;

/// <summary>World and settings search share whitespace-token matching; every token may match a different field.</summary>
internal static class WorldSearch
{
    public static bool MatchesText(string? query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var text = string.Join(" ", fields);
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    // 0 = all, 1–4 = survival/creative/adventure/spectator, 5 = hardcore.
    public static bool Matches(WorldArchive world, string? query, int modeFilter = 0, string? version = null)
    {
        if (!string.IsNullOrEmpty(version) && !string.Equals(world.Settings.Version, version, StringComparison.OrdinalIgnoreCase)) return false;
        if (modeFilter is >= 1 and <= 4 && world.Settings.GameMode != modeFilter - 1) return false;
        if (modeFilter == 5 && !world.Settings.Hardcore) return false;
        var mode = world.Settings.GameMode switch { 0 => "生存 survival", 1 => "创造 creative", 2 => "冒险 adventure", 3 => "旁观 spectator", _ => "未知" };
        return MatchesText(query, world.Settings.Name, world.Path, world.Settings.Version, world.Source,
            world.Settings.Seed?.ToString(CultureInfo.InvariantCulture), mode,
            world.Settings.Hardcore ? "极限 hardcore" : "");
    }
}
