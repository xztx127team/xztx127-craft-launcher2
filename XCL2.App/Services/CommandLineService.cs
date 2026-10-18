using System;
using System.Collections.Generic;
using System.Globalization;

namespace XCL2.App.Services;

/// <summary>
/// 启动器命令行参数解析。
///
/// 支持的参数：
///   -help                                查看帮助并退出
///   -r --账户名 &lt;name&gt; --实例名称 &lt;name&gt;    用指定账户 + 指定实例（版本）直接启动游戏
///   -gui &lt;页面名&gt;                         启动后跳转到对应页面（复用现有的页面搜索关键字表）
///   --d --版本 &lt;mcVersion&gt; --加载器 &lt;loader&gt; 打开下载中心；--版本 留空时会弹出下载页面让用户自行选择，
///                                          填写时会自动跳到下载中心并按版本号筛选好，--加载器可选
///
/// 参数之间不区分大小写，"--参数 值" 和 "--参数=值" 两种写法都支持，方便脚本调用。
/// 新增 --debug、--play、--open、页面快捷方式、窗口尺寸、会话内存与减弱动效；
/// 以单遍消费方式保留参数值，不对值中的选项字样再次扫描。尚未接入的旧进阶项明确报错，不接受无效果的设置。
/// 只做“解析”，不做任何跳转/启动的具体动作——具体动作由 App.xaml.cs / MainWindow 消费本类结果后执行，
/// 这样解析逻辑本身可以脱离 WPF 独立测试。
/// </summary>
public static class CommandLineService
{
    public sealed class ParsedArgs
    {
        /// <summary>命中 -help，只需要打印帮助然后退出，不需要往下走任何其它逻辑。</summary>
        public bool ShowHelp;
        /// <summary>--debug：仅本次会话开放启动器调试面板和 F3 组合键，不额外创建控制台。</summary>
        public bool DebugMode;
        /// <summary>--debug-panel：同时启用调试模式，在首帧后打开信息面板。</summary>
        public bool ShowDebugPanel;
        /// <summary>错误由入口统一展示；有错误时不得执行启动、清理等命令行动作。</summary>
        public List<string> Diagnostics { get; } = new();
        public bool HasErrors => Diagnostics.Count != 0;

        /// <summary>命中 -r，需要用指定账户+实例直接启动游戏。</summary>
        public bool LaunchGame;
        /// <summary>--play 可省略账户名，使用当前选中的账户；旧 -r 不使用此默认行为。</summary>
        public bool UseSelectedAccount;
        public string? AccountName;
        public string? InstanceName;
        public string? ResultFile;
        public bool ListInstances;
        public bool ListAccounts;

        /// <summary>命中 -gui &lt;page&gt;，需要跳转到的页面名（原样传入，交给 MainWindow 的页面关键字表去匹配）。</summary>
        public string? GuiPage;

        /// <summary>命中 --d，需要打开下载中心；Version 为空时弹出下载页面让用户手动选。</summary>
        public bool OpenDownload;
        public string? DownloadVersion;
        public string? DownloadLoader;

        /// <summary>用户显式输入 -l；先重启一次，再转换成隐藏的 --guest-session。</summary>
        public bool GuestModeRelaunchRequested;
        /// <summary>隐藏参数：当前这个进程就是一次性访客会话，不再二次重启。</summary>
        public bool EnterGuestMode;

        /// <summary>隐藏参数 --preview-session：仅由 MainWindow.RequestPreviewModeRestart 生成的重启
        /// 进程带上，标记"这次启动只用来展示新界面预览"。跟 --guest-session 同一套模式：磁盘配置
        /// 永远不记录这个状态，只影响当次进程——正常双击启动、或者用户在预览里点"关闭预览"触发的
        /// 下一次重启都不会再带这个参数，从而回到原有默认界面，不会重新进入/停留在预览里。</summary>
        public bool EnterPreviewMode;
        /// <summary>隐藏参数：开机 Run 项启动，用来应用“弹出/最小化/托盘”行为。</summary>
        public bool StartedByAutoStart;
        /// <summary>隐藏参数：重启接力时等待旧进程 PID 退出，避免误撞多开检测。</summary>
        public int WaitForPid;

