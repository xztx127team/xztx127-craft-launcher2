using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>服务端资源的加载器匹配和最终落点。客户端 saves 目录规则不适用于专用服务器。</summary>
public static class ServerResourceInstallPolicy
{
    public static string? GetModLoader(ServerCoreType core) => core switch
    {
        ServerCoreType.Fabric or ServerCoreType.LegacyFabric => "fabric",
        ServerCoreType.Forge or ServerCoreType.Cleanroom => "forge",
        ServerCoreType.NeoForge => "neoforge",
        ServerCoreType.Quilt => "quilt",
        _ => null
    };

    public static bool MatchesLoader(IEnumerable<string> loaders, string loader)
        => loaders.Any(value => value.Equals(loader, StringComparison.OrdinalIgnoreCase));

    public static string GetDestination(string serverDirectory, ModrinthResourceType type, string? worldName)
    {
        var root = Path.GetFullPath(serverDirectory);
        var relative = type switch
        {
            ModrinthResourceType.Mod => "mods",
            ModrinthResourceType.Plugin => "plugins",
            ModrinthResourceType.ResourcePack => "resourcepacks",
            ModrinthResourceType.Shader => "shaderpacks",
            ModrinthResourceType.DataPack => Path.Combine(ValidateWorldName(worldName), "datapacks"),
            _ => throw new ArgumentException("不支持的服务端资源类型。")
        };
        var destination = Path.GetFullPath(Path.Combine(root, relative));
        if (!destination.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("资源目标必须位于服务器目录内。");
        return destination;
    }

    private static string ValidateWorldName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("请填写服务器目录内的有效世界文件夹名称，不能包含路径分隔符。");
        return name.Trim();
    }
}
