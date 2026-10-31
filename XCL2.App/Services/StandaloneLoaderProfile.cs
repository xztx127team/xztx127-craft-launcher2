using System.IO;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>Flatten loader profiles only after inheriting the entire vanilla launch definition.</summary>
internal static class StandaloneLoaderProfile
{
    internal sealed record RepairTarget(string MinecraftVersion, string Loader, string LoaderVersion);

    internal static VersionDetail Compose(string minecraftDirectory, string versionId, string minecraftVersion,
        string loader, string loaderVersion, VersionDetail vanilla, VersionDetail profile)
    {
        VersionInheritanceResolver.ValidateId(versionId);
        VersionInheritanceResolver.ValidateId(minecraftVersion);
        if (vanilla.Id != minecraftVersion || !string.IsNullOrWhiteSpace(vanilla.InheritsFrom) ||
            !string.IsNullOrWhiteSpace(vanilla.Jar) || vanilla.Downloads == null ||
            !vanilla.Downloads.TryGetValue("client", out var client) || string.IsNullOrWhiteSpace(client.Url))
            throw new InvalidDataException("原版版本信息不完整或版本不匹配，无法生成完整的加载器实例。");
        if (!string.IsNullOrWhiteSpace(profile.InheritsFrom) && profile.InheritsFrom != minecraftVersion)
            throw new InvalidDataException("加载器声明的原版父版本与所选 Minecraft 版本不一致。");
        if (!string.IsNullOrWhiteSpace(profile.Jar) && profile.Jar != minecraftVersion)
            throw new InvalidDataException("加载器引用了不匹配的客户端主程序。");
        if (string.IsNullOrWhiteSpace(profile.MainClass))
            throw new InvalidDataException("加载器版本信息缺少启动主类。");

        // Clone so repair never modifies the parsed on-disk profile before all downloads succeed.
        var child = JsonSerializer.Deserialize<VersionDetail>(JsonSerializer.Serialize(profile))!;
        child.Jar = null;
        child.Libraries ??= new();
        var layers = new[]
        {
            new VersionInheritanceResolver.Layer(minecraftVersion, "", vanilla),
            new VersionInheritanceResolver.Layer(versionId, "", child)
        };
        var result = VersionInheritanceResolver.Merge(minecraftDirectory, layers, (_, _) => null,
            preservePlatformVariants: true).Effective;
        result.Id = versionId;
        result.InheritsFrom = null;
        result.Jar = null;
        // These belong to Minecraft, not to the loader; refresh copied fields in older broken profiles.
        result.Downloads = vanilla.Downloads;
        result.AssetIndex = vanilla.AssetIndex;
        result.Assets = vanilla.Assets;
        result.Logging = vanilla.Logging;
        result.ClientVersion = vanilla.ClientVersion;
        result.JavaVersion = (child.JavaVersion?.MajorVersion ?? 0) > (vanilla.JavaVersion?.MajorVersion ?? 0)
            ? child.JavaVersion : vanilla.JavaVersion;
        result.XclMinecraftVersion = minecraftVersion;
        result.XclComponentLoader = loader == "Legacy Fabric" ? "LegacyFabric" : loader;
        result.XclComponentLoaderVersion = loaderVersion;
        return result;
    }

    internal static VersionRepairPlan CreateRepairPlan(string minecraftDirectory, VersionDetail profile, string? architecture = null)
    {
        var layers = new[] { new VersionInheritanceResolver.Layer(profile.Id, "", profile) };
        return VersionRepairPlan.Create(VersionInheritanceResolver.Merge(minecraftDirectory, layers, (_, _) => null, architecture));
    }

    // Detect the old XCL standalone-install failure narrowly. Normal inherited and complete
    // custom profiles remain untouched, and startup needs no network request for those profiles.
    internal static RepairTarget? GetRepairTarget(VersionDetail profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.InheritsFrom) || !string.IsNullOrWhiteSpace(profile.Jar)) return null;
        var libraries = profile.Libraries ?? new();
        if (libraries.Any(library => library.Name.StartsWith("org.lwjgl:", StringComparison.Ordinal) ||
            library.Name.StartsWith("org.lwjgl.lwjgl:", StringComparison.Ordinal) ||
            library.Name.StartsWith("net.sf.jopt-simple:", StringComparison.Ordinal))) return null;
        var loader = libraries.Select(library => library.Name.Split(':')).FirstOrDefault(parts =>
            parts.Length >= 3 && (parts[0] == "net.fabricmc" && parts[1] == "fabric-loader" ||
                parts[0] == "org.quiltmc" && parts[1] == "quilt-loader" ||
                parts[0] == "net.legacyfabric" && parts[1] == "fabric-loader"));
        if (loader == null) return null;
        var legacy = libraries.Any(library => library.Name.StartsWith("net.legacyfabric:", StringComparison.Ordinal));
        var loaderName = loader[0] == "org.quiltmc" ? "Quilt" : legacy ? "Legacy Fabric" : "Fabric";
        var minecraftVersion = new[] { profile.XclMinecraftVersion, profile.ClientVersion }
            .Concat(libraries.Select(library => library.Name.Split(':'))
                .Where(parts => parts.Length >= 3 && parts[1] == "intermediary" &&
                    parts[0] is "net.fabricmc" or "net.legacyfabric" or "org.quiltmc")
                .Select(parts => parts[2]))
            .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version));
        if (string.IsNullOrWhiteSpace(minecraftVersion)) return null;
        VersionInheritanceResolver.ValidateId(minecraftVersion);
        return new RepairTarget(minecraftVersion, loaderName, loader[2]);
    }
}