        /// <summary>--skip-update-check：仅本次跳过启动器自动更新检查。</summary>
        public bool SkipUpdateCheck;
        /// <summary>仅本次会话的启动器窗口尺寸。</summary>
        public int? WindowWidth;
        public int? WindowHeight;
        /// <summary>--reduced-motion：仅本次关闭界面动效。</summary>
        public bool DisableAnimations;
        public bool DebugAprilFools;
        /// <summary>游戏启动时使用的显式 Java 路径；不存在时报告错误。</summary>
        public string? JavaPathOverride;
        public int? MinMemoryMb;
        public int? MaxMemoryMb;
        /// <summary>用户明确指定的本次 JVM 参数；在保存的高级参数之后追加。</summary>
        public string? ExtraJvmArgs;
        public string? ExtraGameArgs;
        public string? AutoJoinServer;
        public bool LaunchFullscreen;

        // Retained CLR fields for already compiled callers. Unsupported switches report diagnostics and never set these fields.
        [Obsolete("Unsupported CLI option; parsing reports an error.")]
        public bool CheckUpdateOnly, ForceOffline, ForcePortable, DisableTray, StartMinimized, StartMaximized,
            ResetWindowLayout, SafeMode, ShowDebugConsole, ClearCacheOnStart, DisableSingleInstance, DisableGpuAcceleration, VerboseLogging;
        [Obsolete("Unsupported CLI option; parsing reports an error.")]
        public string? LanguageOverride, ThemeOverride, ConfigPathOverride, InstanceDirOverride, ExportLogsPath;
        [Obsolete("Unsupported CLI option; parsing reports an error.")]
        public int? UiZoomPercent;

        /// <summary>隐藏参数 --just-updated：只由 UpdateCheckService 生成的重启脚本自动带上，
        /// 标记\"这次启动是自动更新流程重启回来的那一次\"，不写进帮助文本、也不建议用户手动使用。
        /// MainWindow 首帧后据此决定是否弹出「这次更新了什么」的内嵌变更日志弹窗，见
        /// MainWindow.ApplyStartupArgs 和 AppConfig.ShowUpdateChangelogPopup。</summary>
        public bool JustUpdated;

        /// <summary>是否解析出了任何一个需要 MainWindow 首帧后处理的有效动作。</summary>
        public bool HasAnyAction => ShowHelp || ShowDebugPanel || LaunchGame || ListInstances || ListAccounts || GuiPage != null || OpenDownload || EnterGuestMode ||
            EnterPreviewMode || StartedByAutoStart || JustUpdated;
    }

