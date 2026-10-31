using XCL2.App.Services;

int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new Exception(message);
}
CommandLineService.ParsedArgs Parse(params string[] values) => CommandLineService.Parse(values);
void Valid(CommandLineService.ParsedArgs parsed, string message)
    => Check(!parsed.HasErrors, message + ": " + string.Join("; ", parsed.Diagnostics));
void Invalid(string option, string value)
{
    var parsed = Parse(option, value);
    Check(parsed.HasErrors && parsed.Diagnostics.Count != 0, "invalid value rejected: " + option + " " + value);
}

foreach (var values in new string[]?[] { null, Array.Empty<string>() })
{
    var parsed = CommandLineService.Parse(values);
    Valid(parsed, "empty arguments are valid");
    Check(!parsed.HasAnyAction && !parsed.DebugMode && !parsed.LaunchGame, "empty arguments preserve ordinary startup");
}
{
    var debug = Parse("--debug");
    Valid(debug, "debug startup");
    Check(debug.DebugMode && !debug.ShowHelp, "debug does not silently open console or help");
    var console = Parse("--debug-console");
    Check(console.HasErrors && !console.DebugMode, "unimplemented console option is rejected instead of succeeding without a console");
    foreach (var values in new[] { new[] { "--debug", "-yrj" }, new[] { "-yrj", "--DEBUG" } })
        Check(Parse(values).DebugAprilFools, "legacy debug Easter egg supports either token order");
    Check(!Parse("-yrj").DebugAprilFools && !Parse("--debug").DebugAprilFools, "Easter egg still requires both flags");
}
foreach (var helpFlag in new[] { "--help", "-help", "-h", "/?" })
{
    var parsed = Parse(helpFlag);
    Valid(parsed, "help alias");
    Check(parsed.ShowHelp && CommandLineService.HelpFor(parsed) == CommandLineService.HelpText,
        "ordinary help selection: " + helpFlag);
    var debug = Parse(helpFlag, "--debug");
    Check(debug.ShowHelp && debug.DebugMode && CommandLineService.HelpFor(debug) == CommandLineService.DebugHelpText,
        "debug help selection works in either order: " + helpFlag);
}
Check(CommandLineService.DebugHelpText.Contains(LauncherDebugShortcuts.HelpText)
    && CommandLineService.DebugHelpText.Contains("Minecraft"), "debug help uses the actual shared F3 shortcut table");
foreach (var flag in new[] { "--debug-panel", "--调试面板" })
{
    var parsed = Parse(flag);
    Valid(parsed, "open debug panel");
    Check(parsed.DebugMode && parsed.ShowDebugPanel && parsed.HasAnyAction, "direct panel flag arms both debug and first-frame action");
    Check(Parse(flag, "--帮助").ShowHelp, "help wins over direct panel action at startup");
    Check(Parse(flag + "=false").HasErrors && !Parse(flag + "=false").ShowDebugPanel, "panel boolean assignment is rejected");
}
foreach (var pair in new[] { ("--home", "首页"), ("--首页", "首页"), ("--instances", "版本管理"), ("--实例", "版本管理"),
    ("--performance", "性能悬浮窗"), ("--性能设置", "性能悬浮窗"), ("--设置", "设置"), ("--下载", "下载中心"), ("--日志", "日志") })
{
    var parsed = Parse(pair.Item1);
    Valid(parsed, "friendly page argument");
    Check(parsed.GuiPage == pair.Item2 && parsed.HasAnyAction, "page shortcut: " + pair.Item1);
}
var chinese = Parse("--调试", "--启动", "我的 Fabric", "--内存", "500M:4G", "--减少动画");
Valid(chinese, "Chinese readable options");
Check(chinese.DebugMode && chinese.LaunchGame && chinese.UseSelectedAccount && chinese.InstanceName == "我的 Fabric"
    && chinese.MinMemoryMb == 500 && chinese.MaxMemoryMb == 4096 && chinese.DisableAnimations, "Chinese options preserve launch values and units");
Check(Parse("--打开=日志").GuiPage == "日志", "Chinese open alias supports equals");
Check(Parse("--调试", "--帮助").ShowHelp && Parse("--调试", "--帮助").DebugMode, "Chinese debug help");
Check(Parse("--settings", "--性能设置", "--home").GuiPage == "首页", "new page switches respect last explicit selection");
foreach (var hidden in new[] { "--guest-session", "--preview-session", "--wait-pid", "--autostart", "--just-updated" })
    Check(!CommandLineService.HelpText.Contains(hidden) && !CommandLineService.DebugHelpText.Contains(hidden),
        "internal handoff token is not advertised: " + hidden);

