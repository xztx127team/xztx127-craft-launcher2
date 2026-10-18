using XCL2.App.Services;

var passed = 0;
void Check(bool success, string name) { if (!success) throw new InvalidOperationException(name); passed++; }
var state = new LauncherDebugShortcutState();
LauncherDebugKeyResult Down(LauncherDebugKey key, bool enabled = true, bool canHandle = true, bool repeat = false, bool modifiers = false)
    => state.KeyDown(key, enabled, canHandle, repeat, modifiers);
LauncherDebugKeyResult Up(LauncherDebugKey key = LauncherDebugKey.F3, bool enabled = true, bool canHandle = true, bool modifiers = false)
    => state.KeyUp(key, enabled, canHandle, modifiers);
Check(!Down(LauncherDebugKey.F3, enabled: false).Handled && Up(enabled: false).Action == LauncherDebugAction.None, "all F3 behavior disabled without --debug");
Check(Down(LauncherDebugKey.F3).Action == LauncherDebugAction.None, "F3 does not toggle on press before chord is known");
Check(Up().Action == LauncherDebugAction.TogglePanel, "plain F3 toggles on release");
Down(LauncherDebugKey.F3); Check(Down(LauncherDebugKey.F3, repeat: true).Action == LauncherDebugAction.None, "held F3 repeat does not toggle");
Check(Up().Action == LauncherDebugAction.TogglePanel && Up().Action == LauncherDebugAction.None, "releasing F3 toggles exactly once");
foreach (var (key, action) in new[]
{
    (LauncherDebugKey.H, LauncherDebugAction.ShowHelp), (LauncherDebugKey.L, LauncherDebugAction.OpenLogs),
    (LauncherDebugKey.D, LauncherDebugAction.OpenDownloads), (LauncherDebugKey.P, LauncherDebugAction.OpenSettings),
    (LauncherDebugKey.C, LauncherDebugAction.CopySummary), (LauncherDebugKey.Escape, LauncherDebugAction.ClosePanel),
    (LauncherDebugKey.O, LauncherDebugAction.ShowOverview), (LauncherDebugKey.S, LauncherDebugAction.ShowSystem),
    (LauncherDebugKey.G, LauncherDebugAction.ShowGames), (LauncherDebugKey.R, LauncherDebugAction.TogglePause),
    (LauncherDebugKey.M, LauncherDebugAction.OpenPerformanceSettings)
})
{
    Check(!Down(key).Handled, "unmodified key stays with application: " + key);
    Down(LauncherDebugKey.F3);
    Check(Down(key) == new LauncherDebugKeyResult(action, true), "correct F3 chord: " + key);
    Check(Down(key, repeat: true).Action == LauncherDebugAction.None, "chord repeat suppressed: " + key);
    Check(Down(LauncherDebugKey.H).Action == LauncherDebugAction.None, "one action per F3 hold: " + key);
    Check(Up().Action == LauncherDebugAction.None, "chord release does not also toggle: " + key);
}
Down(LauncherDebugKey.F3); Check(Down(LauncherDebugKey.Other).Handled, "unknown F3 chord cannot activate focused buttons or Delete");
Check(Up().Action == LauncherDebugAction.None, "unknown chord does not unexpectedly toggle on release");
Check(Down(LauncherDebugKey.F3, repeat: true).Handled && Up().Action == LauncherDebugAction.None, "repeat received after activation does not arm a fresh toggle");
Check(!Down(LauncherDebugKey.F3, modifiers: true).Handled && Up().Action == LauncherDebugAction.None, "modified F3 is not captured");
Down(LauncherDebugKey.F3); Down(LauncherDebugKey.Other, modifiers: true);
Check(Up().Action == LauncherDebugAction.None, "modifier during F3 hold cancels plain toggle");
Check(!Down(LauncherDebugKey.F3, canHandle: false).Handled && Up().Action == LauncherDebugAction.None, "input and modal contexts do not arm F3");
Down(LauncherDebugKey.F3); Check(!Down(LauncherDebugKey.L, canHandle: false).Handled, "context change cancels pending chord");
Check(Up().Action == LauncherDebugAction.None, "modal dismissal cannot produce delayed toggle");
Down(LauncherDebugKey.F3); state.Reset();
Check(Up().Action == LauncherDebugAction.None, "deactivation clears held F3");
Down(LauncherDebugKey.F3); Check(Up(LauncherDebugKey.Other).Action == LauncherDebugAction.None && Up().Action == LauncherDebugAction.TogglePanel, "other key-up is harmless");