    public const string HelpText = """
XCL2 启动器 · 命令行帮助

常用启动参数：
  --help / -help / -h                  显示帮助并退出
  --debug                             开启调试模式；F3 显示/隐藏实时调试面板
  --debug --help                      查看调试面板和全部 F3 组合键
  --debug-panel                       开启调试模式并直接显示面板，无需再按 F3
  --play <实例名>                     启动指定实例，默认使用当前选中的账户
  --account <账户名>                  与 --play 配合选择账户；名称含空格时加引号
  --open <页面名>                     打开指定页面，例如 设置、下载中心、日志、百宝箱
  --settings / --downloads / --logs   直接打开设置 / 下载中心 / 日志
  --home / --instances               直接打开首页 / 版本管理
  --performance                      打开性能悬浮窗详细设置，不自动开启监控
  --window-size <宽>x<高>             指定启动器窗口尺寸，例如 1280x800
  --width <像素> --height <像素>       分别指定启动器窗口宽度、高度
  --memory <最大值>                   覆盖本次游戏最大堆，例如 2G、2048M；最小堆不超过最大堆
  --memory <最小值>:<最大值>          明确指定游戏堆范围，例如 500M:8G
  --min-memory <MB> --max-memory <MB>  分别指定本次游戏最小堆、最大堆
  --reduced-motion / --no-animation   本次关闭界面动效；不保存到设置

中文写法（与上面等效）：
  --帮助、--调试、--调试面板、--首页、--实例、--设置、--下载、--日志、--性能设置
  --启动 <实例名>、--打开 <页面名>、--内存 <最大值或范围>、--减少动画

原有启动方式（继续支持）：
  -r --账户名 <账户名> --实例名称 <实例名>  使用指定账户与实例启动游戏
  -gui <页面名>                           等同 --open <页面名>
  --d [--版本 <版本号>] [--加载器 <名称>]   打开下载中心；版本、加载器可选
  --list-instances / --list-accounts       查询实例 / 账户，不输出账户凭据
  --result-file <路径>                    将启动操作的实际进程结果写入 JSON
  -l                                      重启进入一次性访客会话

写法说明：
  参数名不区分大小写；支持 --参数=值；名称和路径保留原大小写。
  带空格的值需要由调用方加引号，例如 --play "My Fabric"。
  值本身以 - 开头时请用等号，例如 --jvm-args=-Dexample=true；不会把值当成启动器选项。
  窗口输入宽度 320–16384、高度 240–16384 像素，实际按界面最小尺寸与可用桌面调整。
  内存为正整数 M/MB 或 G/GB（1G=1024M）。
  单个内存值只设置最大堆；范围必须最小值不大于最大值。未知参数或无效值会说明错误并停止动作。

本次游戏启动选项（不保存到设置）：
  --java <路径>                       使用这个 Java 文件并跳过自动替换；不存在时说明错误
  --jvm-args <参数>                   追加 JVM 参数；以 - 开头时请使用 --jvm-args=值
  --game-args <参数>                  追加游戏参数；请按目标游戏版本填写
  --server <地址>                     使用客户端清单声明的 Quick Play 进入服务器；不支持的版本会说明原因
  --fullscreen                        游戏全屏启动；位置身份参数的远古版本不支持时会说明原因
  --skip-update-check                 仅本次跳过启动器自动更新检查

示例：
  XCL2.exe --play "1.21.1-Fabric" --account Steve --memory 500M:4G
  XCL2.exe --settings --window-size 1280x800 --reduced-motion
  XCL2.exe --d --version 1.21.1 --loader Fabric
  XCL2.exe --debug --help
  XCL2.exe --调试面板 --首页
  XCL2.exe --性能设置
""";

    public static string DebugHelpText => """
XCL2 启动器 · 调试模式帮助

  XCL2.exe --debug        在本次启动启用调试功能，不修改长期设置，不额外弹出控制台。
  XCL2.exe --debug --help  显示本帮助并退出；查看普通参数请使用 --help。
  XCL2.exe --debug-panel  启用调试并在启动后直接打开信息面板。

F3 显示类似 Minecraft 调试信息的实时面板；显示的是当前启动器会话数据。
""" + "\n\n" + LauncherDebugShortcuts.HelpText + "\n\n" + """
调试参数：
  --debug -yrj  本次会话无视日期启用愚人节彩蛋，仍尊重 noyrj 禁用设置。

常用参数也可组合：
  XCL2.exe --debug --logs --window-size 1280x800
  XCL2.exe --debug --play "1.21.1-Fabric" --account Steve --memory 2G
""";

    public static string HelpFor(ParsedArgs args) => args.DebugMode ? DebugHelpText : HelpText;

