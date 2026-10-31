using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services;

public static class VersionInfoResolver
{

    private static readonly Regex SnapshotPattern =
        new(@"\b\d{2}w\d{1,2}[a-z]\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PreReleasePattern =
        new(@"\b\d{1,2}\.\d{1,2}(?:\.\d{1,2})?-(?:pre|rc)\d+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NumericVersionPattern =
        new(@"(?<![\w.])\d{1,2}\.\d{1,2}(?:\.\d{1,2})?(?![\w.])", RegexOptions.Compiled);

    // Exact old_alpha/old_beta IDs from Mojang's official version_manifest_v2.json
    // (verified 2026-10-25). Do not accept arbitrary a/b-prefixed version guesses.
    // https://piston-meta.mojang.com/mc/game/version_manifest_v2.json
    private static readonly HashSet<string> HistoricalVersionIds = new(StringComparer.Ordinal)
    {
        "b1.8.1", "b1.8", "b1.7.3", "b1.7.2", "b1.7", "b1.6.6", "b1.6.5", "b1.6.4", "b1.6.3",
        "b1.6.2", "b1.6.1", "b1.6", "b1.5_01", "b1.5", "b1.4_01", "b1.4", "b1.3_01", "b1.3b",
        "b1.2_02", "b1.2_01", "b1.2", "b1.1_02", "b1.1_01", "b1.0.2", "b1.0_01", "b1.0",
        "a1.2.6", "a1.2.5", "a1.2.4_01", "a1.2.3_04", "a1.2.3_02", "a1.2.3_01", "a1.2.3",
        "a1.2.2b", "a1.2.2a", "a1.2.1_01", "a1.2.1", "a1.2.0_02", "a1.2.0_01", "a1.2.0",
        "a1.1.2_01", "a1.1.2", "a1.1.0", "a1.0.17_04", "a1.0.17_02", "a1.0.16", "a1.0.15",
        "a1.0.14", "a1.0.11", "a1.0.5_01", "a1.0.4", "inf-20100618", "c0.30_01c", "c0.0.13a",
        "c0.0.13a_03", "c0.0.11a", "rd-161348", "rd-160052", "rd-20090515", "rd-132328", "rd-132211"
    };
    private static readonly Regex HistoricalVersionPattern = new(
        @"(?<![\w.])(?:" + string.Join("|", HistoricalVersionIds.OrderByDescending(id => id.Length).Select(Regex.Escape)) + @")(?![\w.])",
        RegexOptions.Compiled);

    /// <summary>解析结果。ModLoader 为 null 表示纯原版；ModLoaderVersion 为 null 表示
    /// 确实解析不出加载器版本号（调用方应当把它当成"未知"处理并整个省略该字段，
    /// **不要**退化成空字符串写进清单文件——空串正是导致 mrpack 无效的原因之一）。</summary>
    public sealed record Result(string McVersion, string? ModLoader, string? ModLoaderVersion);

    /// <summary>
    /// 主入口：给一个版本目录 + 该目录的版本 ID + 已反序列化好的 VersionDetail，
    /// 返回可信的版本信息。detail 传 null 时会自行尝试读取目录里的 json。
    /// </summary>
    public static Result Resolve(string versionDir, string versionId, VersionDetail? detail)
    {
        detail ??= TryLoadDetail(versionDir, versionId);

        if (!string.IsNullOrWhiteSpace(detail?.XclMinecraftVersion) &&
            detail.XclComponentLoader is "Vanilla" or "Fabric" or "LegacyFabric" or "Forge" or "NeoForge" or "Quilt" or "OptiFine")
            return new Result(detail.XclMinecraftVersion,
                detail.XclComponentLoader == "Vanilla" ? null : detail.XclComponentLoader == "LegacyFabric" ? "Legacy Fabric" : detail.XclComponentLoader,
                detail.XclComponentLoader == "Vanilla" ? null : detail.XclComponentLoaderVersion);

        var (loader, loaderVersion, mcFromLibrary) = ResolveFromLibraries(detail);

        // ---- 加载器类型兜底：库里没认出来就退回看版本 ID 里的关键字 ----
        loader ??= GuessLoaderFromId(versionId);

        // ---- 加载器版本兜底：按各家命名约定从版本 ID 反解 ----
        loaderVersion ??= GuessLoaderVersionFromId(versionId, loader);

        // ---- 原版版本号：按可靠度依次尝试 ----
        var mcVersion =
            FirstUsable(mcFromLibrary)
            ?? FirstUsable(detail?.InheritsFrom)
            ?? FirstUsable(detail?.ClientVersion)
            ?? FirstUsable(detail?.Id)
            ?? GuessMcVersionFromId(versionId, loader)
            ?? ExtractAnyVersion(versionId)
            ?? ExtractAnyVersion(detail?.Id)
            ?? versionId; // 实在解析不出来才退回 ID 本身（至少不会是 null）

        return new Result(mcVersion, loader, loaderVersion);
    }

    /// <summary>只要原版版本号的轻量重载（不需要构造 VersionDetail 的调用方用）。</summary>
    public static string ResolveMcVersion(string versionDir, string versionId)
        => Resolve(versionDir, versionId, null).McVersion;

    /// <summary>
    /// 判断一个字符串"看起来像不像一个真实的 Minecraft 版本号"。
    /// 导出整合包前用它做最后一道闸：不像的话宁可留空/报错，也不要写一个
    /// "fabric-loader-0.15.11-1.20.1" 进 dependencies.minecraft 让整个包作废。
    /// </summary>
    public static bool LooksLikeMcVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (HistoricalVersionIds.Contains(v)) return true;
        if (SnapshotPattern.IsMatch(v) && SnapshotPattern.Match(v).Value.Length == v.Length) return true;
        if (PreReleasePattern.IsMatch(v) && PreReleasePattern.Match(v).Value.Length == v.Length) return true;
        var m = NumericVersionPattern.Match(v);
        return m.Success && m.Value.Length == v.Length;
    }

    // ==================== 内部实现 ====================

    private static string? FirstUsable(string? s)
        => LooksLikeMcVersion(s) ? s!.Trim() : null;

    private static VersionDetail? TryLoadDetail(string versionDir, string versionId)
    {
        try
        {
            var path = FolderService.ResolveVersionJson(versionDir, versionId);
            if (path == null) return null;
            return JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    private static (string? Loader, string? LoaderVersion, string? McVersion) ResolveFromLibraries(VersionDetail? detail)
    {
        if (detail?.Libraries == null) return (null, null, null);

        // The Legacy Fabric API deliberately reuses net.fabricmc:fabric-loader.
        // Its intermediary identifies the ecosystem and MC version, never a second
        // loader or its version. Inspect all libraries before handling fabric-loader.
        var legacyFabric = detail.Libraries.Any(lib =>
            lib?.Name?.StartsWith("net.legacyfabric:", StringComparison.OrdinalIgnoreCase) == true);
        var legacyMinecraft = legacyFabric ? detail.Libraries
            .Select(lib => lib?.Name?.Split(':'))
            .Where(parts => parts is { Length: >= 3 } &&
                parts[0].Equals("net.legacyfabric", StringComparison.OrdinalIgnoreCase) &&
                parts[1].Equals("intermediary", StringComparison.OrdinalIgnoreCase))
            .Select(parts => FirstUsable(parts![2])).LastOrDefault(version => version != null) : null;

        foreach (var lib in detail.Libraries)
        {
            var name = lib?.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Maven 坐标：group:artifact:version[:classifier]
            var parts = name.Split(':');
            if (parts.Length < 3) continue;
            var group = parts[0];
            var artifact = parts[1];
            var version = parts[2];

            if ((group.Equals("net.fabricmc", StringComparison.OrdinalIgnoreCase) ||
                 group.Equals("net.legacyfabric", StringComparison.OrdinalIgnoreCase)) &&
                artifact.Equals("fabric-loader", StringComparison.OrdinalIgnoreCase))
                return (legacyFabric ? "Legacy Fabric" : "Fabric", version, legacyMinecraft);

            if (group.Equals("org.quiltmc", StringComparison.OrdinalIgnoreCase) &&
                artifact.Equals("quilt-loader", StringComparison.OrdinalIgnoreCase))
                return ("Quilt", version, null);

            if (group.Equals("net.neoforged", StringComparison.OrdinalIgnoreCase) &&
                (artifact.Equals("neoforge", StringComparison.OrdinalIgnoreCase) ||
                 artifact.Equals("forge", StringComparison.OrdinalIgnoreCase)))
                return ("NeoForge", version, null);

            if (group.Equals("net.minecraftforge", StringComparison.OrdinalIgnoreCase) &&
                artifact.Equals("forge", StringComparison.OrdinalIgnoreCase))
            {
                // Forge 的 version 段形如 "1.20.1-47.2.0"：前半是原版版本号，后半才是 Forge 版本号。
                var dash = version.IndexOf('-');
                if (dash > 0)
                {
                    var mc = version[..dash];
                    var forgeVer = version[(dash + 1)..];
                    return ("Forge", forgeVer, LooksLikeMcVersion(mc) ? mc : null);
                }
                return ("Forge", version, null);
            }
        }

        return legacyFabric ? ("Legacy Fabric", null, legacyMinecraft) : (null, null, null);
    }

    private static string? GuessLoaderFromId(string versionId)
    {
        var lower = versionId.ToLowerInvariant();
        // 注意顺序：neoforge 必须在 forge 之前判断，否则 "neoforge-21.1.66" 会被误判成 Forge。
        if (lower.Contains("neoforge")) return "NeoForge";
        if (lower.Contains("legacyfabric") || lower.Contains("legacy-fabric")) return "Legacy Fabric";
        if (lower.Contains("fabric")) return "Fabric";
        if (lower.Contains("quilt")) return "Quilt";
        if (lower.Contains("forge")) return "Forge";
        if (lower.Contains("optifine")) return "OptiFine";
        if (lower.Contains("liteloader")) return "LiteLoader";
        return null;
    }

    /// <summary>
    /// 按各加载器的官方版本 ID 命名约定反解加载器版本号：
    /// - Fabric / Quilt: "fabric-loader-{loaderVersion}-{mcVersion}"
    /// - Forge:          "{mcVersion}-forge-{forgeVersion}" 或 "{mcVersion}-forge{forgeVersion}"
    /// - NeoForge:       "neoforge-{neoforgeVersion}"
    /// </summary>
    private static string? GuessLoaderVersionFromId(string versionId, string? loader)
    {
        if (loader == null) return null;

        switch (loader)
        {
            case "Fabric":
            case "Legacy Fabric":
            case "Quilt":
            {
                var m = Regex.Match(versionId,
                    @"^(?:legacy-?fabric|fabric|quilt)-loader-(?<lv>[\w.+\-]+?)-(?<mc>[\w.\-]+)$",
                    RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups["lv"].Value;
                break;
            }
            case "Forge":
            {
                var m = Regex.Match(versionId, @"forge[-_]?(?<fv>\d[\w.\-]*)$", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups["fv"].Value;
                break;
            }
            case "NeoForge":
            {
                var m = Regex.Match(versionId, @"neoforge[-_]?(?<nv>\d[\w.\-]*)$", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups["nv"].Value;
                break;
            }
        }
        return null;
    }

    /// <summary>按命名约定反解原版版本号（跟 GuessLoaderVersionFromId 是同一批正则的另一半）。</summary>
    private static string? GuessMcVersionFromId(string versionId, string? loader)
    {
        if (loader is "Fabric" or "Legacy Fabric" or "Quilt")
        {
            var m = Regex.Match(versionId,
                @"^(?:legacy-?fabric|fabric|quilt)-loader-(?<lv>[\w.+\-]+?)-(?<mc>[\w.\-]+)$",
                RegexOptions.IgnoreCase);
            if (m.Success && LooksLikeMcVersion(m.Groups["mc"].Value)) return m.Groups["mc"].Value;
        }

        if (loader == "Forge")
        {
            // "1.20.1-forge-47.2.0" → 取第一段
            var m = Regex.Match(versionId, @"^(?<mc>[\d.]+)[-_]forge", RegexOptions.IgnoreCase);
            if (m.Success && LooksLikeMcVersion(m.Groups["mc"].Value)) return m.Groups["mc"].Value;
        }

        return null;
    }

    /// <summary>从任意字符串里抓出第一个"看起来像版本号"的片段。
    /// 顺序：官方历史 ID → 快照 → 预发布 → 普通数字版本，保留 a/b 前缀与补丁后缀。</summary>
    public static string? ExtractAnyVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var historical = HistoricalVersionPattern.Match(text);
        if (historical.Success) return historical.Value;

        var snap = SnapshotPattern.Match(text);
        if (snap.Success) return snap.Value;

        var pre = PreReleasePattern.Match(text);
        if (pre.Success) return pre.Value;

        var num = NumericVersionPattern.Match(text);
        return num.Success ? num.Value : null;
    }
}
