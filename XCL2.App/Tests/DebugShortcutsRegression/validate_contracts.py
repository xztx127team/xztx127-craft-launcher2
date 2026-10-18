#!/usr/bin/env python3
"""Source wiring only; does not execute C# or Windows keyboard/rendering behavior."""
from pathlib import Path
import json
import re

app = Path(__file__).resolve().parents[2]
ui = (app / "Views/MainWindow.Debug.cs").read_text(encoding="utf-8-sig")
main = (app / "Views/MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
helper = (app / "Services/LauncherDebugShortcuts.cs").read_text(encoding="utf-8-sig")
summary = (app / "Services/LauncherDebugSnapshot.cs").read_text(encoding="utf-8-sig")
cli = (app / "Services/CommandLineService.cs").read_text(encoding="utf-8-sig")
checks = {}
def check(name, value): checks[name] = bool(value)
check("debug initializes before existing key handlers", main.index("InitializeDebugControls();") < main.index("PreviewKeyDown +="))
check("legacy F3 topmost skips debug mode", bool(re.search(r"PreviewKeyDown\s*\+=\s*\([^)]*\)\s*=>\s*\{\s*if \(App\.StartupArgs\.DebugMode\) return;\s*var key[^}]*key != Key\.F3", main)))
check("no debug hook registration without explicit flag", "if (!App.StartupArgs.DebugMode) return;" in ui and ui.index("if (!App.StartupArgs.DebugMode) return;") < ui.index("PreviewKeyDown += DebugPreviewKeyDown"))
check("input, modal, hidden and inactive window are gated", all(value in ui for value in ("!IsActive", "!IsVisible", "OverlayDialogService.HasActiveOverlay", "TextBoxBase or PasswordBox or HwndHost")))
check("no system-wide hooks", not any(value in ui for value in ("RegisterHotKey", "SetWindowsHookEx", "HookGlobal", "SendInput")))
check("existing real navigation and permissions reused", "DebugNavigate(NavLogsButton)" in ui and "DebugNavigate(NavSettingsButton)" in ui and "if (!button.IsEnabled)" in ui and "button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))" in ui)
check("existing download manager reused", "DownloadQueueButton_Click(DownloadQueueButton, new RoutedEventArgs())" in ui)
check("single F3 toggles on release and suppresses repeats", "var toggle = wasHeld && !_usedChord" in helper and "if (repeat || _usedChord)" in helper)
check("shared help discoverable in CLI", "LauncherDebugShortcuts.HelpText" in cli and "LauncherDebugShortcuts.HelpText" in ui)
check("timer and in-flight samples stop and cleanup", all(value in ui for value in ("_debugTimer?.Stop()", "_debugSamplingCancellation?.Cancel()", "PreviewKeyDown -= DebugPreviewKeyDown", "_debugTimer.Tick -= DebugTimer_Tick")))
check("samples only when panel visible and window active", "!_debugPanelVisible || !IsActive || !IsVisible || _isWindowClosed" in ui)
check("fresh floating-window metrics reused", "PerformanceOverlayService.IsPolling" in ui and "age <= TimeSpan.FromSeconds(2.5)" in ui)
check("old metrics cleared after sampling stop or failure", ui.count("_debugLastMetrics = null") >= 3 and "采样于" in ui)
check("safe mode is not falsely advertised as running", "SafeMode" not in summary and "StartupArgs.SafeMode" not in ui)
record = summary[summary.index("internal sealed record LauncherDebugSnapshot("):summary.index("/// <summary>")]
check("copied runtime snapshot has no arbitrary strings", not re.search(r"\bstring\??\s+\w+", record))
check("debug copy does not collect identities, argv, paths or log text", not any(value in ui for value in ("GetCommandLineArgs", "SelectedVersionId", "CurrentAccount", "SessionLogPath", "GetBufferedText", "MinecraftAccessToken", "Environment.UserName")))
check("diagnostic panel stays below modal overlay", "AppBodyGrid.Children.IndexOf(OverlayRoot)" in ui and "AppBodyGrid.Children.Insert" in ui)
check("diagnostic panel keeps existing page mounted", "MainContent.Content =" not in ui)
failed = [name for name, value in checks.items() if not value]
report = {"kind": "static_source_contracts", "checks": checks, "passed": len(checks) - len(failed), "failed": failed, "CSharp_compilation": "NOT_RUN", "Windows_keyboard_and_rendering": "NOT_RUN"}
(Path(__file__).parent / "validation.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
print(json.dumps({"passed": report["passed"], "failed": failed}, ensure_ascii=False))
raise SystemExit(1 if failed else 0)