    public static ParsedArgs Parse(string[]? args)
    {
        var result = new ParsedArgs();
        if (args == null || args.Length == 0) return result;
        bool aprilFools = false;
        for (int i = 0; i < args.Length; i++)
        {
            var raw = args[i] ?? "";
            if (raw.Length == 0) { result.Diagnostics.Add("出现了空的位置参数；请把值写在对应参数后。"); continue; }
            var equal = raw.IndexOf('=');
            var name = equal >= 0 ? raw[..equal] : raw;
            string? inlineValue = equal >= 0 ? raw[(equal + 1)..] : null;
            name = name.ToLowerInvariant();

            bool Flag(Action set)
            {
                if (inlineValue != null)
                { result.Diagnostics.Add($"参数 {name} 不接受值；请直接使用 {name}。"); return false; }
                set(); return true;
            }
            string? Value()
            {
                if (inlineValue != null)
                {
                    if (!string.IsNullOrWhiteSpace(inlineValue)) return inlineValue;
                    result.Diagnostics.Add($"参数 {name} 缺少非空值。"); return null;
                }
                if (i + 1 >= args.Length || IsOptionToken(args[i + 1]))
                { result.Diagnostics.Add($"参数 {name} 缺少值；以 - 开头的值请使用 {name}=值。"); return null; }
                var value = args[++i];
                if (!string.IsNullOrWhiteSpace(value)) return value;
                result.Diagnostics.Add($"参数 {name} 缺少非空值。"); return null;
            }
            int? Number(int minimum, int maximum, string unit)
            {
                var value = Value();
                if (value == null) return null;
                if (TryPositiveInteger(value, minimum, maximum, out var number)) return number;
                result.Diagnostics.Add($"参数 {name} 需要 {minimum}–{maximum} 范围内的整数{unit}。"); return null;
            }

            switch (name)
            {
                case "-help": case "--help": case "-h": case "/?": case "--帮助": Flag(() => result.ShowHelp = true); break;
                case "--debug": case "--调试": Flag(() => result.DebugMode = true); break;
                case "--debug-panel": case "--调试面板": Flag(() => { result.DebugMode = true; result.ShowDebugPanel = true; }); break;
                case "-yrj": Flag(() => aprilFools = true); break;
                case "-r": Flag(() => result.LaunchGame = true); break;
                case "--play": case "--启动":
                    var instance = Value();
                    if (instance != null) { result.LaunchGame = true; result.UseSelectedAccount = true; result.InstanceName = instance; }
                    break;
                case "--账户名": case "--account": result.AccountName = Value(); break;
                case "--实例名称": case "--instance": result.InstanceName = Value(); break;
                case "-gui": case "--open": case "--打开": result.GuiPage = Value(); break;
                case "--settings": case "--设置": Flag(() => result.GuiPage = "设置"); break;
                case "--downloads": case "--下载": Flag(() => result.GuiPage = "下载中心"); break;
                case "--logs": case "--日志": Flag(() => result.GuiPage = "日志"); break;
                case "--home": case "--首页": Flag(() => result.GuiPage = "首页"); break;
                case "--instances": case "--实例": Flag(() => result.GuiPage = "版本管理"); break;
                case "--performance": case "--性能设置": Flag(() => result.GuiPage = "性能悬浮窗"); break;
                case "--d": Flag(() => result.OpenDownload = true); break;
                case "--版本": case "--version": result.DownloadVersion = Value(); break;
                case "--加载器": case "--loader": result.DownloadLoader = Value(); break;
                case "--list-instances": Flag(() => result.ListInstances = true); break;
                case "--list-accounts": Flag(() => result.ListAccounts = true); break;
                case "--result-file": result.ResultFile = Value(); break;
                case "-l": Flag(() => result.GuestModeRelaunchRequested = true); break;
                case "--guest-session": Flag(() => result.EnterGuestMode = true); break;
                case "--preview-session": Flag(() => result.EnterPreviewMode = true); break;
                case "--autostart": Flag(() => result.StartedByAutoStart = true); break;
                case "--wait-pid": result.WaitForPid = Number(1, int.MaxValue, "") ?? 0; break;
                case "--just-updated": Flag(() => result.JustUpdated = true); break;
                case "--window-size":
                    var size = Value();
                    if (size != null)
                    {
                        var parts = size.Split(new[] { 'x', 'X', '×' });
                        if (parts.Length == 2 && TryPositiveInteger(parts[0], 320, 16384, out var width)
                            && TryPositiveInteger(parts[1], 240, 16384, out var height))
                        { result.WindowWidth = width; result.WindowHeight = height; }
                        else result.Diagnostics.Add("参数 --window-size 需要 宽x高，例如 1280x800；宽 320–16384、高 240–16384 像素。");
                    }
                    break;
                case "--width": result.WindowWidth = Number(320, 16384, "像素"); break;
                case "--height": result.WindowHeight = Number(240, 16384, "像素"); break;
                case "--memory": case "--内存":
                    var memory = Value();
                    if (memory != null)
                    {
                        var parts = memory.Split(':');
                        if (parts.Length == 1 && TryMemory(parts[0], out var max)) result.MaxMemoryMb = max;
                        else if (parts.Length == 2 && TryMemory(parts[0], out var min) && TryMemory(parts[1], out max) && min <= max)
                        { result.MinMemoryMb = min; result.MaxMemoryMb = max; }
                        else result.Diagnostics.Add("参数 --memory 需要正整数内存值或 最小值:最大值，例如 2G、2048M、500M:8G；最小值不能大于最大值。");
                    }
                    break;
                case "--min-memory": result.MinMemoryMb = Number(1, 1_048_576, "MB"); break;
                case "--max-memory": result.MaxMemoryMb = Number(1, 1_048_576, "MB"); break;
                case "--reduced-motion": case "--no-animation": case "--no-animations": case "--减少动画": Flag(() => result.DisableAnimations = true); break;
                case "--skip-update-check": Flag(() => result.SkipUpdateCheck = true); break;
                case "--java": result.JavaPathOverride = Value(); break;
                case "--jvm-args": result.ExtraJvmArgs = Value(); break;
                case "--game-args": result.ExtraGameArgs = Value(); break;
                case "--server": result.AutoJoinServer = Value(); break;
                case "--fullscreen": Flag(() => result.LaunchFullscreen = true); break;
                case "--check-update-only": case "--offline": case "--portable": case "--no-tray":
                case "--minimized": case "--maximized": case "--reset-window": case "--safe-mode":
                case "--debug-console": case "--verbose": case "--clear-cache": case "--no-single-instance": case "--no-gpu-accel":
                    Flag(() => { });
                    result.Diagnostics.Add($"参数 {name} 尚未支持，未执行任何操作；请移除此参数后重试，使用 --help 查看当前支持项。");
                    break;
                case "--lang": case "--theme": case "--zoom": case "--config-path": case "--instance-dir": case "--export-logs":
                    _ = Value();
                    result.Diagnostics.Add($"参数 {name} 尚未支持，未执行任何操作；请移除此参数后重试，使用 --help 查看当前支持项。");
                    break;
                default:
                    result.Diagnostics.Add(IsOptionToken(raw) ? $"未知参数 {name}；使用 --help 查看支持的参数。"
                        : $"无法识别位置参数“{raw}”；请使用 --play、--open 等明确指定用途。");
                    break;
            }
        }
        result.DebugAprilFools = result.DebugMode && aprilFools;
        if (aprilFools && !result.DebugMode) result.Diagnostics.Add("参数 -yrj 需要和 --debug 一起使用。");
        foreach (var (option, text) in new[] { ("--jvm-args", result.ExtraJvmArgs), ("--game-args", result.ExtraGameArgs) })
            if (text != null && text.Count(character => character == '"') % 2 != 0)
                result.Diagnostics.Add($"参数 {option} 包含未闭合的双引号，请修正后重试。");
        if (result.MinMemoryMb is int minimumMemory && result.MaxMemoryMb is int maximumMemory && minimumMemory > maximumMemory)
            result.Diagnostics.Add("最小堆内存不能大于最大堆内存；请检查 --memory、--min-memory 和 --max-memory。");
        return result;
    }

    private static bool IsOptionToken(string? text) => text != null && (text.StartsWith('-') || text == "/?");

    private static bool TryPositiveInteger(string text, int minimum, int maximum, out int number)
        => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= minimum && number <= maximum;

    private static bool TryMemory(string text, out int megabytes)
    {
        megabytes = 0;
        var value = text.Trim();
        long multiplier = 1;
        if (value.EndsWith("GB", StringComparison.OrdinalIgnoreCase)) { multiplier = 1024; value = value[..^2]; }
        else if (value.EndsWith("G", StringComparison.OrdinalIgnoreCase)) { multiplier = 1024; value = value[..^1]; }
        else if (value.EndsWith("MB", StringComparison.OrdinalIgnoreCase)) value = value[..^2];
        else if (value.EndsWith("M", StringComparison.OrdinalIgnoreCase)) value = value[..^1];
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0
            || number > 1_048_576 / multiplier) return false;
        megabytes = (int)(number * multiplier); return true;
    }
}
