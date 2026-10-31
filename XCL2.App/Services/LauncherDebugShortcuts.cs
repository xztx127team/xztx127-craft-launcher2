namespace XCL2.App.Services;

public enum LauncherDebugAction { None, TogglePanel, ShowHelp, OpenLogs, OpenDownloads, OpenSettings, CopySummary, ClosePanel,
    ShowOverview, ShowSystem, ShowGames, TogglePause, OpenPerformanceSettings }
internal enum LauncherDebugKey { Other, F3, H, L, D, P, C, Escape, O, S, G, R, M }
internal readonly record struct LauncherDebugKeyResult(LauncherDebugAction Action, bool Handled);

/// <summary>F3 先按住再组合；单键在松开时切换，组合键不会随后误切换面板。</summary>
internal sealed class LauncherDebugShortcutState
{
    private bool _held;
    private bool _usedChord;
    internal LauncherDebugKeyResult KeyDown(LauncherDebugKey key, bool enabled, bool canHandle, bool repeat, bool hasModifiers)
    {
        if (!enabled || !canHandle) { Reset(); return default; }
        if (hasModifiers) { if (_held) _usedChord = true; return default; }
        if (key == LauncherDebugKey.F3)
        {
            if (!repeat && !_held) { _held = true; _usedChord = false; }
            return new(LauncherDebugAction.None, true);
        }
        if (!_held) return default;
        var action = key switch
        {
            LauncherDebugKey.H => LauncherDebugAction.ShowHelp,
            LauncherDebugKey.L => LauncherDebugAction.OpenLogs,
            LauncherDebugKey.D => LauncherDebugAction.OpenDownloads,
            LauncherDebugKey.P => LauncherDebugAction.OpenSettings,
            LauncherDebugKey.C => LauncherDebugAction.CopySummary,
            LauncherDebugKey.Escape => LauncherDebugAction.ClosePanel,
            LauncherDebugKey.O => LauncherDebugAction.ShowOverview,
            LauncherDebugKey.S => LauncherDebugAction.ShowSystem,
            LauncherDebugKey.G => LauncherDebugAction.ShowGames,
            LauncherDebugKey.R => LauncherDebugAction.TogglePause,
            LauncherDebugKey.M => LauncherDebugAction.OpenPerformanceSettings,
            _ => LauncherDebugAction.None
        };
        // An undefined F3 chord must not accidentally activate the focused button or Delete action.
        if (action == LauncherDebugAction.None) { _usedChord = true; return new(LauncherDebugAction.None, true); }
        if (repeat || _usedChord) return new(LauncherDebugAction.None, true);
        _usedChord = true;
        return new(action, true);
    }

    internal LauncherDebugKeyResult KeyUp(LauncherDebugKey key, bool enabled, bool canHandle, bool hasModifiers)
    {
        if (key != LauncherDebugKey.F3) return default;
        var wasHeld = _held;
        var toggle = wasHeld && !_usedChord && enabled && canHandle && !hasModifiers;
        Reset();
        return new(toggle ? LauncherDebugAction.TogglePanel : LauncherDebugAction.None, wasHeld && enabled);
    }
    internal void Reset() { _held = false; _usedChord = false; }
}

public static class LauncherDebugShortcuts
{
    public const string HelpText =
        "调试快捷键（使用 --debug 启动后，在启动器主窗口生效）：\n" +
        "  F3              松开时显示 / 隐藏调试信息\n" +
        "  F3 + H          显示调试快捷键帮助\n" +
        "  F3 + O          查看运行总览（窗口、DPI、下载与进程数）\n" +
        "  F3 + S          查看系统 / 启动器 CPU、内存、GC 与渲染级别\n" +
        "  F3 + G          查看每个游戏进程的 CPU 和内存\n" +
        "  F3 + R          暂停 / 继续调试采样，暂停时保留当前读数\n" +
        "  F3 + M          打开性能悬浮窗详细设置\n" +
        "  F3 + L          打开日志页面\n" +
        "  F3 + D          打开 / 收起右上角下载管理\n" +
        "  F3 + P          打开设置页面\n" +
        "  F3 + C          复制不含账户、路径、启动参数或日志正文的运行概况\n" +
        "  F3 + Esc        关闭调试信息\n" +
        "请按住 F3 再按组合键；输入框和模态弹窗中保留原有键盘操作。\n";
}
