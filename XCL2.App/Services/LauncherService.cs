using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// 负责根据版本 json、账户信息、Java 路径拼装启动参数并启动游戏进程，
/// 同时支持离线账户的安全 .bat 导出；认证账户只导出提示，不持久化登录凭据。
/// </summary>
/// <summary>
/// 启动时发现依赖库文件缺失（常见于远古版本如 1.8 及更早）时抛出，携带结构化的
/// VersionId，让 UI 层能直接调用 DownloadService.DownloadLibrariesOnlyAsync
/// 做"一键补全依赖库后自动重试启动"，不需要用户手动去"版本选择"页重装整个版本。
/// </summary>
public class MissingLibrariesException : InvalidOperationException
{
    public IReadOnlyList<string> MissingLibraryNames { get; }
    public string VersionId { get; }

    public MissingLibrariesException(string versionId, List<string> missingLibraryNames, string message)
        : base(message)
    {
        VersionId = versionId;
        MissingLibraryNames = missingLibraryNames;
    }
}

public partial class LauncherService
{
    public class LaunchOptions
    {
        public string MinecraftDir { get; set; } = "";
        public string VersionId { get; set; } = "";
        public string JavaPath { get; set; } = "";
        public Account Account { get; set; } = new();
        public int MinMemoryMb { get; set; } = 1024;
        public int MaxMemoryMb { get; set; } = 4096;
        public int WindowWidth { get; set; } = 854;
        public int WindowHeight { get; set; } = 480;

        /// <summary>是否为本次 javaw.exe 启动请求 Windows 的高性能 GPU。默认由调用方按“实例 > 总体”解析后传入。</summary>
        public bool UseHighPerformanceGpu { get; set; } = true;

        /// <summary>是否额外弹出一个独立 CMD 窗口，实时镜像游戏控制台输出，方便在命令行里直接查看日志。</summary>
        public bool ShowConsoleWindow { get; set; } = false;

        /// <summary>本次实际选择的触屏模式，用于 JVM 启动前准备 GLFW 输入兼容设置。</summary>
        public bool UseTouchMode { get; set; }

        /// <summary>
        /// 是否对该版本启用"版本隔离"：开启后 --gameDir 会指向
        /// .minecraft/versions/&lt;VersionId&gt;/ 而不是 .minecraft 根目录，
        /// 这样 mods、resourcepacks、saves、config、shaderpacks 等就都是每个版本各自独立的，
        /// 不会跟其他版本共用一份、互相污染。这跟官方启动器/HMCL/PCL 的默认行为一致。
        /// </summary>
        public bool IsolateVersion { get; set; } = true;

        /// <summary>实际使用的游戏运行目录 (--gameDir)。由 BuildArguments 根据 IsolateVersion 计算后写入，
        /// 供 Launch() 之后用于设置进程工作目录，调用方不需要自己算。</summary>
        public string EffectiveGameDir { get; internal set; } = "";

        /// <summary>
        /// 游戏内语言，格式为 Minecraft options.txt 认识的 lang 值，如 "zh_cn"、"en_us"。
        /// 为空/null 时不写入、不干预，保留玩家在游戏内自己选过的语言。
        /// </summary>
        public string? GameLanguage { get; set; }

        /// <summary>
        /// 实例级“游戏内图形 API”偏好：game=游戏自主设置，opengl=强制 OpenGL，vulkan=强制 Vulkan。
        /// UI 仅对 Minecraft 26.1+ 暴露；该值只作用于 Minecraft 游戏进程自己的
        /// options.txt / 启动参数，不会改变启动器 WPF 界面的渲染后端。
        /// </summary>
        public string? GraphicsApiPreference { get; set; }

        /// <summary>
        /// 用户自定义 JVM 启动参数（原始字符串，未切分），仅高手模式下由 UI 传入非空值。
        /// 拼接位置在 BuildArguments 里位于官方 arguments.jvm 解析出的参数之后、"-cp" 之前。
        /// </summary>
        public string? CustomJvmArgs { get; set; }

        /// <summary>
        /// 离线自定义皮肤所需的额外 JVM 参数(-javaagent 挂载 authlib-injector 等)，由调用方
        /// 通过 <see cref="SkinService.BuildSkinJvmArgs"/> 预先算好后传入——LauncherService 本身
        /// 不负责判断"该账户是不是该用万能皮肤补丁"，只负责在合适的位置原样拼接这些参数。
        /// 拼接位置在 CustomJvmArgs 之前(官方参数之后)，同样遵循"后面覆盖前面"的 JVM 惯例，
        /// 让用户的 CustomJvmArgs 如果凑巧也设置了同名 -D 属性时能够覆盖这里的默认值。
        /// </summary>
        public List<string>? SkinJvmArgs { get; set; }

        public string? VersionTypeLabel { get; set; }

        /// <summary>
        /// 启动前执行的命令（高手模式可选，例如启动前先跑一个脚本备份存档/同步配置）。
        /// 原始命令行字符串，会通过系统默认 shell(cmd /c) 执行，工作目录设为 EffectiveGameDir。
        /// 执行失败/返回非 0 不会阻止游戏启动——这只是一个辅助钩子，不是启动的前置条件，
        /// 失败了应该让用户自己发现（通过日志），而不是把游戏也一起卡住。
        /// </summary>
        public string? PreLaunchCommand { get; set; }

        /// <summary>
        /// 启动后自动加入的服务器地址（形如 "play.example.com" 或 "1.2.3.4:25565"），
        /// 对应 Minecraft 1.20+ 支持的 --quickPlayMultiplayer 启动参数：游戏加载完主菜单后
        /// 会跳过手动进多人游戏列表点服务器这一步，直接尝试连接该地址。
        /// 为空/null 时不传该参数，行为等同于之前（游戏正常进主菜单，不自动连接任何服务器）。
        /// 只有当前正在启动的该 version json 的 arguments.game 里真的声明了
        /// --quickPlayMultiplayer 该键时才会生效（1.20.6 以前的版本没有该参数，
        /// 传了也不会有任何效果，BuildArguments 内部会自动判断，不需要调用方关心版本号）。
        /// </summary>
        public string? AutoJoinServerAddress { get; set; }
        public bool RequireAutoJoinServerSupport { get; set; }
        public string? ExtraGameArgs { get; set; }
        public bool LaunchFullscreen { get; set; }
        internal Plugins.PluginChain<Plugins.PluginLaunchPlan>? PreparedPluginLaunch { get; set; }
        internal string? PreparedPluginIdentity { get; set; }
    }

    public static int? GetRequiredJavaMajorVersion(string minecraftDir, string versionId, bool isolateVersion = true)
    {
        int? fromVersionJson = null;
        int? metadataFallback = null;
        try
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentId = versionId;
            for (var depth = 0; depth < 32 && !string.IsNullOrWhiteSpace(currentId) && visited.Add(currentId); depth++)
            {
                var versionDir = ResourceDownloadPath.ResolveChild(Path.Combine(minecraftDir, "versions"), currentId);
                var versionJsonPath = ResolveVersionFile(versionDir, currentId, "json");
                if (versionJsonPath == null) break;
                var detail = JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(versionJsonPath));
                var ownMajor = detail?.JavaVersion?.MajorVersion;
                if (fromVersionJson == null && ownMajor is int v && v > 0) fromVersionJson = v;
                // 文件夹允许改名。优先采信 JSON 自己的版本号，而不是只解析文件夹名。
                metadataFallback ??= LegacyMinecraftCompatibility.EstimateJavaMajor(
                    ExtractBaseMinecraftVersion(detail?.ClientVersion ?? ""));
                metadataFallback ??= LegacyMinecraftCompatibility.EstimateJavaMajor(
                    ExtractBaseMinecraftVersion(detail?.Id ?? ""));
                if (metadataFallback == null && LegacyMinecraftCompatibility.UsesPositionalIdentity(detail?.MinecraftArgumentsLegacy))
                    metadataFallback = 8;
                currentId = detail?.InheritsFrom ?? "";
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback($"读取版本 JSON 的 Java 要求失败，已尝试其它来源：{versionId}", ex);
        }

