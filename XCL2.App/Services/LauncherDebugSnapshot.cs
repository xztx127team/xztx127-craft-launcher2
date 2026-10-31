using System.Globalization;
using System.Text;

namespace XCL2.App.Services;

internal enum LauncherDebugPage { Loading, Home, Versions, Downloads, Multiplayer, Worlds, Mods, Servers, Toolbox, Bedrock, About, Accounts, Settings, More, Logs, Ai, Api, Plugin, Other }
internal enum LauncherDebugWindowMode { Normal, Maximized, Minimized, Fullscreen }
internal enum LauncherDebugView { Overview, System, Games }
internal sealed record LauncherDebugGameSnapshot(int ProcessId, double? CpuPercent, long? WorkingSetBytes);
internal sealed record LauncherDebugSnapshot(
    Version? Version, LauncherDebugPage Page, LauncherDebugWindowMode WindowMode,
    int Width, int Height, int ZoomPercent, bool Preview, bool Topmost, bool Restricted,
    TimeSpan Uptime, long? ProcessMemoryBytes, long ManagedMemoryBytes,
    long? TotalMemoryBytes, long? AvailableMemoryBytes, double? SystemCpuPercent, double? LauncherCpuPercent,
    int RunningGames, int RunningDownloads, int PausedDownloads, int TotalDownloads,
    IReadOnlyList<LauncherDebugGameSnapshot>? Games = null,
    int Gen0Collections = 0, int Gen1Collections = 0, int Gen2Collections = 0,
    int RenderTier = 0, int DpiPercent = 100, int? ProcessThreadCount = null);