{
    var parsed = Parse("--play", "My Fabric", "--account", "CaseSensitive Steve");
    Valid(parsed, "friendly launch");
    Check(parsed.LaunchGame && parsed.UseSelectedAccount && parsed.InstanceName == "My Fabric"
        && parsed.AccountName == "CaseSensitive Steve", "friendly launch preserves full original value case and spaces");
    var selectedAccount = Parse("--play=1.21.1-Fabric");
    Check(selectedAccount.LaunchGame && selectedAccount.UseSelectedAccount && selectedAccount.AccountName == null,
        "play can delegate to current selected account without fabricating a name");
    var legacy = Parse("-r", "--账户名=Steve", "--实例名称", "Legacy Instance");
    Valid(legacy, "legacy Chinese launch aliases");
    Check(legacy.LaunchGame && !legacy.UseSelectedAccount && legacy.AccountName == "Steve" && legacy.InstanceName == "Legacy Instance",
        "legacy -r retains its explicit-account contract");
    var english = Parse("-R", "--ACCOUNT=Steve", "--INSTANCE=English Instance");
    Check(english.LaunchGame && !english.UseSelectedAccount && english.AccountName == "Steve" && english.InstanceName == "English Instance",
        "case-insensitive option names do not lowercase values");
}
foreach (var pair in new[] { ("--settings", "设置"), ("--downloads", "下载中心"), ("--logs", "日志") })
{
    var parsed = Parse(pair.Item1);
    Valid(parsed, "navigation alias");
    Check(parsed.GuiPage == pair.Item2 && parsed.HasAnyAction, "friendly page alias: " + pair.Item1);
}
foreach (var flag in new[] { "--open", "-gui" })
{
    var parsed = Parse(flag + "=百宝箱");
    Valid(parsed, "open with inline value");
    Check(parsed.GuiPage == "百宝箱", "page alias supports equals form: " + flag);
}
{
    var parsed = Parse("--d", "--版本", "1.21.1", "--加载器=Fabric");
    Valid(parsed, "legacy download aliases");
    Check(parsed.OpenDownload && parsed.DownloadVersion == "1.21.1" && parsed.DownloadLoader == "Fabric", "download filter values preserved");
    Check(Parse("--d").OpenDownload && Parse("--d").DownloadVersion == null, "version remains optional for the download dialog");
    var english = Parse("--d", "--version=1.20.1", "--loader=NeoForge");
    Check(english.DownloadLoader == "NeoForge" && english.DownloadVersion == "1.20.1", "English filter aliases retained");
}
{
    var parsed = Parse("--list-instances", "--list-accounts", "--result-file=C:\\Reports\\Launch Result.json");
    Valid(parsed, "structured query");
    Check(parsed.ListInstances && parsed.ListAccounts && parsed.ResultFile == "C:\\Reports\\Launch Result.json" && parsed.HasAnyAction,
        "structured result paths and listing flags remain intact");
    var handoff = Parse("--guest-session", "--preview-session", "--autostart", "--just-updated", "--wait-pid=1234");
    Valid(handoff, "hidden relaunch handoff");
    Check(handoff.EnterGuestMode && handoff.EnterPreviewMode && handoff.StartedByAutoStart && handoff.JustUpdated
        && handoff.WaitForPid == 1234, "hidden handoff flags remain consumable without help advertising");
    Check(Parse("-l").GuestModeRelaunchRequested, "legacy guest relaunch request retained");
}

