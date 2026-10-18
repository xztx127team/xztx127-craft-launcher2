from pathlib import Path
root = Path(__file__).resolve().parents[2]
ui = (root / "Services/AprilFoolsUi.cs").read_text(encoding="utf-8-sig")
svc = (root / "Services/AprilFoolsService.cs").read_text(encoding="utf-8-sig")
cfg = (root / "Models/AppConfig.AprilFools.cs").read_text(encoding="utf-8-sig")
settings = (root / "Views/SettingsPage.AprilFools.cs").read_text(encoding="utf-8-sig")
challenge = (root / "Views/AprilFoolsExitChallengeDialog.cs").read_text(encoding="utf-8-sig")
whiteflag = ui[ui.index("public static void OnWhiteFlagClicked("):ui.index("public static bool NotifyLaunchRequested(")]
checks = {
 "current monitor physical work area": "Screen.FromHandle(hwnd).WorkingArea" in ui,
 "moves without activation": "0x0014" in ui,
 "actual button rectangles": "button.TransformToAncestor(tileGrid).TransformBounds" in ui,
 "both launch tiles evade": "new[] { launchTile, quickStartTile }" in ui,
 "edge distance policy": "AprilFoolsMotionPolicy.Distance" in ui,
 "frame rate avoidance": "FromMilliseconds(16)" in ui,
 "no mouse preview swallow": "PreviewMouseLeftButtonDown" not in ui,
 "exact win phrase": "哇哇哇，你竟然点到了！今天不整蛊你了。" in ui,
 "150ms motion default": "= 150;" in cfg,
 "legacy registry disable": 'GetInt("noyrj", 0)' in svc,
 "downloads only nonmotion": "(effect & MotionEffects) == Effect.None" in svc,
 "geometry is restored": "SetWindowPlacement" in ui,
 "prior close cancellation honored": "if (e.Cancel) { UpdateMotion(); return; }" in ui,
 "hidden restore does not reshow": "if (!owner.IsVisible) placement.ShowCmd = 0" in ui,
 "no native recovery popup": "System.Windows.MessageBox" not in ui,
 "whiteflag exits without challenge": "AprilFoolsService.ClickWhiteFlag();" in whiteflag and "AprilFoolsExitChallengeDialog" not in whiteflag,
 "settings close opens challenge": "toggle.Unchecked +=" in settings and "RequestAprilFoolsClose(promptForCode: false);" in settings and "new AprilFoolsExitChallengeDialog(_owner, easterEggCode)" in settings,
 "code button opens current challenge": 'Name = "AprilFoolsCodeButton", Content = "输入彩蛋码"' in settings and "RequestAprilFoolsClose(promptForCode: true)" in settings,
 "code unlock belongs to dialog": "private readonly AprilFoolsExitChallengeState _state = new();" in challenge and "_state.TryUnlockShortcut(easterEggCode);" in challenge,
 "confirmation requires code or completion": "if (!_closed && _state.CanClose) CloseWith(true);" in challenge,
 "F1 exit remains direct": "if (e.Key != Key.F1" in ui,
 "modal pauses motion": "!OverlayDialogService.HasActiveOverlay" in ui,
 "geometry persistence suppressed": "IsWindowGeometryTemporary(Window window)" in ui,
 "intrusive effect default opt-in": "public bool AprilFoolsExternalVideos { get; set; }" in cfg,
}
for label, passed in checks.items():
    if not passed: raise AssertionError(label)
print(f"AprilFools source contracts: {len(checks)} passed; no C# execution or Windows verification.")