var snapshot = new LauncherDebugSnapshot(new Version(2, 3, 0), LauncherDebugPage.Ai, LauncherDebugWindowMode.Normal,
    1000, 640, 125, true, false, false, TimeSpan.FromHours(125) + TimeSpan.FromMinutes(3),
    256L * 1048576, 64L * 1048576, 16L * 1024 * 1048576, 8L * 1024 * 1048576, 25, 5, 2, 1, 3, 8);
var text = LauncherDebugSnapshotFormatter.Format(snapshot);
Check(text.Contains("XCL2 2.3.0 · DEBUG") && text.Contains("AI 助手") && text.Contains("125%"), "human-readable launcher context");
Check(text.Contains("125:03:00"), "uptime does not wrap at 24 hours");
Check(text.Contains("25.0%") && text.Contains("5.0%") && text.Contains("16,384 MB") && text.Contains("8,192 MB"), "real CPU and total/available memory kept distinct");
Check(text.Contains("256 MB") && text.Contains("64 MB") && text.Contains("游戏进程：2") && text.Contains("1 进行中 / 3 已暂停 / 8 合计"), "process and queue counts are understandable");
var unknown = LauncherDebugSnapshotFormatter.Format(snapshot with { SystemCpuPercent = null, LauncherCpuPercent = double.NaN, TotalMemoryBytes = null });
Check(unknown.Contains("暂无数据") && !unknown.Contains("NaN") && unknown.Contains("总共 未知"), "unavailable metrics are not fabricated zeros");
var extreme = LauncherDebugSnapshotFormatter.Format(snapshot with { SystemCpuPercent = 1000, LauncherCpuPercent = -5, RunningGames = -10, Uptime = TimeSpan.FromSeconds(-1) });
Check(extreme.Contains("100.0%") && extreme.Contains("0.0%") && extreme.Contains("游戏进程：0") && extreme.Contains("00:00:00"), "invalid numeric samples are bounded");
var stringProperties = typeof(LauncherDebugSnapshot).GetProperties().Where(property => property.PropertyType == typeof(string)).ToArray();
Check(stringProperties.Length == 0, "copied runtime model accepts no account, directory, command line, instance name, token or arbitrary string fields");
foreach (var key in new[] { "F3", "F3 + H", "F3 + L", "F3 + D", "F3 + P", "F3 + C", "F3 + Esc", "F3 + O", "F3 + S", "F3 + G", "F3 + R", "F3 + M" })
    Check(LauncherDebugShortcuts.HelpText.Contains(key), "shared CLI/UI help documents " + key);
var system = LauncherDebugSnapshotFormatter.Format(snapshot with
    { Gen0Collections = 12, Gen1Collections = 3, Gen2Collections = 1, DpiPercent = 150, RenderTier = 2, ProcessThreadCount = 20 }, LauncherDebugView.System);
Check(system.Contains("Gen0 12 / Gen1 3 / Gen2 1") && system.Contains("150%") && system.Contains("Tier 2") && system.Contains("启动器线程：20"),
    "system view contains sampled GC/thread and window graphics data");
var gameSnapshot = snapshot with { Games = new[] { new LauncherDebugGameSnapshot(20, null, null), new LauncherDebugGameSnapshot(10, 12.5, 512L * 1048576) } };
var gameText = LauncherDebugSnapshotFormatter.Format(gameSnapshot, LauncherDebugView.Games);
Check(gameText.Contains("PID 10  |  CPU 12.5%  |  内存 512 MB") && gameText.Contains("PID 20  |  CPU 暂无数据  |  内存 未知"),
    "game view shows real per-process metrics without substituting zeros");
Check(gameText.IndexOf("PID 10", StringComparison.Ordinal) < gameText.IndexOf("PID 20", StringComparison.Ordinal), "game rows have stable PID order");
Check(LauncherDebugSnapshotFormatter.Format(snapshot with { Games = Array.Empty<LauncherDebugGameSnapshot>() }, LauncherDebugView.Games).Contains("暂无运行中的游戏"),
    "empty game list is distinguished from pending sample");
Check(typeof(LauncherDebugGameSnapshot).GetProperties().All(property => property.PropertyType != typeof(string)), "game debug copy has no names or paths");
Console.WriteLine($"PASS {passed} debug shortcut and summary checks");
