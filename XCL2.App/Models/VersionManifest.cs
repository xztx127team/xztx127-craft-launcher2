using System.Text.Json.Serialization;

namespace XCL2.App.Models;

public class VersionManifestRoot
{
    [JsonPropertyName("latest")] public LatestVersions Latest { get; set; } = new();
    [JsonPropertyName("versions")] public List<VersionManifestEntry> Versions { get; set; } = new();
}

public class LatestVersions
{
    [JsonPropertyName("release")] public string Release { get; set; } = "";
    [JsonPropertyName("snapshot")] public string Snapshot { get; set; } = "";
}

public enum VersionCategory
{
    Release,
    Snapshot,
    AprilFools,
    Legacy // old_alpha / old_beta
}

public class VersionManifestEntry
{
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("releaseTime")] public string ReleaseTime { get; set; } = "";

    /// <summary>
    /// Mojang 官方 version_manifest 的 "type" 字段只有 release / snapshot / old_beta / old_alpha 四种，
    /// 愚人节版本(如 15w14a、2.0、3D Shareware v1.34、22w13oneblockatatime、23w13a_or_b、24w14potato、
    /// 25w14craftmine、26w14a)在 manifest 里也被标记成 "snapshot"，官方没有单独区分，只能靠已知 id 名单识别。
    /// 这里维护一份已知愚人节版本清单；未来每年新出的愚人节版本需要手动追加到该集合里。
    ///
    /// 26w14a ("Herdcraft Update") 是 2026 年愚人节快照，2026-04-01 发布，
    /// 沿用旧的 "w<周数><字母>" 快照命名格式（不同于同年正式版 26.x 的编号规则）。
    /// </summary>
    private static readonly HashSet<string> KnownAprilFoolsIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "2.0", "15w14a", "1.RV-Pre1", "3D Shareware v1.34", "20w14infinite",
        "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine", "26w14a"
    };

    private static bool LooksLikePreviewId(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        foreach (var c in id)
        {
            var lower = char.ToLowerInvariant(c);
            if (lower is 'a' or 'b' or 'c' or 'w' or '-') return true;
        }
        return false;
    }

    public VersionCategory GetCategory()
    {
        if (Type is "old_alpha" or "old_beta") return VersionCategory.Legacy;
        if (KnownAprilFoolsIds.Contains(Id)) return VersionCategory.AprilFools;
        if (LooksLikePreviewId(Id)) return VersionCategory.Snapshot;
        return Type == "release" ? VersionCategory.Release : VersionCategory.Snapshot;
    }

    /// <summary>
    /// releaseTime 是 ISO-8601 字符串（如 "2024-04-23T13:19:35+00:00"），用 DateTimeOffset
    /// 解析后可直接按时间比较；解析失败（理论上不会发生，兜底用）时视为最旧，排到最后。
    /// </summary>
    public DateTimeOffset GetReleaseTimeOrMin()
        => DateTimeOffset.TryParse(ReleaseTime, out var t) ? t : DateTimeOffset.MinValue;
}

public static class VersionManifestSortExtensions
{
    /// <summary>
    /// 统一的"新→旧"排序入口：所有展示版本列表的地方（安装向导、下载中心）都应该调用该，
    /// 而不是各自直接假设 manifest 里的顺序已经是新到旧——虽然 Mojang 官方源目前确实是按时间
    /// 倒序给的，但第三方镜像源(BMCLAPI等)不保证顺序一致，显式按 releaseTime 倒序排一次，
    /// 保证界面"从上到下从新到旧"该约定任何数据源下都成立。releaseTime 相同/缺失时按 Id
    /// 做次级排序，保证结果稳定、可重现。
    /// </summary>
    public static List<VersionManifestEntry> SortNewestFirst(this IEnumerable<VersionManifestEntry> versions)
        => versions
            .OrderByDescending(v => v.GetReleaseTimeOrMin())
            .ThenByDescending(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>version_id.json 的核心字段（客户端 jar、库、资源索引、主类等）</summary>
public class VersionDetail
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("mainClass")] public string MainClass { get; set; } = "";
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
    [JsonPropertyName("jar")] public string? Jar { get; set; }
    [JsonPropertyName("arguments")] public ArgumentsSpec? Arguments { get; set; }
    [JsonPropertyName("minecraftArguments")] public string? MinecraftArgumentsLegacy { get; set; }
    [JsonPropertyName("libraries")] public List<LibraryEntry> Libraries { get; set; } = new();
    [JsonPropertyName("downloads")] public Dictionary<string, DownloadArtifact>? Downloads { get; set; }
    [JsonPropertyName("assetIndex")] public AssetIndexRef? AssetIndex { get; set; }
    [JsonPropertyName("assets")] public string? Assets { get; set; }
    [JsonPropertyName("javaVersion")] public JavaVersionSpec? JavaVersion { get; set; }
    [JsonPropertyName("logging")] public LoggingConfiguration? Logging { get; set; }