        int? fromMods = null;
        try
        {
            fromMods = GetMaxRequiredJavaMajorFromMods(minecraftDir, versionId, isolateVersion);
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback($"扫描 Mod Java 要求失败，已尝试其它来源：{versionId}", ex);
        }

        // 旧缓存/第三方 JSON 可能没有 javaVersion。不要随手用系统中第一个 Java 21+ 去
        // 运行依赖 Java 8 URLClassLoader 的 Launchwrapper；已声明的要求始终优先。
        fromVersionJson ??= metadataFallback ?? LegacyMinecraftCompatibility.EstimateJavaMajor(
            ExtractBaseMinecraftVersion(versionId));

        if (fromVersionJson == null) return fromMods;
        if (fromMods == null) return fromVersionJson;
        return Math.Max(fromVersionJson.Value, fromMods.Value);
    }

    public List<string> CheckMissingLibraries(string minecraftDir, string versionId)
        => CheckMissingLibraries(minecraftDir, versionId, null);

    public List<string> CheckMissingLibraries(string minecraftDir, string versionId, string? architecture)
    {
        var missing = new List<string>();
        var versionDir = ResourceDownloadPath.ResolveChild(Path.Combine(minecraftDir, "versions"), versionId);
        var versionJsonPath = ResolveVersionFile(versionDir, versionId, "json");
        if (versionJsonPath == null)
        {
            // 版本 json 本身都找不到，这已经不是"缺依赖库"能描述的问题了，交给
            // BuildArguments 里已有的、措辞更明确的 InvalidOperationException 去处理，
            // 这里不重复报错，避免同一个问题被以两种不同的方式提示两次。
            return missing;
        }

        var inheritance = VersionInheritanceResolver.Load(minecraftDir, versionId, FolderService.ResolveVersionJson, architecture);
        var detail = inheritance.Effective;

        var librariesDir = Path.Combine(minecraftDir, "libraries");
        var clientJarDir = inheritance.ClientLayer.Directory;
        var nativesDir = NativeDirectoryPolicy.Resolve(clientJarDir, architecture);
        ArchiveImportSafety.CheckLinks(nativesDir);
        var hasClassifierNativesEntry = false;

        void Scan(VersionDetail d)
        {
            foreach (var lib in d.Libraries.Where(l => l.IsApplicableToCurrentOs(architecture)))
            {
                // 跟 BuildArguments.AddLibs 里的判断保持一致：纯 classifier-natives 条目
                // 不进 classpath，但需要单独确认它对应的 dll 确实已经解压出来了。
                if (lib.Downloads?.Artifact == null && lib.Natives != null && lib.Natives.Count > 0)
                {
                    hasClassifierNativesEntry = true;
                    continue;
                }

                string? relativePath = null;
                if (lib.Downloads?.Artifact is { } art && !string.IsNullOrEmpty(art.Path))
                    relativePath = art.Path;
                else if (!string.IsNullOrEmpty(lib.Url))
                    relativePath = lib.GetMavenPath();

                if (string.IsNullOrEmpty(relativePath)) continue;

                var p = ArchiveImportSafety.Resolve(librariesDir, relativePath);
                if (!File.Exists(p))
                {
                    missing.Add($"{lib.Name} (期望路径: {p})");
                }
                else
                {
                    // 同 DownloadService.VerifySha1 的修复动机："文件存在"不等于"文件完整"——
                    // 0 字节的库 jar（下载中断/磁盘写满留下的半成品）之前完全没人检查，
                    // 直接被当成"已安装"放行，直到 Java 进程真正加载该 jar 才崩溃
                    // （比如 lwjgl 的 jar 是空文件，报 NoClassDefFoundError: org/lwjgl/...）。
                    // 这里补上启动前的最后一道防线：哪怕文件"存在"，0 字节也判定为缺失，
                    // 让下面的"自动补全"提示能覆盖到这种情况，而不是放任它一路崩到 Java 里。
                    try
                    {
                        if (new FileInfo(p).Length == 0)
                            missing.Add($"{lib.Name} (文件已损坏/大小为 0，路径: {p})");
                    }
                    catch { /* 拿不到文件大小就不额外判定，交给后面真正启动时的检测兜底 */ }
                }
            }
        }
        Scan(detail);
        var logging = LauncherLoggingPolicy.Resolve(detail);
        if (logging != null && !LauncherLoggingPolicy.IsVerified(LauncherLoggingPolicy.PathFor(minecraftDir, logging), logging))
            missing.Add("Log4j 日志安全配置缺失或校验失败，请自动补全依赖。");

        if (hasClassifierNativesEntry)
        {
            var hasAnyExtractedNative = NativeDirectoryPolicy.HasCompatibleDll(nativesDir, architecture);
            if (!hasAnyExtractedNative)
            {
                missing.Add($"natives 运行库(lwjgl/openal 等 .dll 文件，目录: {nativesDir})");
            }
        }

        return missing;
    }

    /// <summary>
    /// 扫描该版本 mods 目录下所有 jar，读取里面 fabric.mod.json 的 "depends": { "java": "..." }
    /// (以及少数 mod 用 "requires" 字段写同样的意思)，解析出其中声明的最低 Java 主版本号，
    /// 返回所有 mod 里要求的最大值(最严格的那个)。
    /// mods 目录位置跟"版本隔离"开关一致：开启时是 versions/&lt;id&gt;/mods，
    /// 关闭时是 .minecraft 根目录下的 mods（这里传入的 isolateVersion 需要和实际启动时
    /// 使用的隔离设置保持一致，否则会扫到错误的 mods 目录）。
    /// </summary>
    private static int? GetMaxRequiredJavaMajorFromMods(string minecraftDir, string versionId, bool isolateVersion)
    {
        var gameDir = isolateVersion ? ResourceDownloadPath.ResolveChild(Path.Combine(minecraftDir, "versions"), versionId) : minecraftDir;
        var modsDir = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(modsDir)) return null;

        int? max = null;
        foreach (var jarPath in Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
        {
            int? require;
            try { require = ReadFabricModJavaRequirement(jarPath); }
            catch (Exception ex)
            {
                ErrorPresenter.LogFallback($"读取单个 Mod 的 Java 要求失败，已跳过：{jarPath}", ex);
                continue;
            } // 单个 mod jar 读取失败(损坏/非法 zip 等)不应该影响其他 mod 的判断

            if (require is int r && (max == null || r > max)) max = r;
        }
        return max;
    }

    /// <summary>从版本 ID 里提取出基础原版 Minecraft 版本号，用于兜底估算 Java 要求。
    /// 兼容常见命名：纯原版 "1.16.5"、带加载器后缀 "1.16.5-forge-36.2.34"、
    /// "fabric-loader-0.15.0-1.16.5"、"1.16.5-Fabric 0.15.0" 等——按顺序在整个字符串里找
    /// 第一段形如 "1.x" 或 "1.x.y" 的数字序列即可，不需要认出具体是哪个加载器。</summary>
    internal static string? ExtractBaseMinecraftVersion(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return null;

        return VersionInfoResolver.ExtractAnyVersion(versionId);
    }

    /// <summary>从单个 mod jar 里读取 fabric.mod.json 的 depends.java (或 requires.java) 字段，
    /// 解析出其中声明的最低 Java 主版本号。字段值形如 ">=25"、"&gt;=17"、"25" 等，
    /// 这里只关心"最低版本号"该数字本身，不需要精确实现完整的版本范围语法。</summary>
    private static int? ReadFabricModJavaRequirement(string jarPath)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(jarPath);
        var entry = archive.GetEntry("fabric.mod.json");
        if (entry == null) return null;

        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        foreach (var section in new[] { "depends", "requires" })
        {
            if (!root.TryGetProperty(section, out var depends)) continue;
            if (!depends.TryGetProperty("java", out var javaReq)) continue;
            if (javaReq.ValueKind != JsonValueKind.String) continue;

            var parsed = ParseMinJavaMajorFromRequirementString(javaReq.GetString() ?? "");
            if (parsed != null) return parsed;
        }
        return null;
    }

    /// <summary>从形如 ">=25"、"&gt;=17"、"25"、">=1.8" 这样的版本要求字符串里提取出
    /// "最低需要的 Java 主版本号"这一个数字。兼容两种历史写法(跟 JavaService.ParseJavaMajorVersion
    /// 用的是同一套规则)：
    /// - 新式(Java 9+): "25"/"&gt;=25"/"25.x" -> 主版本号就是第一段数字 "25"
    /// - 旧式(Java 8 及以前): "1.8"/"&gt;=1.8" -> 第一段数字是 "1"，真正的主版本号在第二段 "8"
    /// 只取字符串里的数字段，足以覆盖 fabric.mod.json "java" 字段的实际写法(一个简单的最低版本
    /// 声明，不是完整的 semver 比较符组合表达式)。</summary>
    internal static int? ParseMinJavaMajorFromRequirementString(string requirement)
    {
        // 按 . 拆分后逐段提取数字，忽略比较符(>= <= > < ~ ^ 空格等非数字字符)。
        var segments = requirement.Split('.')
            .Select(seg => new string(seg.Where(char.IsDigit).ToArray()))
            .Where(seg => seg.Length > 0)
            .Select(seg => int.TryParse(seg, out var n) ? n : (int?)null)
            .Where(n => n != null)
            .Select(n => n!.Value)
            .ToList();

        if (segments.Count == 0) return null;

        // "1.8" 这种旧格式第一段永远是 1，真正版本号在第二段；否则第一段就是主版本号。
        if (segments[0] == 1 && segments.Count > 1) return segments[1];
        return segments[0];
    }

    /// <summary>启动游戏，返回封装了进程 + 实时输出缓冲的 GameProcessInfo，供进程管理/日志面板使用。</summary>
    public GameProcessInfo Launch(LaunchOptions opts)
    {
        using var lease = ComponentsOperationLock.AcquireForLaunch(opts.MinecraftDir, opts.VersionId);
        var settings = InstanceConfigService.TryLoad(Path.Combine(opts.MinecraftDir, "versions", opts.VersionId));
        if (settings?.OptimizeMemoryBeforeLaunch == true)
        {
            try { LauncherLogService.AppendLine("[启动前内存优化] " + WorkingSetOptimizationService.Run().Summary); }
            catch (Exception ex) { ErrorPresenter.LogFallback("启动前内存优化失败", ex); }
        }
        var info = Plugins.PluginRuntimeRegistry.Launch(opts, () => LaunchBuiltIn(opts))
            ?? throw new InvalidOperationException("启动内核没有返回游戏进程。");
        // Keep the selected profile and every inherited client locked even before its first
        // window appears. Shared leases preserve the existing ability to launch multiple games.
        lease.TransferTo(info.Process);
        return info;
    }

    /// <summary>内置 Java 启动内核。公开 Launch 先经过旧版插件中间件链；next() 最终落到这里。</summary>
    private GameProcessInfo LaunchBuiltIn(LaunchOptions opts)
    {
        DemandPreparedLaunch(opts);
        var (mainClass, args) = BuildArguments(opts); // 内部会设置 opts.EffectiveGameDir
        args = ApplyPluginLaunchPlan(opts, args);

        
        if (!string.IsNullOrWhiteSpace(opts.PreLaunchCommand))
        {
            try
            {
                var preLaunchPsi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {opts.PreLaunchCommand}",
                    WorkingDirectory = Directory.Exists(opts.EffectiveGameDir) ? opts.EffectiveGameDir : opts.MinecraftDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var preProcess = Process.Start(preLaunchPsi);
                preProcess?.WaitForExit(10_000); // 最多等 10 秒，避免一个卡住的脚本把启动流程无限期挂起
            }
            catch (Exception ex)
            {
                ErrorPresenter.LogTechnicalDetail($"[启动前执行命令失败] 命令：{opts.PreLaunchCommand}\n{ex}");
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = opts.JavaPath,
            WorkingDirectory = opts.EffectiveGameDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (opts.PreparedPluginLaunch != null)
            foreach (var variable in opts.PreparedPluginLaunch.Plan.EnvironmentVariables) psi.Environment[variable.Key] = variable.Value;

        // GPU 首选项必须在 javaw.exe 创建之前设置。Windows 图形设置按可执行文件路径保存
        // GpuPreference；同时给子进程注入 SHIM_MCCOMPAT，兼容一部分 NVIDIA Optimus 机器。
        // 任何注册表/驱动兼容性异常都只记录日志，不得阻止 Minecraft 正常启动。
        HighPerformanceGpuService.ApplyForLaunch(psi, opts.JavaPath, opts.UseHighPerformanceGpu);

        bool? originalTouchInput = null;
        LegacyTouchOptions? legacyTouch = null;
        if (opts.UseTouchMode)
        {
            originalTouchInput = TouchVersionPolicy.Resolve(opts.MinecraftDir, opts.VersionId);
            // 仅在明确选择触屏时恢复旧触屏包留下的覆盖；键鼠启动不读写这些设置。
            GlfwTouchLaunchCompatibility.RestorePreviousOverride(opts.EffectiveGameDir);
            if (originalTouchInput == false) legacyTouch = LegacyTouchOptions.Acquire(opts.EffectiveGameDir);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (legacyTouch != null) process.Exited += (_, _) => legacyTouch.Dispose();
        try { process.Start(); } catch { legacyTouch?.Dispose(); throw; }
        if (process.HasExited) legacyTouch?.Dispose();
        else if (legacyTouch != null)
            try { legacyTouch.Attach(process); } catch (Exception ex) { LauncherLogService.AppendLine("[旧版触屏] 记录进程恢复信息失败：" + ex.Message); }

        var info = new GameProcessInfo(process, opts.VersionId, opts.Account.DisplayLabel, opts.EffectiveGameDir)
        {
            UseOriginalTouchInput = originalTouchInput,
            TouchModeRequestedAtLaunch = opts.UseTouchMode,
            TouchModeEnabled = opts.UseTouchMode
        };
        info.BeginReadOutput();
        Plugins.PluginHostBridge.Current?.WatchGame(info, opts.MinecraftDir);
        return info;
    }

    /// <summary>默认不导出认证令牌，保留现有调用接口。</summary>
    public string ExportLaunchScript(LaunchOptions opts) => ExportLaunchScript(opts, includeAccessToken: false);

    /// <summary>用户明确选择时，将当前游戏访问令牌写入独立的启动脚本；此选择不持久保存。</summary>
    public string ExportLaunchScript(LaunchOptions opts, bool includeAccessToken)
    {
        var versionDir = ResourceDownloadPath.ResolveChild(Path.Combine(opts.MinecraftDir, "versions"), opts.VersionId);
        var defaultPath = InstanceConfigService.GetLaunchScriptPath(versionDir);
        // This choice applies only to the current call. Automatic/default exports
        // cannot enable it or overwrite the user's separately requested token script.
        var path = includeAccessToken
            ? Path.Combine(Path.GetDirectoryName(defaultPath)!, "launch-with-token.bat")
            : defaultPath;
        // Replace any previously exported session before validation/plugin callbacks can
        // fail. Failed exports retain only this fixed, credential-free notice.
        LaunchScriptExportPolicy.Write(path, LaunchScriptExportPolicy.ProtectedNotice());
        if (opts.Account.Type != AccountType.Offline)
        {
            if (!includeAccessToken) return path;
            if (string.IsNullOrWhiteSpace(opts.Account.MinecraftAccessToken))
                throw new InvalidOperationException("当前账户没有可导出的游戏登录令牌，请先登录后再导出。");
        }

        DemandPreparedLaunch(opts);
        var (_, args) = BuildArguments(opts);
        args = ApplyPluginLaunchPlan(opts, args);
        var script = LaunchScriptExportPolicy.Runnable(opts.JavaPath, opts.EffectiveGameDir, args, opts.UseHighPerformanceGpu);
        LaunchScriptExportPolicy.Write(path, script);
        return path;
    }

    private static void DeduplicateClasspathByMavenCoordinates(List<string> classpath, string librariesDir)
    {
        if (classpath.Count < 2) return;

        var keyByPath = new Dictionary<string, (string Key, string Version)>(StringComparer.Ordinal);
        foreach (var p in classpath)
        {
            try
            {
                var rel = Path.GetRelativePath(librariesDir, p);
                var parts = rel.Split(Path.DirectorySeparatorChar);
                // 库 jar 在 libraries 目录下的相对路径结构固定为
                // <group路径>/<artifact>/<version>/<artifact>-<version>[-classifier].jar
                if (parts.Length >= 4 && !rel.StartsWith("..", StringComparison.Ordinal))
                {
                    var version = parts[parts.Length - 2];
                    var artifact = parts[parts.Length - 3];
                    var group = string.Join(".", parts, 0, parts.Length - 3);

                    // 从文件名里把 classifier 抠出来：文件名固定形如
                    // "<artifact>-<version>[-<classifier>].jar"，去掉 "<artifact>-<version>" 前缀
                    // 和 ".jar" 后缀，剩下的（如果以 '-' 开头）就是 classifier，没有就是空字符串
                    // （代表无 classifier 的主 jar 本体）。
                    var fileName = parts[parts.Length - 1];
                    var expectedPrefix = artifact + "-" + version;
                    var classifier = "";
                    if (fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                        && fileName.StartsWith(expectedPrefix, StringComparison.Ordinal))
                    {
                        var middle = fileName.Substring(expectedPrefix.Length, fileName.Length - expectedPrefix.Length - 4);
                        if (middle.StartsWith("-", StringComparison.Ordinal)) classifier = middle;
                    }

                    keyByPath[p] = (group + ":" + artifact + ":" + classifier, version);
                }
            }
            catch
            {
                // 路径无法相对化（如跨盘符）时不参与去重，原样保留
            }
        }

        // 同一 key 出现多份时，只保留版本号最高的那份；其余进删除名单
        var best = new Dictionary<string, string>(StringComparer.Ordinal);
        var toRemove = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (p, kv) in keyByPath)
        {
            if (best.TryGetValue(kv.Key, out var current))
            {
                if (CompareMavenVersions(kv.Version, keyByPath[current].Version) > 0)
                {
                    toRemove.Add(current);
                    best[kv.Key] = p;
                }
                else
                {
                    toRemove.Add(p);
                }
            }
            else
            {
                best[kv.Key] = p;
            }
        }

        if (toRemove.Count > 0)
            classpath.RemoveAll(toRemove.Contains);
    }

    /// <summary>
    /// Maven 版本号比较：按 '.'/'-'/'_' 分段，纯数字段按数值比，其余按字符串比；
    /// 前面可带 v/V 前缀（如 v1.2.3），比较时忽略。例：9.3 &lt; 9.10.1。
    /// </summary>
    private static int CompareMavenVersions(string a, string b)
    {
        static string[] Segments(string v)
        {
            var s = v.TrimStart('v', 'V');
            return s.Split(new[] { '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        }

        var sa = Segments(a);
        var sb = Segments(b);
        for (var i = 0; i < Math.Max(sa.Length, sb.Length); i++)
        {
            var x = i < sa.Length ? sa[i] : "";
            var y = i < sb.Length ? sb[i] : "";
            if (x == y) continue;
            if (long.TryParse(x, out var xn) && long.TryParse(y, out var yn))
                return xn.CompareTo(yn);
            return string.CompareOrdinal(x, y);
        }
        return 0;
    }

    internal static string? ResolveVersionFile(string dir, string preferredBaseName, string extension)
    {
        if (string.Equals(extension, "json", StringComparison.OrdinalIgnoreCase))
            return FolderService.ResolveVersionJson(dir, preferredBaseName);
        var exact = Path.Combine(dir, $"{preferredBaseName}.{extension}");
        if (File.Exists(exact)) return exact;
        if (!Directory.Exists(dir)) return null;
        var matches = Directory.GetFiles(dir, $"*.{extension}");
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string BuildDisplayVersion(string versionId, VersionDetail detail, VersionDetail? parent)
    {
        var isModernClient = !string.IsNullOrEmpty(detail.ClientVersion) || !string.IsNullOrEmpty(parent?.ClientVersion);
        return isModernClient ? versionId : $"{versionId} XCL2";
    }

    /// <summary>
    /// 把用户手写的一整行 JVM 参数字符串切分成参数数组，正确处理双引号包裹的带空格参数
    /// （例如 -Dfoo="some value with spaces"）。不能用简单的 string.Split(' ')，
    /// 否则带引号的参数会被错误地拆成多段。
    /// 规则：不在引号内的空白是分隔符；引号本身不保留在结果里；未闭合的引号视为解析失败
    /// （抛异常，由调用方 catch 后忽略这段自定义参数，避免拼出语法错误的命令行）。
    /// </summary>
    internal static List<string> SplitArgsRespectingQuotes(string input)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasContent = false;

        foreach (var c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasContent = true;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasContent)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasContent = false;
                }
                continue;
            }
            current.Append(c);
            hasContent = true;
        }

        if (inQuotes)
            throw new FormatException("自定义 JVM 参数里有未闭合的引号");

        if (hasContent)
            result.Add(current.ToString());

        return result;
    }

    private static bool IsNonEmptyFile(string path)
    {
        try { return new FileInfo(path).Length > 0; }
        catch { return false; }
    }

    private (string mainClass, List<string> args) BuildArguments(LaunchOptions opts, bool preview = false)
    {
        var versionDir = ResourceDownloadPath.ResolveChild(Path.Combine(opts.MinecraftDir, "versions"), opts.VersionId);

        // 版本隔离：开启时 mods/resourcepacks/saves/config/shaderpacks 等都放在该版本自己的
        // 文件夹下 (.minecraft/versions/<版本号>/)，不跟其他版本共用；关闭时则沿用旧行为，
        // 所有版本共用 .minecraft 根目录下同一份 mods 等文件夹。
        var gameDir = opts.IsolateVersion ? versionDir : opts.MinecraftDir;
        if (!preview) Directory.CreateDirectory(gameDir);
        opts.EffectiveGameDir = gameDir;

        if (!preview && !string.IsNullOrWhiteSpace(opts.GraphicsApiPreference))
        {
            try { ApplyGraphicsApiPreference(gameDir, opts.GraphicsApiPreference!); }
            catch (Exception ex)
            {
                // 图形 API 是可选覆盖项。options.txt 因权限/占用写不进去时记录日志，但不应该
                // 因为一项显示后端偏好而阻止整个游戏启动。
                ErrorPresenter.LogFallback($"写入实例图形 API 设置失败：{gameDir}", ex);
            }
        }

        // Resolve every ancestor once; missing parents and cycles are explicit launch errors.
        var argumentArchitecture = JavaExecutableArchitecture.TryReadArchitecture(opts.JavaPath) ?? LaunchArgumentRulePolicy.CurrentArchitecture;
        var inheritance = VersionInheritanceResolver.Load(opts.MinecraftDir, opts.VersionId, FolderService.ResolveVersionJson, argumentArchitecture);
        var detail = inheritance.Effective;
        var mainClass = detail.MainClass;
        var gameLanguage = LegacyMinecraftCompatibility.ResolveGameLanguage(opts.GameLanguage,
            inheritance.Layers.SelectMany(layer => new[] { layer.Detail.ClientVersion, layer.Detail.Id }));
        if (!preview && !string.IsNullOrWhiteSpace(gameLanguage))
        {
            try { ApplyGameLanguage(gameDir, gameLanguage!); }
            catch (Exception ex) { ErrorPresenter.LogFallback("写入游戏语言失败，保留游戏现有设置。", ex); }
        }

        var librariesDir = Path.Combine(opts.MinecraftDir, "libraries");
        var classpath = new List<string>();
        var missingLibraries = new List<string>();

        void AddLibs(VersionDetail d)
        {
            // 版本 json 里经常混杂 Linux/macOS 专用的 native 库条目（用 rules 按操作系统限定），
            // 这些条目在下载阶段本来就不会为 Windows 用户下载。之前这里没有做同样的平台过滤，
            // 导致把"压根没打算下载"的 Linux/macOS 库也当成"缺失"，纯净原版都会报依赖库缺失。
            foreach (var lib in d.Libraries.Where(l => l.IsApplicableToCurrentOs(argumentArchitecture)))
            {

                if (lib.Downloads?.Artifact == null && lib.Natives != null && lib.Natives.Count > 0)
                    continue;

                string? relativePath = null;
                if (lib.Downloads?.Artifact is { } art && !string.IsNullOrEmpty(art.Path))
                {
                    relativePath = art.Path;
                }
                else if (!string.IsNullOrEmpty(lib.Url))
                {

                    relativePath = lib.GetMavenPath();
                }

                if (string.IsNullOrEmpty(relativePath)) continue;

                var p = ArchiveImportSafety.Resolve(librariesDir, relativePath);
                if (File.Exists(p) && IsNonEmptyFile(p))
                    classpath.Add(p);
                else
                    missingLibraries.Add($"{lib.Name} (期望路径: {p})");
            }
        }
        AddLibs(detail);

        DeduplicateClasspathByMavenCoordinates(classpath, librariesDir);

        // The vanilla/client owner can be several ancestors away from the selected loader.
        var clientLayer = inheritance.ClientLayer;
        var clientJarDir = clientLayer.Directory;
        var clientJarBaseName = string.IsNullOrEmpty(clientLayer.Detail.Id) ? clientLayer.VersionId : clientLayer.Detail.Id;
        var clientJar = ResolveVersionFile(clientJarDir, clientJarBaseName, "jar");
        var nativesDir = NativeDirectoryPolicy.Resolve(clientJarDir, argumentArchitecture);
        ArchiveImportSafety.CheckLinks(nativesDir);
        var assetsId = detail.Assets ?? detail.AssetIndex?.Id ?? "legacy";
        _ = ResourceDownloadPath.ResolveChild(Path.Combine(opts.MinecraftDir, "assets", "indexes"), assetsId + ".json");

        if (string.IsNullOrEmpty(mainClass))
            throw new InvalidOperationException(
                $"版本 {opts.VersionId} 的 version json 中没有 mainClass，且父版本也没有，无法确定启动入口类。");

        if (detail.Libraries.Any(lib => lib.Natives?.ContainsKey("windows") == true) &&
            !NativeDirectoryPolicy.HasCompatibleDll(nativesDir, argumentArchitecture))
            missingLibraries.Add($"{argumentArchitecture} 本机运行库缺失或架构不匹配（目录: {nativesDir}）");

        if (missingLibraries.Count > 0)
        {
            throw new MissingLibrariesException(opts.VersionId, missingLibraries,
                $"以下 {missingLibraries.Count} 个依赖库文件在本地不存在，无法启动（很可能是远古版本的 lwjgl/jinput/twitch 等" +
                "natives 库或 Fabric/Forge 等加载器的库没有下载完整）：\n" +
                string.Join("\n", missingLibraries.Take(10)) +
                (missingLibraries.Count > 10 ? $"\n...等共 {missingLibraries.Count} 个" : "") +
                "\n\n可以点「自动补全」尝试自动下载补齐这些库。");
        }

        if (clientJar == null)
        {
            throw new InvalidOperationException(
                $"找不到版本主 jar 文件（文件夹：{clientJarDir}，期望文件名：{clientJarBaseName}.jar）。" +
                "请确认该版本文件夹里的主 jar 文件没有被误删或改名。");
        }
        classpath.Add(clientJar);

        var jvmArgsFromJson = new List<string>();
        if (detail.Arguments?.Jvm != null) jvmArgsFromJson.AddRange(ParseArgumentEntries(detail.Arguments.Jvm, architecture: argumentArchitecture));

        var isBootstrapLauncherModular = string.Equals(
            mainClass, "cpw.mods.bootstraplauncher.BootstrapLauncher", StringComparison.Ordinal);
        if (isBootstrapLauncherModular)
        {
            var ignoreListFragments = new List<string>
            {
                "bootstraplauncher", "securejarhandler",
                "asm-commons", "asm-util", "asm-analysis", "asm-tree", "asm",
                "JarJarFileSystems", "client-extra",
                "fmlcore", "javafmllanguage", "lowcodelanguage", "mclanguage",
                "forge-",
            };
            // 当前版本主 jar 的文件名片段（不含扩展名即可，ignoreList 是"包含"匹配）：
            // 用 clientJarBaseName 而不是 opts.VersionId，因为文件夹可能被改过名，
            // 真正参与 classpath/模块扫描的是 json 内部 id 对应的那个物理文件名。
            if (!string.IsNullOrEmpty(clientJarBaseName))
                ignoreListFragments.Add(clientJarBaseName);

            var mergeModuleJars = classpath
                .Select(Path.GetFileName)
                .Where(f => f != null &&
                            (f.StartsWith("jna-", StringComparison.OrdinalIgnoreCase) ||
                             f.StartsWith("jna-platform-", StringComparison.OrdinalIgnoreCase)))
                .Distinct()
                .ToList();

            jvmArgsFromJson.Add($"-DignoreList={string.Join(",", ignoreListFragments)}");
            if (mergeModuleJars.Count > 0)
                jvmArgsFromJson.Add($"-DmergeModules={string.Join(",", mergeModuleJars)}");
        }

        var legacyArgsText = detail.MinecraftArgumentsLegacy;
        var usesPositionalIdentity = detail.Arguments?.Game is not { Count: > 0 } &&
            LegacyMinecraftCompatibility.UsesPositionalIdentity(legacyArgsText);
        var usesManifestGameArguments = usesPositionalIdentity ||
            detail.Arguments?.Game is not { Count: > 0 } && LegacyMinecraftCompatibility.UsesEarlyNamedIdentity(legacyArgsText);
        var authenticated = opts.Account.Type is AccountType.Microsoft or AccountType.AuthServer;
        var accessToken = authenticated ? opts.Account.MinecraftAccessToken ?? "0" : "0";
        var uuid = opts.Account.Uuid.Replace("-", "");
        var displayVersion = BuildDisplayVersion(opts.VersionId, detail, null);
        var usesLegacyAssets = legacyArgsText?.Contains("${game_assets}", StringComparison.Ordinal) == true;
        var gameAssets = usesLegacyAssets ? LegacyAssetLayout.GetGameAssetsDirectory(opts.MinecraftDir, assetsId)
            : Path.Combine(opts.MinecraftDir, "assets");
        if (!preview && usesLegacyAssets) LegacyAssetLayout.PrepareForLaunch(opts.MinecraftDir, gameDir, assetsId);

        var variables = new Dictionary<string, string>
        {
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = "XCL2",
            ["launcher_version"] = "1",
            ["classpath"] = string.Join(";", classpath.Distinct()),

            ["library_directory"] = librariesDir,
            // 同一批新版 loader 的模块路径参数里还会出现 "${classpath_separator}"，用来拼接
            // 多个模块路径条目——Windows 上是分号 ';'，直接给固定值即可，不需要跟着 classpath
            // 变量走(那个是给 -cp 用的完整分类路径字符串，两者含义不同，不能混用)。
            ["classpath_separator"] = ";",
            // 老版本(1.6~1.12)version json 用 minecraftArguments 字符串，里面会出现这些占位符。
            // 其中 --userProperties 是 1.7.x/1.8.x 的必填项，缺了会直接
            // MissingRequiredOptionException ['userProperties'] 退出码 1。
            ["user_properties"] = "{}",
            ["profile_properties"] = "{}",
            ["auth_player_name"] = opts.Account.Username,
            ["auth_uuid"] = uuid,
            ["auth_access_token"] = accessToken,
            ["auth_session"] = LegacyMinecraftCompatibility.BuildAuthSession(uuid, accessToken, authenticated),
            ["game_directory"] = gameDir,
            ["game_assets"] = gameAssets,
            ["assets_root"] = Path.Combine(opts.MinecraftDir, "assets"),
            ["assets_index_name"] = assetsId,
            ["version_name"] = displayVersion,
            ["version_type"] = string.IsNullOrWhiteSpace(opts.VersionTypeLabel) ? "release" : opts.VersionTypeLabel,
            ["user_type"] = opts.Account.Type == AccountType.Microsoft ? "msa" : "legacy",
        };
        var resolvedJvmArgsFromJson = jvmArgsFromJson
            .Select(a => SubstituteVariables(a, variables))
            .Where(a => !string.IsNullOrWhiteSpace(a))
            // -cp/-classpath 和内存参数我们自己在下面手写指定，避免跟 json 里的重复/冲突。
            .Where(a => a != "-cp" && a != "-classpath" &&
                        !a.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase) &&
                        !a.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase))
            .ToList();
        for (var i = resolvedJvmArgsFromJson.Count - 1; i >= 0; i--)
        {
            if (resolvedJvmArgsFromJson[i] == variables["classpath"])
                resolvedJvmArgsFromJson.RemoveAt(i);
        }

        var args = new List<string>
        {
            $"-Xms{opts.MinMemoryMb}M",
            $"-Xmx{opts.MaxMemoryMb}M",
            $"-Djava.library.path={nativesDir}",
            // -Dfile.encoding 只影响文件读写用的默认编码；JDK 18+ 的控制台 stdout/stderr
            // 编码由单独的 stdout.encoding/stderr.encoding 决定（JEP 400），如果不显式指定，
            // 在中文 Windows 上 JVM 仍然会用系统 ANSI 代码页(GBK)写控制台输出，
            // 跟我们这边 StandardOutputEncoding=UTF8 对不上，日志面板/控制台镜像就会整体乱码。
            JavaRuntimeCompatibility.FileEncodingArgument(opts.JavaPath, detail.Libraries.Select(library => library.Name ?? "")),
            "-Dstdout.encoding=UTF-8",
            "-Dstderr.encoding=UTF-8",
            // 标识"这局游戏是被哪个启动器启动的"，参考 PCL 等主流第三方启动器的做法
            // (-Dminecraft.launcher.brand=PCL)。不影响游戏运行，只是让 Minecraft 自己生成的
            // 崩溃报告(crash-reports/*.txt)里能正确显示 "Launched Version: XCL2"，
            // 而不是显示空白/unknown——方便以后根据用户提交的崩溃报告定位问题。
            "-Dminecraft.launcher.brand=XCL2",
            "-Dminecraft.launcher.version=1",
        };

        args.AddRange(resolvedJvmArgsFromJson);

        // 离线自定义皮肤(万能皮肤补丁 authlib-injector)：放在官方参数之后、用户自定义 JVM 参数
        // 之前，这样如果用户自己在 CustomJvmArgs 里也写了同名 -D 属性，用户的设置优先生效。
        // 只有离线账户选了"自定义皮肤"才会有内容，史蒂夫/艾利克斯/微软账户都是空列表。
        if (opts.SkinJvmArgs is { Count: > 0 })
            args.AddRange(opts.SkinJvmArgs);

        // 用户自定义 JVM 参数（仅高手模式下 UI 才会传入非空值）：放在官方参数之后、-cp 之前，
        // 遵循"后面的覆盖前面的"这一 JVM 惯例——如果用户自己也传了跟官方参数冲突的 flag
        // （比如自己也想设置某个 --add-opens），用户的自定义意图优先生效。
        // 校验/解析失败时只记录日志、跳过自定义参数，不让程序崩溃或拼出语法错误的命令行。
        if (!string.IsNullOrWhiteSpace(opts.CustomJvmArgs))
        {
            try
            {
                var customArgs = SplitArgsRespectingQuotes(opts.CustomJvmArgs);
                if (customArgs.Count > 0) args.AddRange(customArgs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[XCL2] 自定义 JVM 参数解析失败，已忽略: {ex.Message}");
            }
        }

        var logging = LauncherLoggingPolicy.Resolve(detail);
        if (logging != null)
        {
            var loggingPath = LauncherLoggingPolicy.PathFor(opts.MinecraftDir, logging);
            if (!LauncherLoggingPolicy.IsVerified(loggingPath, logging))
                throw new MissingLibrariesException(opts.VersionId, new List<string> { "Log4j 日志安全配置" },
                    "Log4j 日志安全配置缺失或校验失败，已停止启动；请使用自动补全依赖后重试。");
            // Apply last so custom/profile arguments cannot silently disable the verified mitigation.
            LauncherLoggingPolicy.ApplyArguments(args, loggingPath);
        }

        args.Add("-cp");
        args.Add(string.Join(";", classpath.Distinct()));
        args.Add(mainClass);

        // 1.0 / Alpha / Beta 的官方模板以“玩家名 会话”开头。Launchwrapper 把未识别的
        // 现代选项也作为位置参数交给游戏；在前面塞 --username 会让玩家名和会话位置错位。
        // 此分支直接保留旧模板的顺序，1.6+ 的 --username 模板仍使用原有的现代路径。
        if (!usesManifestGameArguments) args.AddRange(new List<string>
        {
            "--username", opts.Account.Username,
            "--version", displayVersion,
            "--gameDir", gameDir,
            "--assetsDir", Path.Combine(opts.MinecraftDir, "assets"),
            "--assetIndex", assetsId,
            "--uuid", uuid,
            "--accessToken", accessToken,
            "--userType", opts.Account.Type == AccountType.Microsoft ? "msa" : "legacy",

            "--versionType", string.IsNullOrWhiteSpace(opts.VersionTypeLabel) ? "release" : opts.VersionTypeLabel,
            "--width", opts.WindowWidth.ToString(),
            "--height", opts.WindowHeight.ToString()
        });

        var currentFeatures = new Dictionary<string, bool>
        {
            ["is_demo_user"] = false,
            ["has_quick_playsupport"] = !string.IsNullOrWhiteSpace(opts.AutoJoinServerAddress),
            ["is_quick_play_multiplayer"] = !string.IsNullOrWhiteSpace(opts.AutoJoinServerAddress)
        };
        var gameArgsFromJson = new List<string>();
        if (detail.Arguments?.Game != null) gameArgsFromJson.AddRange(ParseArgumentEntries(detail.Arguments.Game, currentFeatures, argumentArchitecture));

        // 老格式 minecraftArguments（单个字符串）：之前完全没读，导致两类问题——
        //  1) 1.7.x/1.8.x 必填的 --userProperties 缺失，游戏一启动就 MissingRequiredOptionException 退出；
        //  2) OptiFine/老 Forge/LiteLoader 靠 "--tweakClass xxx" 注入，没传就等于启动原版。
        // 当前版本自己声明了就用当前的（loader json 通常是完整覆盖），否则回退到父版本的。
        // 新格式(arguments.game)已有内容时不再重复读取，避免同一参数出现两次。
        if (!string.IsNullOrWhiteSpace(legacyArgsText) && gameArgsFromJson.Count == 0)
        {
            // 模板错误不能静默忽略，否则游戏会以缺少身份/加载器参数的半套命令启动。
            gameArgsFromJson.AddRange(SplitArgsRespectingQuotes(legacyArgsText!));
        }

        // 双保险：上面 ParseArgumentEntries 已经按 features.is_demo_user 正确过滤了 --demo，
        // 理论上不会再混进来；这里再显式剔除一次，防止极端情况下（比如某个 version json
        // 用了未来才会出现的、目前还没识别的规则写法）漏判，导致离线/自定义账户被强制进入
        // Minecraft 的试玩模式。
        gameArgsFromJson.RemoveAll(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));

        // 已经手写过的这些键（连同各自的值）不再从 json 里重复追加，避免同一个参数出现两次。
        var alreadyHandledKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--username", "--version", "--gameDir", "--assetsDir", "--assetIndex", "--uuid",
            "--accessToken", "--userType", "--versionType", "--width", "--height"
        };
        if (usesManifestGameArguments) alreadyHandledKeys.Clear();

        static bool HasUnresolvedPlaceholder(string s) =>
            s.Contains("${") && s.Contains('}');

        var quickPlayMultiplayerInjected = false;
        var resolvedGameArgsFromJson = new List<string>();
        {
            var rawTokens = gameArgsFromJson
                .Select(a => SubstituteVariables(a, variables))
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .ToList();

            // 自动进入服务器：如果调用方提供了地址，把 --quickPlayMultiplayer 后面那个
            // 仍是 "${quickPlayMultiplayer}" 占位符的 token 原地替换成真实地址，这样它就不再
            // 满足下面 HasUnresolvedPlaceholder 的判定条件，不会被当成"给不出真实值"而丢弃。
            // 只处理这一个键，其余三个 quickPlay 系列参数（Path/Singleplayer/Realms）保持
            // 原有"没有值就整对丢弃"的行为不变——该功能目前只做了"进入指定服务器"这一种用法。
            if (!string.IsNullOrWhiteSpace(opts.AutoJoinServerAddress))
            {
                for (var i = 0; i < rawTokens.Count - 1; i++)
                {
                    if (string.Equals(rawTokens[i], "--quickPlayMultiplayer", StringComparison.OrdinalIgnoreCase)
                        && !rawTokens[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        if (quickPlayMultiplayerInjected)
                        {
                            rawTokens.RemoveRange(i, 2);
                            i--;
                            continue;
                        }
                        rawTokens[i + 1] = opts.AutoJoinServerAddress.Trim();
                        quickPlayMultiplayerInjected = true;
                    }
                }
            }

            for (var i = 0; i < rawTokens.Count; i++)
            {
                var token = rawTokens[i];
                if (token.StartsWith("--"))
                {
                    var hasValue = i + 1 < rawTokens.Count && !rawTokens[i + 1].StartsWith("--");
                    var value = hasValue ? rawTokens[i + 1] : null;
                    if (value != null && HasUnresolvedPlaceholder(value))
                    {
                        // 整对 key+value 都跳过：只丢 value、留下裸的 "--quickPlayPath" 这种 key
                        // 同样会让部分版本的参数解析报错(缺值)，必须连 key 一起丢。
                        i++; // 跳过 value，下一轮循环从 value 之后的 token 开始
                        continue;
                    }
                }
                resolvedGameArgsFromJson.Add(token);
            }
        }

        for (var i = 0; i < resolvedGameArgsFromJson.Count; i++)
        {
            var token = resolvedGameArgsFromJson[i];
            if (token.StartsWith("--") && alreadyHandledKeys.Contains(token))
            {
                // 该键上面已经手写过了，不再追加键本身；紧跟其后的值（如果有且不是另一个
                // "--xxx" 键）属于该键，一起跳过，避免它被当成下一轮循环的独立 token 误加进去。
                if (i + 1 < resolvedGameArgsFromJson.Count &&
                    !resolvedGameArgsFromJson[i + 1].StartsWith("--"))
                {
                    i++;
                }
                continue;
            }
            args.Add(token);
        }

        if (opts.RequireAutoJoinServerSupport && !quickPlayMultiplayerInjected)
            throw new NotSupportedException("该客户端清单没有声明可用的 Quick Play 服务器参数，无法使用 --server；请在游戏中手动连接服务器。");
        if (!usesManifestGameArguments && !quickPlayMultiplayerInjected && !string.IsNullOrWhiteSpace(opts.AutoJoinServerAddress))
        {
            args.Add("--quickPlayMultiplayer");
            args.Add(opts.AutoJoinServerAddress!.Trim());
        }

        // “强制使用”是 Minecraft 游戏进程本身的图形 API 强制项，不是启动器 UI 的渲染模式。
        // 26.2+ 已支持 --graphicsBackend，因此在写入游戏 options.txt 之外再追加启动期覆盖；
        // 26.1 不强塞未知参数，只保留上面的游戏 options.txt 设置。
        var graphicsPreference = NormalizeGraphicsApiPreference(opts.GraphicsApiPreference);
        if (graphicsPreference is InstanceConfigService.GraphicsApiOpenGl or InstanceConfigService.GraphicsApiVulkan &&
            SupportsForcedGraphicsBackendArgument(opts.VersionId, detail.Id, inheritance.Layers[0].Detail.Id))
        {
            SetOrAppendArgument(args, "--graphicsBackend", graphicsPreference);
        }

        // --lang 只在很老的版本(约 1.12 以前)里被读取，新版本已不认该参数，
        // 但加上也无害，作为老版本的兼容兜底一起传。真正生效的是上面写的 options.txt。
        if (!usesManifestGameArguments && !string.IsNullOrWhiteSpace(gameLanguage))
        {
            args.Add("--lang");
            args.Add(gameLanguage!);
        }

        if (opts.LaunchFullscreen && usesManifestGameArguments)
            throw new NotSupportedException("该早期客户端没有声明 --fullscreen 支持；请在游戏内切换全屏。");
        if (opts.LaunchFullscreen && !args.Contains("--fullscreen")) args.Add("--fullscreen");
        if (!string.IsNullOrWhiteSpace(opts.ExtraGameArgs)) args.AddRange(SplitArgsRespectingQuotes(opts.ExtraGameArgs));
        return (mainClass, args);
    }

    private static List<string> ParseArgumentEntries(List<object> entries, IReadOnlyDictionary<string, bool>? features = null,
        string? architecture = null)
    {
        var result = new List<string>();
        foreach (var entry in entries)
        {
            if (entry is not JsonElement el) continue;

            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
                continue;
            }

            if (el.ValueKind != JsonValueKind.Object) continue;

            if (!LaunchArgumentRulePolicy.Allows(el, features, architecture)) continue;

            if (!el.TryGetProperty("value", out var valueEl)) continue;

            if (valueEl.ValueKind == JsonValueKind.String)
            {
                var s = valueEl.GetString();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
            else if (valueEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in valueEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrEmpty(s)) result.Add(s);
                    }
                }
            }
        }
        return result;
    }

    /// <summary>把 "${xxx}" 形式的占位符替换成 variables 里对应的值；变量表里没有的占位符原样保留
    /// （官方 arguments.jvm 里偶尔会出现启动器不需要处理的占位符，保留原文比抛异常更安全）。</summary>
    private static string SubstituteVariables(string input, Dictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains("${")) return input;
        foreach (var kv in variables)
            input = input.Replace("${" + kv.Key + "}", kv.Value);
        return input;
    }

    private static string NormalizeGraphicsApiPreference(string? preference)
    {
        var normalized = preference?.Trim().ToLowerInvariant();
        return normalized is InstanceConfigService.GraphicsApiOpenGl or InstanceConfigService.GraphicsApiVulkan
            ? normalized
            : InstanceConfigService.GraphicsApiGame;
    }

    /// <summary>
    /// 把实例级“游戏内图形 API”选择写入实际 gameDir 的 options.txt。Minecraft 自己使用
    /// preferredGraphicsBackend，合法值是 "default" / "opengl" / "vulkan"。这里不碰任何
    /// 启动器窗口渲染设置；每次启动对应实例前只同步这一条游戏选项，其余 options.txt 内容原样保留。
    /// </summary>
    private static void ApplyGraphicsApiPreference(string gameDir, string preference)
    {
        var normalized = NormalizeGraphicsApiPreference(preference);
        var gameValue = normalized == InstanceConfigService.GraphicsApiGame ? "default" : normalized;
        var optionsPath = Path.Combine(gameDir, "options.txt");
        var replacement = $"preferredGraphicsBackend:\"{gameValue}\"";

        if (!File.Exists(optionsPath))
        {
            var initialLines = new List<string>(SkipFirstRunLines) { replacement };
            File.WriteAllText(optionsPath, string.Join("\n", initialLines) + "\n", Utf8NoBom);
            return;
        }

        var lines = File.ReadAllLines(optionsPath, Encoding.UTF8).ToList();
        var output = new List<string>(lines.Count + 1);
        var replaced = false;

        foreach (var line in lines)
        {
            if (!line.StartsWith("preferredGraphicsBackend:", StringComparison.OrdinalIgnoreCase))
            {
                output.Add(line);
                continue;
            }

            
            if (!replaced)
            {
                output.Add(replacement);
                replaced = true;
            }
        }

        if (!replaced) output.Add(replacement);
        File.WriteAllLines(optionsPath, output, Utf8NoBom);
    }

    private static bool SupportsForcedGraphicsBackendArgument(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var extracted = VersionInfoResolver.ExtractAnyVersion(candidate);
            if (string.IsNullOrWhiteSpace(extracted)) continue;

            var numeric = extracted.Split('-', 2)[0];
            var parts = numeric.Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
                continue;
            var patch = parts.Length > 2 && int.TryParse(parts[2], out var parsedPatch) ? parsedPatch : 0;

            if (major == 1)
            {
                if (minor > 26 || (minor == 26 && patch >= 2)) return true;
            }
            else if (major > 26 || (major == 26 && minor >= 2))
            {
                return true;
            }
        }
        return false;
    }

    private static void SetOrAppendArgument(List<string> args, string key, string value)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                    args[i + 1] = value;
                else
                    args.Add(value);
                return;
            }

            if (args[i].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                args[i] = key + "=" + value;
                return;
            }
        }

        args.Add(key);
        args.Add(value);
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly string[] SkipFirstRunLines =
    {
        "onboardAccessibility:false",
        "narrator:0",
        "tutorialStep:none",
        "skipMultiplayerWarning:true"
    };

    private static void ApplyGameLanguage(string gameDir, string lang)
    {
        var optionsPath = Path.Combine(gameDir, "options.txt");
        var langLine = $"lang:{lang}";

        if (!File.Exists(optionsPath))
        {
            var initialLines = new List<string> { langLine };
            initialLines.AddRange(SkipFirstRunLines);
            File.WriteAllText(optionsPath, string.Join("\n", initialLines) + "\n", Utf8NoBom);
            LogLanguageWrite(gameDir, lang, "created-minimal-skip-onboarding");
            return;
        }

        // File.ReadAllLines(path, Encoding) 在文件实际带 BOM 时会自动识别并剥离 BOM
        // （不论调用方传入的是哪个 Encoding 实例，BOM 检测是文件读取层做的），
        // 所以这里即使原文件是之前版本用带 BOM 的 Encoding.UTF8 写出来的坏文件，
        // 重新读取时也能正确去掉 BOM、按正常内容解析；真正需要修的是"写"这一步，
        // 确保重写之后不再产生新的 BOM，把污染源头断掉。
        var lines = File.ReadAllLines(optionsPath, Encoding.UTF8).ToList();
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            // options.txt 每行形如 "key:value"，用冒号分隔；只匹配行首的 "lang:" 避免误伤
            // 其他可能包含 "lang" 子串的无关字段。
            if (lines[i].StartsWith("lang:", StringComparison.Ordinal))
            {
                lines[i] = langLine;
                replaced = true;
                break;
            }
        }
        if (!replaced) lines.Add(langLine);

        File.WriteAllLines(optionsPath, lines, Utf8NoBom);
        LogLanguageWrite(gameDir, lang, replaced ? "updated-existing-line" : "appended-new-line");
    }

    /// <summary>
    /// 诊断日志：每次启动实际写了什么语言、走了哪条分支，追加到 xcl2/logs/language-debug.log。
    /// 如果之后还有人反馈"设了中文还是英文"，看这份日志就能立刻确定问题出在
    /// "根本没写进去/写错了内容"还是"确实写对了、但游戏没读该文件"(后者会是游戏本身的问题，
    /// 需要换排查方向，比如检查是不是有多个 options.txt 或者账户/服务器强制了语言)。
    /// </summary>
    private static void LogLanguageWrite(string gameDir, string lang, string branch)
    {
        try
        {
            var logPath = Path.Combine(App.DataDir, "logs", "language-debug.log");
            var optionsPath = Path.Combine(gameDir, "options.txt");
            var actualFirstLines = File.Exists(optionsPath)
                ? string.Join(" | ", File.ReadAllLines(optionsPath, Encoding.UTF8).Take(3))
                : "(文件不存在)";
            File.AppendAllText(logPath,
                $"[{DateTime.Now}] gameDir={gameDir} lang={lang} branch={branch} 写入后前3行=[{actualFirstLines}]\n");
        }
        catch { /* 诊断日志失败不应该影响正常启动流程 */ }
    }
}
