namespace XCL2.App.Models;

public enum ServerCoreType
{
    Vanilla,
    Paper,
    Fabric,
    Forge,
    NeoForge,
    Quilt,
    Purpur,
    Folia,
    Velocity,
    Waterfall,
    Spigot,

    // ===== 非主流五大加载器之外的客户端加载器 =====
    // 老版本 MC（尤其是 1.5.2 及更早）没有 Fabric/Forge/NeoForge/Quilt 中的大部分选项，
    // 但有 LiteLoader 这类当年就存在的加载器；OptiFine 严格说不是"加载器"而是客户端优化 mod，
    // 但安装形态（独立 installer jar，产出一个可选的 versions/ 目录）跟加载器完全一致，
    // 沿用同一套 LoaderChoiceDialog/ClientLoaderInstallService 基础设施最省事。
    // Cleanroom 是给 1.12.2 生态的 Forge-fork（用 TRIP 后端跑在新版 Java 上），
    // LabyMod 4 同样是"独立安装、产出自己的 versions/ 目录"的客户端增强层。
    OptiFine,
    LiteLoader,
    Cleanroom,
    LabyMod,
    /// <summary>Legacy Fabric：Fabric 生态里专门覆盖 1.13 之前老版本的独立分支项目，
    /// Meta API 跟官方 Fabric 是同一套接口形状（meta.legacyfabric.net 对 meta.fabricmc.net），
    /// 客户端安装产出的 profile json 结构也跟 Fabric 完全一致，只是数据源、Maven 仓库地址不同。</summary>
    LegacyFabric
}

/// <summary>某个 Minecraft 版本下，某个核心类型可选的"构建/加载器版本"列表里的一项。</summary>
public class ServerCoreBuild
{
    /// <summary>展示给用户看的版本号文本，例如 Paper 的 build 号"451"，或 Fabric loader 版本"0.16.9"。</summary>
    public string DisplayVersion { get; set; } = "";

    /// <summary>是否官方标记为推荐/稳定构建（Paper 的 channel=default，Fabric 的 stable=true 等）。</summary>
    public bool IsRecommended { get; set; }

    /// <summary>部分加载器的"展示文本"和"实际调用安装接口所需的原始标识"不是同一个字符串
    /// （例如 OptiFine：DisplayVersion 是给用户看的 "HD_U_I6"，但下载接口要求分开传 type="HD_U"、
    /// patch="I6"）。这类场景把安装所需的原始标识按 "type|patch" 存在这里，调用方按 '|' 拆开用；
    /// 不需要该的加载器（Fabric/Forge/...）保持 null，行为不变。</summary>
    public string? RawIdentifier { get; set; }
}

/// <summary>下载一个服务端核心所需的完整上下文，UI 层收集后传给 ServerCoreDownloadService。</summary>
public class ServerCoreDownloadRequest
{
    public ServerCoreType CoreType { get; set; }
    public string McVersion { get; set; } = "";

    /// <summary>Paper/Purpur/Folia/Velocity/Waterfall 的 build 号 / Fabric 的 loader 版本号；
    /// Vanilla/Forge/NeoForge/Spigot 不需要，留空即可。</summary>
    public string? BuildOrLoaderVersion { get; set; }

    /// <summary>Forge/NeoForge 专用：安装器版本号（对应 Vanilla 版本可能有多个 loader 版本可选）。</summary>
    public string? InstallerVersion { get; set; }

    /// <summary>目标安装目录（对应清单里"安装位置可选"）。</summary>
    public string TargetDir { get; set; } = "";
}

/// <summary>
/// 下载完成后的结果：区分"已经是可直接启动的服务端 jar"和"还需要本地再跑安装器"两种情况，
/// 调用方（未来的开服向导）据此决定是直接进入下一步，还是先展示"正在安装服务端..."的中间态。
/// </summary>
public class ServerCoreDownloadResult
{
    public string DownloadedFilePath { get; set; } = "";

    /// <summary>true = DownloadedFilePath 就是安装器，还需要调用 ServerCoreDownloadService.RunForgeInstallerAsync。</summary>
    public bool RequiresInstall { get; set; }

    /// <summary>true = DownloadedFilePath 是 BuildTools.jar 本体，还需要调用
    /// ServerCoreDownloadService.RunSpigotBuildToolsAsync 在本地编译才能得到真正的服务端 jar
    /// （目前只有 Spigot 会是 true）。跟 RequiresInstall 分开一个字段，因为编译耗时数分钟、
    /// 需要 Git，UI 层要展示的中间态和"跑 Forge 安装器"完全不一样，不应该合并成同一个布尔值
    /// 让调用方猜"这里 RequiresInstall=true 到底是要跑安装器还是要编译"。</summary>
    public bool RequiresBuild { get; set; }

    /// <summary>安装完成后实际用于启动服务端的 jar 文件名（Vanilla/Paper/Fabric 下载完就是该；
    /// Forge/NeoForge 要等 RunForgeInstallerAsync 跑完后才能确定实际文件名，这里先留空）。</summary>
    public string? ServerJarFileName { get; set; }

    /// <summary>
    /// 该服务端核心运行所需的 Java 主版本号。Vanilla 从 Mojang version.json 的
    /// javaVersion.majorVersion 字段读取（权威）；其余核心类型退化为按 MC 版本号区间估算
    /// （见 ServerJavaRequirement），比完全不管、硬用客户端全局 Java 版本要可靠得多——
    /// 后者正是 "UnsupportedClassVersionError: class file version 69.0 ... up to 65.0"
    /// 这类崩溃的根因。
    /// </summary>
    public int RequiredJavaMajorVersion { get; set; } = 21;
}

public static class ServerJavaRequirement
{
    public static int EstimateMajorVersionForMcVersion(string mcVersion)
    {
        var parsed = ParseVersionParts(mcVersion);
        if (parsed == null) return 21;

        var (major, minor, patch) = parsed.Value;

        // 年份制版本号（26.x 及以后，Minecraft 从 26 起换了命名方案）。
        // 旧代码这里一律 return 21，等于把所有新版本都当成"要 Java 21"——
        // 而 1.26.1+ 在旧命名下本来就要求 Java 25，年份制的 26.x 是同一批版本的新写法，
        // 要求不会比它更低。按 25 处理，跟下面 `minor >= 26 && patch >= 1` 那条规则保持一致。
        if (major >= 26) return 25;
        if (major != 1) return 21;

        // 1.26.1 及以上（含未来 1.27+）固定要求 Java 25，必须最先判断，
        // 否则会被下面 "minor >= 21 -> 21" 这条分支提前拦截。
        if (minor > 26) return 25;
        if (minor == 26 && patch >= 1) return 25;

        // 1.20.1 起（含 1.20.1、1.20.2...、1.21+ 到 1.26.0）要求 Java 21。
        if (minor > 20) return 21;
        if (minor == 20 && patch >= 1) return 21;

        // 1.16 以上（即 1.17 起）到 1.20（不含 1.20.1）要求 Java 17。
        if (minor >= 17) return 17;

        // 1.16 及以下要求 Java 8。
        return 8;
    }

    private static (int major, int minor, int patch)? ParseVersionParts(string mcVersion)
    {
        if (string.IsNullOrWhiteSpace(mcVersion)) return null;
        var parts = mcVersion.Split('.');
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out var major)) return null;
        if (!int.TryParse(parts[1], out var minor)) return null;
        var patch = 0;
        if (parts.Length >= 3) int.TryParse(parts[2], out patch);
        return (major, minor, patch);
    }
}