    [JsonPropertyName("clientVersion")] public string? ClientVersion { get; set; }
    // Component maintenance keeps the instance name unchanged even when its game/loader changes.
    // Separate metadata prevents names such as "Fabric 生存" from misidentifying a vanilla profile.
    [JsonPropertyName("xclMinecraftVersion")] public string? XclMinecraftVersion { get; set; }
    [JsonPropertyName("xclComponentLoader")] public string? XclComponentLoader { get; set; }
    [JsonPropertyName("xclComponentLoaderVersion")] public string? XclComponentLoaderVersion { get; set; }
}

public class LoggingConfiguration
{
    [JsonPropertyName("client")] public LoggingClientConfiguration? Client { get; set; }
}

public class LoggingClientConfiguration
{
    [JsonPropertyName("argument")] public string Argument { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("file")] public LoggingFile? File { get; set; }
}

public class LoggingFile
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public class JavaVersionSpec
{
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; } = 17;
}

public class ArgumentsSpec
{
    [JsonPropertyName("game")] public List<object>? Game { get; set; }
    [JsonPropertyName("jvm")] public List<object>? Jvm { get; set; }
}

public class AssetIndexRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public class DownloadArtifact
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
}

public class LibraryEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; set; }
    [JsonPropertyName("rules")] public List<LibraryRule>? Rules { get; set; }
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }

    /// <summary>
    /// Fabric/Quilt 风格的库条目没有 "downloads" 对象，只给 Maven 坐标 name + 仓库 url，
    /// 例如 {"name":"net.fabricmc:fabric-loader:0.15.11","url":"https://maven.fabricmc.net/"}。
    /// </summary>
    [JsonPropertyName("url")] public string? Url { get; set; }

    /// <summary>
    /// 把 "group:artifact:version[:classifier]" 坐标换算成标准 maven 仓库相对路径：
    /// group(.替换为/)/artifact/version/artifact-version[-classifier].jar
    /// 用于 downloads.artifact 缺失（Fabric/Quilt 等）时定位/拼接库文件。
    /// </summary>
    public string? GetMavenPath()
    {
        if (string.IsNullOrWhiteSpace(Name)) return null;
        var parts = Name.Split(':');
        if (parts.Length < 3) return null;

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";

        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    /// <summary>
    /// 按照 Mojang version json 的 rules 规则，判断这条库在当前系统(Windows)下是否适用。
    /// 没有 rules 字段的条目一律视为适用；有 rules 就按顺序应用，最后一条匹配的规则的 action 生效——
    /// 这是官方启动器和主流第三方启动器共用的标准算法。本启动器只跑在 Windows 上，固定按 "windows" 匹配。
    /// 之前只有下载阶段(DownloadService)做了该过滤，启动阶段(LauncherService)完全没做，
    /// 导致版本 json 里 Linux/macOS 专用的 native 库条目(本来就不会被下载)在启动时被误判成"缺失"，
    /// 纯净原版也会报"依赖库不存在"。现在两边统一调用这一个方法，不会再出现口径不一致。
    /// </summary>
    public bool IsApplicableToCurrentOs() => IsApplicableToCurrentOs(null, null);

    public bool IsApplicableToCurrentOs(string? architecture, string? osVersion = null)
    {
        if (Rules == null || Rules.Count == 0) return true;
        var allow = false;
        foreach (var rule in Rules)
        {
            var matchesOs = rule.Os == null || MinecraftPlatformRulePolicy.Matches(
                rule.Os.GetValueOrDefault("name"), rule.Os.GetValueOrDefault("arch"), rule.Os.GetValueOrDefault("version"), architecture, osVersion);
            if (matchesOs) allow = rule.Action == "allow";
        }
        return allow;
    }
}

public class LibraryDownloads
{
    [JsonPropertyName("artifact")] public DownloadArtifact? Artifact { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, DownloadArtifact>? Classifiers { get; set; }
}

public class LibraryRule
{
    [JsonPropertyName("action")] public string Action { get; set; } = "allow";
    [JsonPropertyName("os")] public Dictionary<string, string>? Os { get; set; }
}

/// <summary>Shared Windows rule matching for libraries and JVM/game argument entries.</summary>
internal static class MinecraftPlatformRulePolicy
{
    internal static string CurrentArchitecture => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.X64 => "amd64",
        System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
        System.Runtime.InteropServices.Architecture.Arm => "arm",
        _ => "unknown"
    };

    internal static bool Matches(string? name, string? architecturePattern, string? versionPattern,
        string? architecture = null, string? version = null)
        => (name == null || name == "windows")
            && RegexMatches(architecturePattern, architecture ?? CurrentArchitecture)
            && RegexMatches(versionPattern, version ?? Environment.OSVersion.Version.ToString());

    private static bool RegexMatches(string? pattern, string value)
    {
        if (pattern == null) return true;
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(value, pattern,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (Exception error) when (error is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        { return false; }
    }
}

/// <summary>asset index 内容：虚拟资源文件到 hash 的映射</summary>
public class AssetIndexFile
{
    [JsonPropertyName("objects")] public Dictionary<string, AssetObject> Objects { get; set; } = new();
}

public class AssetObject
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}
