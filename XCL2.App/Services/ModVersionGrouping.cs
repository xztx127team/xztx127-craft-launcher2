using System.Linq;
using XCL2.App.Models;

namespace XCL2.App.Services;

public static class ModVersionGrouping
{
    private static readonly Dictionary<string, string> LoaderDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fabric"] = "Fabric",
        ["forge"] = "Forge",
        ["neoforge"] = "NeoForge",
        ["quilt"] = "Quilt",
        ["liteloader"] = "LiteLoader",
        ["bukkit"] = "Bukkit",
        ["spigot"] = "Spigot",
        ["paper"] = "Paper",
        ["purpur"] = "Purpur",
        ["folia"] = "Folia",
        ["velocity"] = "Velocity",
        ["bungeecord"] = "BungeeCord",
        ["waterfall"] = "Waterfall",
        ["sponge"] = "Sponge",
        ["datapack"] = "数据包",
        ["minecraft"] = "通用", // InlineVersionEntry 构造函数里给材质包/光影包等无 loader 概念的项目用的占位值
        ["curseforge"] = "CurseForge 文件", 
    };

    // 先按游戏版本倒序，再按加载器名称排序；同一文件会出现在全部受支持版本下。
    public static readonly IComparer<string> GameVersionComparer = Comparer<string>.Create(CompareVersions);

    // 数字分段排序，保证 1.21.10 排在 1.21.9 前；同号正式版排在预览版前。
    private static int CompareVersions(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return 0;
        if (left == null) return -1;
        if (right == null) return 1;
        var a = System.Text.RegularExpressions.Regex.Matches(left, @"\d+|[^\d]+");
        var b = System.Text.RegularExpressions.Regex.Matches(right, @"\d+|[^\d]+");
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            int result;
            if (long.TryParse(a[i].Value, out var av) && long.TryParse(b[i].Value, out var bv))
                result = av.CompareTo(bv);
            else
                result = StringComparer.OrdinalIgnoreCase.Compare(a[i].Value, b[i].Value);
            if (result != 0) return result;
        }
        if (a.Count == b.Count) return 0;
        var longer = a.Count > b.Count ? left : right;
        var shorter = a.Count > b.Count ? right : left;
        var suffix = longer[shorter.Length..];
        var prerelease = suffix.StartsWith("-") || suffix.StartsWith(" ");
        var resultForLonger = prerelease ? -1 : 1;
        return a.Count > b.Count ? resultForLonger : -resultForLonger;
    }

    public static List<VersionGroup> Group(IEnumerable<InlineVersionEntry> flatEntries, bool includePreview = true)
    {
        var buckets = new Dictionary<(string loader, string gameVersion), List<InlineVersionEntry>>();
        foreach (var entry in flatEntries)
        {
            if (!includePreview && entry.IsPreview) continue;
            var versions = entry.GameVersions.Count == 0 ? new List<string> { "未知版本" } : entry.GameVersions;
            foreach (var gameVersion in versions.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var loader in entry.Loaders.DefaultIfEmpty("minecraft").Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = (loader.ToLowerInvariant(), gameVersion);
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<InlineVersionEntry>();
                if (!list.Contains(entry)) list.Add(entry);
            }
        }
        return buckets.OrderByDescending(pair => pair.Key.gameVersion, GameVersionComparer)
            .ThenBy(pair => LoaderDisplayNames.TryGetValue(pair.Key.loader, out var name) ? name : pair.Key.loader,
                StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                var loader = LoaderDisplayNames.TryGetValue(pair.Key.loader, out var name) ? name : pair.Key.loader;
                return new VersionGroup($"{loader} {pair.Key.gameVersion}", pair.Value, pair.Key.gameVersion, loader);
            }).ToList();
    }
}
