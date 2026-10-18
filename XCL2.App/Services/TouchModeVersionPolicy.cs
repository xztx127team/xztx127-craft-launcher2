using System.IO;

namespace XCL2.App.Services;

/// <summary>
/// 判断实例是否使用旧版触屏兼容路径。
/// 26.3-snapshot-4 起窗口和输入库由 GLFW 切换到 SDL3；此前使用 GLFW/LWJGL 兼容层。
/// </summary>
public static class TouchModeVersionPolicy
{
    public const string WarningMessage =
        "此版本将使用 GLFW/LWJGL 触屏兼容模式。26.3 第 3 快照及更早版本使用旧路径，" +
        "第 4 快照及以后使用 SDL3 路径。\n\n是否继续开启触屏模式？";

    /// <summary>读取实例 json 与目录名，判断是否属于旧版兼容路径；无法判断时返回 false（不打扰）。</summary>
    public static bool IsBelowSupported(string versionDir, string versionId)
    {
        var root = Directory.GetParent(versionDir)?.Parent?.FullName;
        return root != null ? TouchVersionPolicy.Resolve(root, versionId) == false : TouchVersionPolicy.FromVersion(versionId) == false;
    }

    /// <summary>mcVersion 为解析出的原版号；candidates 为目录名/json 里可能带 "-snapshot-N" 的原始字符串。</summary>
    public static bool IsBelowSupported(string? mcVersion, IEnumerable<string?> candidates)
    {
        var explicitVersions = candidates.Where(x => x != null &&
            (x.Contains("snapshot", StringComparison.OrdinalIgnoreCase) || x.Contains("快照") || x.Contains("T", StringComparison.OrdinalIgnoreCase)))
            .Select(TouchVersionPolicy.FromVersion).Where(x => x.HasValue).Distinct().ToArray();
        if (explicitVersions.Length == 1) return explicitVersions[0] == false;
        return TouchVersionPolicy.FromVersion(mcVersion) == false;
    }
}
