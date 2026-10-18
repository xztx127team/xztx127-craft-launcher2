using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
}

// 加载隐藏光标、触摸抑制和桌面默认 ClipCursor 都不得提前进入世界。
Check(TouchInputModePolicy.Classify(false, true, false, false, false, true) == null, "loading hidden cursor is not gameplay");
Check(TouchInputModePolicy.Classify(true, true, false, false, false, true) == null, "desktop sized clip is not capture");
Check(TouchInputModePolicy.Classify(false, true, false, false, true, true, true, true) == null, "touch suppression remains unknown");
Check(TouchInputModePolicy.Classify(true, false, true, true, false, true) == false, "real capture wins over stale overlay arrow");
Check(TouchInputModePolicy.Classify(false, true, true, true, false, true, true, true) == true, "inventory/death menu wins over joined world");
Check(TouchInputModePolicy.Classify(false, true, false, false, false, true, true, false) == null, "joined log alone is not capture");
Check(TouchInputModePolicy.Classify(false, true, false, false, false, true, true, true) == false, "joined plus hidden centered cursor supports unclipped legacy backend");
Check(TouchInputModePolicy.Classify(false, true, false, false, false, true, false, false, true) == false, "observed native recenter supports no-log fullscreen backend");
Check(TouchInputModePolicy.Classify(false, true, false, false, true, true, false, true, true) == null, "suppression does not override confirmed recenter classification");

var capture = new LegacyTouchCursorEvidence();
Check(!capture.Observe(500, 300, 500, 300, false, false, 0), "static hidden centered cursor is not recenter evidence");
Check(!capture.Observe(500, 300, 500, 300, false, false, 1000), "waiting does not invent native recenter");
capture.MenuMoved(500, 300, 1000);
capture.Observe(500, 300, 500, 300, false, false, 1100);
Check(!capture.Observe(500, 300, 500, 300, false, false, 1300), "menu touch already at center cannot authorize recenter");
capture.MenuMoved(400, 200, 2000);
Check(!capture.Observe(500, 300, 500, 300, false, true, 2100), "touch suppression cannot establish recenter evidence");
Check(!capture.Observe(500, 300, 500, 300, false, false, 2200), "first genuine hidden recenter sample still waits");
Check(!capture.Observe(500, 300, 500, 300, false, false, 2250), "short recenter transient is not accepted");
Check(capture.Observe(500, 300, 500, 300, false, false, 2300), "real off-center menu input then native stable recenter is capture");
Check(capture.Observe(510, 310, 500, 300, false, false, 2400), "actual look movement between recenter frames retains capture");
capture.Observe(500, 300, 500, 300, true, false, 2500);
Check(!capture.Captured, "visible menu revokes native capture");
Check(!capture.Observe(500, 300, 500, 300, false, false, 2700), "visible static center cannot reuse old menu anchor");
capture.MenuMoved(400, 200, 3000);
capture.Observe(500, 300, 500, 300, false, false, 3100);
capture.Observe(500, 300, 500, 300, false, false, 3200);
capture.ClearCapture();
Check(!capture.Captured && !capture.Observe(500, 300, 500, 300, false, false, 3300), "new loading phase requires fresh stable samples");
capture.Reset();
Check(!capture.Observe(500, 300, 500, 300, false, false, 3500), "focus loss/disconnect discards prior recenter evidence");
capture.MenuMoved(400, 200, 4000);
Check(!capture.Observe(500, 300, 500, 300, false, false, 184001), "expired menu movement cannot authorize recenter");

var first = new LegacyTouchWorldEvidence();
var second = new LegacyTouchWorldEvidence();
first.Observe("[12:00:00] [Render thread/INFO]: Connecting to example.test, 25565");
Check(first.Phase == LegacyTouchWorldPhase.Loading, "connection request is loading");
first.Observe("[12:00:01] [Server thread/INFO]: Player logged in with entity id 1 at (0.0, 0.0, 0.0)");
Check(first.Phase == LegacyTouchWorldPhase.Loading, "server login is not client ready");
first.Observe("[12:00:02] [Server thread/INFO]: Loaded 150 advancements");
Check(first.Phase == LegacyTouchWorldPhase.Loading, "server advancements cannot authorize client");
first.Observe("[12:00:03] [Render thread/INFO]: Loaded 0 advancements");
Check(first.Phase == LegacyTouchWorldPhase.Joined, "client packet supplies world evidence");
Check(second.Phase == LegacyTouchWorldPhase.Unknown, "different game processes have independent evidence");
first.Observe("[12:00:04] [Render thread/INFO]: Disconnected from server");
Check(first.Phase == LegacyTouchWorldPhase.Outside, "disconnect revokes world evidence");
first.Observe("[12:00:04] [Render thread/INFO]: Loaded 10 advancements");
Check(first.Phase == LegacyTouchWorldPhase.Outside, "late previous world packet cannot override disconnect");
first.Observe("[12:00:05] [Client thread/INFO]: Connecting to other.test, 25565");
Check(first.Phase == LegacyTouchWorldPhase.Loading, "new connection cannot reuse previous evidence");
first.Observe("[12:00:06] [Client thread/INFO]: [CHAT] Welcome to the world");
Check(first.Phase == LegacyTouchWorldPhase.Joined, "old client received in-world chat");
first.Observe("[12:00:07] [Server thread/INFO]: Stopping server");
Check(first.Phase == LegacyTouchWorldPhase.Outside, "leaving local world revokes evidence");