/// <summary>只接收数值、版本号和已知枚举，不接收账号、实例名称、路径或命令行文本。</summary>
internal static class LauncherDebugSnapshotFormatter
{
    internal static string Format(LauncherDebugSnapshot state, LauncherDebugView view = LauncherDebugView.Overview)
    {
        var text = new StringBuilder();
        text.AppendLine($"XCL2 {state.Version?.ToString() ?? "未知"} · DEBUG");
        text.AppendLine($"运行 {Duration(state.Uptime)}  |  .NET {Environment.Version}  |  {IntPtr.Size * 8} 位进程");
        if (view == LauncherDebugView.Games)
        {
            text.AppendLine($"游戏进程：{Math.Max(0, state.RunningGames)}  ·  CPU 按整机总算力计算");
            if (state.Games == null) text.AppendLine("正在读取游戏数据…");
            else if (state.Games.Count == 0) text.AppendLine("暂无运行中的游戏。");
            else foreach (var game in state.Games.OrderBy(game => game.ProcessId))
                text.AppendLine($"PID {game.ProcessId}  |  CPU {Percent(game.CpuPercent)}  |  内存 {Bytes(game.WorkingSetBytes)}");
            text.AppendLine("内存为进程物理工作集；实例名称与显示范围可在性能悬浮窗中查看和设置。");
        }
        else if (view == LauncherDebugView.System)
        {
            AppendSystem(text, state);
            text.AppendLine($"操作系统版本：{Environment.OSVersion.Version}  |  {(Environment.Is64BitOperatingSystem ? 64 : 32)} 位系统");
            text.AppendLine($"启动器线程：{(state.ProcessThreadCount is >= 0 ? state.ProcessThreadCount.Value.ToString(CultureInfo.InvariantCulture) : "未知")}");
            text.AppendLine($"GC 累计次数：Gen0 {Math.Max(0, state.Gen0Collections)} / Gen1 {Math.Max(0, state.Gen1Collections)} / Gen2 {Math.Max(0, state.Gen2Collections)}");
            text.AppendLine($"WPF 渲染级别：Tier {Math.Clamp(state.RenderTier, 0, 2)}  |  窗口 DPI：{Math.Clamp(state.DpiPercent, 1, 1000)}%");
            text.AppendLine("渲染级别是启动器的图形能力指标，不代表游戏 FPS。");
        }
        else
        {
            text.AppendLine($"页面：{PageName(state.Page)}  |  界面：{(state.Preview ? "新界面预览" : "标准")}");
            text.AppendLine($"窗口：{Math.Max(0, state.Width)} × {Math.Max(0, state.Height)}  ·  {WindowName(state.WindowMode)}  ·  缩放 {Math.Clamp(state.ZoomPercent, 1, 1000)}%");
            text.AppendLine($"窗口 DPI：{Math.Clamp(state.DpiPercent, 1, 1000)}%  |  WPF 渲染级别：Tier {Math.Clamp(state.RenderTier, 0, 2)}");
            text.AppendLine($"置顶：{OnOff(state.Topmost)}  |  基本模式：{OnOff(state.Restricted)}");
            text.AppendLine();
            AppendSystem(text, state);
            text.AppendLine($"游戏进程：{Math.Max(0, state.RunningGames)}  |  下载：{Math.Max(0, state.RunningDownloads)} 进行中 / {Math.Max(0, state.PausedDownloads)} 已暂停 / {Math.Max(0, state.TotalDownloads)} 合计");
        }
        text.AppendLine();
        text.Append("F3 收起  ·  F3 + H 帮助  ·  F3 + R 暂停/继续  ·  F3 + C 复制");
        return text.ToString();
    }
    private static void AppendSystem(StringBuilder text, LauncherDebugSnapshot state)
    {
        text.AppendLine($"CPU 逻辑处理器：{Environment.ProcessorCount}  |  系统 CPU：{Percent(state.SystemCpuPercent)}  |  启动器 CPU：{Percent(state.LauncherCpuPercent)}");
        text.AppendLine($"系统内存：总共 {Bytes(state.TotalMemoryBytes)}  /  可用 {Bytes(state.AvailableMemoryBytes)}");
        var used = state.TotalMemoryBytes is >= 0 && state.AvailableMemoryBytes is >= 0 && state.AvailableMemoryBytes <= state.TotalMemoryBytes
            ? state.TotalMemoryBytes - state.AvailableMemoryBytes : null;
        text.AppendLine($"系统已用：{Bytes(used)}  |  启动器内存：{Bytes(state.ProcessMemoryBytes)}  |  托管堆：{Bytes(state.ManagedMemoryBytes)}");
    }
    private static string Percent(double? value) => value is double number && double.IsFinite(number)
        ? Math.Clamp(number, 0, 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "暂无数据";
    private static string Bytes(long? value) => value is >= 0 ? (value.Value / 1048576d).ToString("N0", CultureInfo.InvariantCulture) + " MB" : "未知";
    private static string Duration(TimeSpan value) => value < TimeSpan.Zero ? "00:00:00" : $"{(long)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    private static string OnOff(bool value) => value ? "开" : "关";
    private static string WindowName(LauncherDebugWindowMode value) => value switch { LauncherDebugWindowMode.Maximized => "最大化", LauncherDebugWindowMode.Minimized => "最小化", LauncherDebugWindowMode.Fullscreen => "全屏", _ => "窗口" };
    private static string PageName(LauncherDebugPage value) => value switch
    {
        LauncherDebugPage.Loading => "加载中", LauncherDebugPage.Home => "首页", LauncherDebugPage.Versions => "版本管理",
        LauncherDebugPage.Downloads => "下载中心", LauncherDebugPage.Multiplayer => "联机", LauncherDebugPage.Worlds => "存档",
        LauncherDebugPage.Mods => "Mod 管理", LauncherDebugPage.Servers => "服务端管理", LauncherDebugPage.Toolbox => "百宝箱",
        LauncherDebugPage.Bedrock => "基岩版", LauncherDebugPage.About => "帮助", LauncherDebugPage.Accounts => "账户",
        LauncherDebugPage.Settings => "设置", LauncherDebugPage.More => "更多", LauncherDebugPage.Logs => "日志",
        LauncherDebugPage.Ai => "AI 助手", LauncherDebugPage.Api => "API 开发中心", LauncherDebugPage.Plugin => "插件页面", _ => "其他页面"
    };
}
