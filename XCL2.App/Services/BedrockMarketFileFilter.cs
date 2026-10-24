using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>仅筛选已经从上游读取的文件，不推断未标注的游戏兼容版本。</summary>
public static class BedrockMarketFileFilter
{
    public static List<BedrockMarketFile> Apply(IEnumerable<BedrockMarketFile> files,
        string source = "all", string fileType = "all", string gameVersion = "", string sort = "newest")
    {
        var version = gameVersion.Trim();
        var filtered = files.Where(file =>
            (source is "" or "all" || file.Source.Equals(source, StringComparison.OrdinalIgnoreCase)) &&
            (fileType is "" or "all" || file.Name.EndsWith("." + fileType, StringComparison.OrdinalIgnoreCase)) &&
            (version.Length == 0 || MatchesGameVersion(file.GameVersions, version)));

        return sort switch
        {
            "name" => filtered.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(file => file.PublishedAt).ToList(),
            "size" => filtered.OrderByDescending(file => file.Size)
                .ThenByDescending(file => file.PublishedAt)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => filtered.OrderByDescending(file => file.PublishedAt)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static bool MatchesGameVersion(string declaredVersions, string query)
    {
        if (string.IsNullOrWhiteSpace(declaredVersions)) return false;
        // 1.21 可匹配 1.21.50，1.2 不会误匹配 1.20；未知版本不会进入结果。
        return declaredVersions.Split(new[] { ',', ';', '|', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(version => version.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                version.StartsWith(query + ".", StringComparison.OrdinalIgnoreCase) ||
                version.StartsWith(query + "-", StringComparison.OrdinalIgnoreCase));
    }
}