var motion = new LegacyTouchMotion();
for (var sample = 0; sample < 120; sample++) motion.Add(0.25, -0.5);
Check(motion.Take(out var x, out var y) && x == 30 && y == -60, "all touch samples form one frame without lost motion");
Check(!motion.Take(out _, out _), "one frame is not injected twice");
motion.Add(0.625, -0.625);
Check(!motion.Take(out _, out _), "subpixel frame retained");
motion.Add(0.625, -0.625);
Check(motion.Take(out x, out y) && x == 1 && y == -1, "subpixels accumulate across frames");
motion.Add(double.NaN, 1);
motion.Add(0.75, -0.75);
Check(motion.Take(out x, out y) && x == 1 && y == -1, "nonfinite input cannot poison subsequent samples");
motion.Add(100, 100); motion.Reset();
Check(!motion.Take(out _, out _), "focus loss or cancel discards queued motion");
motion.Add(double.MaxValue, -double.MaxValue);
Check(motion.Take(out x, out y) && x == 32767 && y == -32767, "extreme jump cannot overflow input");
// SDL: 两指输入可留下被系统抑制的箭头，释放 ClipCursor 也不能把它当成菜单。
Check(TouchInputModePolicy.ClassifyModern(false, true, true, false, true, true) == null,
    "suppressed stale SDL arrow does not hide gameplay controls");
Check(TouchInputModePolicy.ClassifyModern(true, true, true, true, true, true) == null,
    "suppressed fullscreen cursor is ambiguous even with showing bit");
Check(TouchInputModePolicy.ClassifyModern(true, false, true, true, true, true) == false,
    "local game capture survives touch suppression");
Check(TouchInputModePolicy.ClassifyModern(false, true, true, true, false, true) == true,
    "real visible released cursor still opens menus");
Check(TouchInputModePolicy.ClassifyModern(true, true, false, false, false, false) == null,
    "initial fullscreen loading is not capture");
Check(TouchInputModePolicy.ClassifyModern(true, true, false, false, false, true) == false,
    "observed fullscreen game capture supports SDL look");

// 第一个触点前进、第二个触点转视角：任何触点仍在操作时，都不能采纳菜单样本。
var modes = new TouchModeStability();
Check(modes.Observe(false, true, false, 0, 100, 1000) == null, "menu sample starts confirmation");
Check(modes.Observe(false, true, true, 1500, 100, 1000) == null, "forward contact cancels pending menu candidate");
Check(modes.Observe(false, true, true, 5000, 100, 1000) == null, "second contact cannot force menu transition");
Check(modes.Observe(false, true, true, 6000, 100, 1000) == null, "lifting one finger while another remains keeps controls");
Check(modes.Observe(false, true, false, 7000, 100, 1000) == null, "last finger up starts a fresh confirmation");
Check(modes.Observe(false, true, false, 7999, 100, 1000) == null, "menu debounce boundary is exclusive before deadline");
Check(modes.Observe(false, true, false, 8000, 100, 1000) == true, "idle stable menu remains reachable");
Check(modes.Observe(true, false, false, 9000, 100, 1000) == null, "capture starts confirmation");
Check(modes.Observe(true, null, false, 9050, 100, 1000) == null, "unknown sample breaks continuous evidence");
Check(modes.Observe(true, false, false, 9100, 100, 1000) == null, "capture restarts after unknown sample");
Check(modes.Observe(true, false, false, 9200, 100, 1000) == false, "idle stable game restores controls");
modes.Observe(false, true, false, 10000, 100, 1000);
modes.Reset();
Check(modes.Observe(false, true, false, 12000, 100, 1000) == null, "explicit action or focus loss clears old candidate");
Check(modes.Observe(false, true, false, 11000, 100, 1000) == null, "backward clock restarts confirmation");
Check(modes.Observe(false, false, false, 12000, 100, 1000) == null, "current state clears candidate");
Check(modes.Observe(false, true, false, 14000, 100, 1000) == null, "same-state sample prevents stale confirmation");

Console.WriteLine($"PASS: {checks} touch regression checks");