// Sequential consumption prevents option-looking strings inside explicit values
// from being rescanned as active flags, help, or the hidden handoff protocol.
{
    var parsed = Parse("--jvm-args=-Dcaption=--debug --help", "--game-args=--guest-session -yrj",
        "--account=--debug", "--instance=--help", "--result-file=--just-updated");
    Valid(parsed, "option-shaped inline values");
    Check(!parsed.DebugMode && !parsed.ShowHelp && !parsed.EnterGuestMode && !parsed.JustUpdated && !parsed.DebugAprilFools,
        "options inside values never activate launcher flags");
    Check(parsed.ExtraJvmArgs == "-Dcaption=--debug --help" && parsed.ExtraGameArgs == "--guest-session -yrj"
        && parsed.AccountName == "--debug" && parsed.InstanceName == "--help" && parsed.ResultFile == "--just-updated",
        "inline values are retained exactly once without splitting at embedded equals or spaces");
    var spacedValue = Parse("--open", "菜单 --help --debug");
    Check(spacedValue.GuiPage == "菜单 --help --debug" && !spacedValue.ShowHelp && !spacedValue.DebugMode,
        "an ordinary consumed value containing flag words is not rescanned");
    var missingBeforeHelp = Parse("--jvm-args", "--help");
    Check(missingBeforeHelp.HasErrors && missingBeforeHelp.ShowHelp && missingBeforeHelp.ExtraJvmArgs == null,
        "missing JVM value cannot swallow launcher help as executable argument text");
}
foreach (var option in new[] { "--play", "--account", "--open", "-gui", "--memory", "--window-size", "--width", "--wait-pid", "--jvm-args" })
{
    Check(Parse(option).HasErrors, "missing terminal value reported: " + option);
    Check(Parse(option + "=").HasErrors, "empty equals value reported: " + option);
    var beforeFlag = Parse(option, "--debug");
    Check(beforeFlag.HasErrors && beforeFlag.DebugMode, "missing value does not consume the following flag: " + option);
}
foreach (var value in new[] { "--does-not-exist", "-typo", "unexpected positional value", "" })
    Check(Parse(value).HasErrors, "unknown option or position has a readable diagnostic: " + value);
foreach (var value in new[] { "--debug=true", "--settings=日志", "--help=please", "--guest-session=false" })
    Check(Parse(value).HasErrors, "boolean flag rejects a value: " + value);
Check(!Parse("--debug=false").DebugMode && !Parse("--help=false").ShowHelp,
    "invalid flag assignments never enable their action");

foreach (var separator in new[] { "x", "X", "×" })
{
    var parsed = Parse("--window-size", "1280" + separator + "800");
    Valid(parsed, "window size separator");
    Check(parsed.WindowWidth == 1280 && parsed.WindowHeight == 800, "window dimensions use launcher fields: " + separator);
}
{
    var parsed = Parse("--window-size=1280x800", "--width=1440", "--height", "900");
    Valid(parsed, "last explicit dimensions win");
    Check(parsed.WindowWidth == 1440 && parsed.WindowHeight == 900, "dimension aliases obey token order");
    var reverse = Parse("--width=1440", "--height=900", "--window-size=1280x800");
    Check(reverse.WindowWidth == 1280 && reverse.WindowHeight == 800, "combined size can replace earlier dimensions");
    var bad = Parse("--window-size=1280xNaN");
    Check(bad.HasErrors && bad.WindowWidth == null && bad.WindowHeight == null, "invalid combined size never applies half a size");
}
foreach (var invalid in new[] { "1280", "1280x800x1", "NaNx800", "Infinityx800", "+1280x800", "319x800", "1280x239", "16385x800", "1280x16385" })
    Invalid("--window-size", invalid);
foreach (var invalid in new[] { "0", "-1", "+1280", "1e3", "Infinity", "NaN", "2147483648", "12.5", "1,280" })
    Invalid("--width", invalid);

foreach (var pair in new[] { ("2G", 2048), ("2gB", 2048), ("2048M", 2048), ("2048mb", 2048), ("2048", 2048), ("1024G", 1_048_576) })
{
    var parsed = Parse("--memory=" + pair.Item1);
    Valid(parsed, "memory quantity");
    Check(parsed.MaxMemoryMb == pair.Item2 && parsed.MinMemoryMb == null, "single memory amount only overrides maximum heap: " + pair.Item1);
}
{
    var parsed = Parse("--memory", "500M:8G");
    Valid(parsed, "memory range");
    Check(parsed.MinMemoryMb == 500 && parsed.MaxMemoryMb == 8192, "range explicitly sets both MB fields");
    var original = Parse("--min-memory=768", "--max-memory", "4096");
    Valid(original, "legacy memory overrides");
    Check(original.MinMemoryMb == 768 && original.MaxMemoryMb == 4096, "legacy memory flags remain integer MB");
    var overwritten = Parse("--memory=500M:8G", "--max-memory=4096");
    Check(overwritten.MinMemoryMb == 500 && overwritten.MaxMemoryMb == 4096, "mixed memory aliases apply in argument order");
    Check(Parse("--min-memory=4096", "--memory=2G").HasErrors,
        "an explicitly oversized minimum is reported rather than silently changed");
}
foreach (var invalid in new[] { "0", "0G", "-1G", "+2G", "2.5G", "1e3M", "Infinity", "NaN", "1T", "1025G", "1048577M", "4G:500M", ":8G", "500M:", "1G:2G:3G" })
    Invalid("--memory", invalid);
foreach (var invalid in new[] { "0", "-1", "NaN", "Infinity", "2048M", "1e3", "2147483648" })
    Invalid("--min-memory", invalid);
Check(Parse("--min-memory=4096", "--max-memory=2048").HasErrors, "minimum cannot exceed maximum across old flags");
{
    Check(CommandLineSessionPolicy.ResolveMemory(1024, 4096, null, null) == (1024, 4096),
        "no session memory override retains effective instance/dynamic values");
    Check(CommandLineSessionPolicy.ResolveMemory(4096, 8192, null, 2048) == (2048, 2048),
        "maximum-only override lowers an oversized effective minimum without changing stored configuration");
    Check(CommandLineSessionPolicy.ResolveMemory(1024, 4096, 6144, null) == (6144, 6144),
        "minimum-only override raises an undersized effective maximum");
    Check(CommandLineSessionPolicy.ResolveMemory(1024, 4096, 500, 8192) == (500, 8192),
        "explicit valid session range is used exactly");
    var parsed = Parse("--memory=2G");
    Check(CommandLineSessionPolicy.ResolveMemory(4096, 8192, parsed.MinMemoryMb, parsed.MaxMemoryMb) == (2048, 2048),
        "production parser's single amount integrates with the actual session memory policy");
}
foreach (var flag in new[] { "--reduced-motion", "--no-animation", "--no-animations" })
{
    var parsed = Parse(flag);
    Valid(parsed, "animation alias");
    Check(parsed.DisableAnimations, "all reduced-motion aliases select the existing session override: " + flag);
}

// Unsupported legacy switches must fail clearly; supported session overrides retain exact values.
foreach (var option in new[] { "--check-update-only", "--offline", "--portable", "--no-tray", "--minimized", "--maximized",
    "--reset-window", "--safe-mode", "--verbose", "--clear-cache", "--no-single-instance", "--no-gpu-accel", "--debug-console" })
    Check(Parse(option).HasErrors, "unsupported switch cannot silently succeed: " + option);
foreach (var option in new[] { "--lang", "--theme", "--zoom", "--config-path", "--instance-dir", "--export-logs" })
    Check(Parse(option + "=value").HasErrors, "unsupported value option cannot silently succeed: " + option);
{
    var parsed = Parse("--skip-update-check", "--fullscreen", "--java=C:\\Java\\javaw.exe", "--jvm-args=-Dexample=true",
        "--game-args=--custom", "--server=example.com:25565");
    Valid(parsed, "implemented per-session overrides");
    Check(parsed.SkipUpdateCheck && parsed.LaunchFullscreen && parsed.JavaPathOverride == "C:\\Java\\javaw.exe"
        && parsed.ExtraJvmArgs == "-Dexample=true" && parsed.ExtraGameArgs == "--custom" && parsed.AutoJoinServer == "example.com:25565",
        "implemented flags retain values for actual runtime consumers");
    Check(CommandLineService.HelpText.Contains("会明确报错并停止启动") && !CommandLineService.HelpText.Contains("兼容解析项"),
        "help does not advertise successful no-op parsing");
    Check(Parse("--jvm-args=-Dname=\"unclosed").HasErrors && Parse("--game-args=--name \"unclosed").HasErrors,
        "malformed quoting is rejected before an action starts");
    Check(CommandLineSessionPolicy.CombineJvmArguments(false, "-Xmx99G", "-Xmx88G", null) == null,
        "ordinary mode excludes both global and per-instance historical custom JVM values");
    Check(CommandLineSessionPolicy.CombineJvmArguments(false, "-Xmx99G", "-Xmx88G", "-Dsession=yes") == "-Dsession=yes",
        "explicit session JVM request remains intentional in ordinary mode");
    Check(CommandLineSessionPolicy.CombineJvmArguments(true, "-Dinstance=yes", "-Dglobal=yes", "-Dsession=yes") == "-Dinstance=yes -Dsession=yes",
        "advanced instance selection precedes explicit session override");
}
foreach (var invalid in new[] { "0", "-1", "NaN", "Infinity", "2147483648" }) Invalid("--wait-pid", invalid);
Check(Parse("--help", "--unknown").ShowHelp && Parse("--help", "--unknown").HasErrors,
    "entry point can prioritize help while retaining useful diagnostics");

Console.WriteLine($"PASS: {checks} command-line parser/help assertions");
