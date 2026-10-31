using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
}

// 加载隐藏光标、触摸抑制和桌面默认 ClipCursor 都不得提前进入世界。
Check(!TouchInputModePolicy.ShouldProbeLegacyMode(false, true), "active look contacts do not block on a redundant synchronous cursor probe");
Check(TouchInputModePolicy.ShouldProbeLegacyMode(true, true), "holding controls in a menu still permits proven menu-to-look recovery");
Check(TouchInputModePolicy.ShouldProbeLegacyMode(false, false), "idle gameplay still detects an opened menu");
Check(TouchInputModePolicy.ShouldProbeLegacyMode(true, false), "idle menus continue normal mode discovery");
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

// 纯触屏旧游戏可能一直没有 Joined 日志，GLFW 空光标仍带 SUPPRESSED。
// 必须沿真实菜单离中心定位 -> 游戏原生稳定回中的序列恢复，不能靠隐藏或加载超时。
var suppressedCapture = new LegacyTouchCursorEvidence();
var loadingWithoutJoined = new LegacyTouchWorldEvidence();
loadingWithoutJoined.Observe("Starting integrated minecraft server version 1.12.2");
foreach (var at in new[] { 0L, 1000L, 60_000L })
{
    var hiddenLoading = suppressedCapture.ObserveBackendCursor(500, 300, 500, 300,
        showing: false, suppressed: true, backendHidden: true, now: at);
    Check(!hiddenLoading.Captured && hiddenLoading.Suppressed,
        "native hidden centered loading without menu movement never clears touch suppression");
}
suppressedCapture.MenuMoved(500, 300, 61_000);
suppressedCapture.ObserveBackendCursor(500, 300, 500, 300, false, true, true, 61_100);
var centeredMenu = suppressedCapture.ObserveBackendCursor(500, 300, 500, 300, false, true, true, 61_500);
Check(!centeredMenu.Captured && centeredMenu.Suppressed, "menu touch already at center is not a native warp");
suppressedCapture.MenuMoved(400, 200, 62_000);
var firstWarp = suppressedCapture.ObserveBackendCursor(500, 300, 500, 300, false, true, true, 62_010);
Check(!firstWarp.Captured && firstWarp.Suppressed, "one backend-hidden center sample cannot authorize capture");
var shortWarp = suppressedCapture.ObserveBackendCursor(500, 300, 500, 300, false, true, true, 62_109);
Check(!shortWarp.Captured && shortWarp.Suppressed, "backend-hidden recenter still requires the full stability interval");
var stableWarp = suppressedCapture.ObserveBackendCursor(500, 300, 500, 300, false, true, true, 62_110);
Check(stableWarp.Captured && !stableWarp.Suppressed, "native hidden stable recenter clears stale Windows suppression");
Check(loadingWithoutJoined.Phase == LegacyTouchWorldPhase.Loading &&
    TouchInputModePolicy.Classify(false, true, false, false, stableWarp.Suppressed, false,
        worldJoined: false, cursorCentered: true, recenteringObserved: stableWarp.Captured) == false,
    "real native recenter recovers fullscreen look even when no Joined line ever arrives");
var lookFrame = suppressedCapture.ObserveBackendCursor(510, 310, 500, 300, false, true, true, 62_200);
Check(lookFrame.Captured && !lookFrame.Suppressed, "relative look leaving center retains established native capture");
var suppressedArrow = suppressedCapture.ObserveBackendCursor(500, 300, 500, 300, false, true, false, 62_300);
Check(suppressedArrow.Suppressed &&
    TouchInputModePolicy.Classify(false, true, true, false, suppressedArrow.Suppressed, false,
        recenteringObserved: suppressedArrow.Captured) == null,
    "ordinary arrow hidden by touch cannot bypass suppression using an older capture");
suppressedCapture.Reset();
suppressedCapture.MenuMoved(400, 200, 63_000);
suppressedCapture.ObserveBackendCursor(500, 300, 500, 300, false, true, false, 63_010);
var arrowWait = suppressedCapture.ObserveBackendCursor(500, 300, 500, 300, false, true, false, 64_000);
Check(!arrowWait.Captured && arrowWait.Suppressed, "suppressed ordinary arrow never establishes native recenter");
suppressedCapture.ObserveBackendCursor(500, 300, 500, 300, false, true, true, 64_100);
suppressedCapture.ObserveBackendCursor(520, 300, 500, 300, false, true, true, 64_150);
var restartedWarp = suppressedCapture.ObserveBackendCursor(500, 300, 500, 300, false, true, true, 64_250);
Check(!restartedWarp.Captured && restartedWarp.Suppressed, "leaving center restarts backend-hidden recenter confirmation");
suppressedCapture.Reset();
var invalidatedWarp = suppressedCapture.ObserveBackendCursor(500, 300, 500, 300, false, true, true, 65_000);
Check(!invalidatedWarp.Captured && invalidatedWarp.Suppressed, "focus or session reset discards backend-hidden recenter authority");

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

// 在世界真正抓取鼠标后，持续按住W不能阻断menu->look；菜单/未知样本依然不能中断手势。
var heldControls = new TouchModeStability();
var allowCapturedLook = TouchInputModePolicy.AllowLegacyLookDuringContact(true, false, nativeLookConfirmed: true);
Check(allowCapturedLook, "native gameplay capture can preserve held movement controls");
Check(heldControls.Observe(true, false, true, 10_000, 100, 150, allowCapturedLook) == null,
    "held movement still requires a stable native look sample");
Check(heldControls.Observe(true, false, true, 10_100, 100, 150, allowCapturedLook) == false,
    "native look confirms while left movement finger remains held");
Check(!TouchInputModePolicy.AllowLegacyLookDuringContact(true, false, nativeLookConfirmed: false),
    "Joined or hidden-cursor fallback cannot interrupt an active menu drag");
Check(!TouchInputModePolicy.AllowLegacyLookDuringContact(false, true, nativeLookConfirmed: true),
    "a menu observation never changes an active look gesture");
Check(!TouchInputModePolicy.AllowLegacyLookDuringContact(true, null, nativeLookConfirmed: true),
    "unknown samples cannot authorize capture even during a new touch");
heldControls.Reset();
Check(heldControls.Observe(false, true, true, 11_000, 100, 150,
    TouchInputModePolicy.AllowLegacyLookDuringContact(false, true, false)) == null &&
    heldControls.Observe(false, true, true, 12_000, 100, 150,
    TouchInputModePolicy.AllowLegacyLookDuringContact(false, true, false)) == null,
    "visible/suppressed cursor fluctuations cannot end a held look or movement gesture");

Console.WriteLine($"PASS: {checks} touch regression checks");

// Reattach inside a world with no log history; static loading still cannot authorize capture.
var recovery = new TouchCaptureRecovery();
Check(!recovery.Observe(false, false, true, true, false, 0), "loading starts unknown");
Check(!recovery.Observe(false, false, true, true, false, 5000), "hidden loading alone never authorizes look");
recovery.Reset();
Check(!recovery.Observe(false, false, true, false, false, 6000), "attach waits for stable hidden centered samples");
Check(!recovery.Observe(false, false, true, false, false, 6699), "attach grace period");
Check(recovery.Observe(false, false, true, false, false, 6700), "attach recovers without mandatory client log");
Check(recovery.Observe(false, true, false, false, false, 6800), "touch suppression retains already observed capture");
Check(!recovery.Observe(true, false, false, false, false, 6900), "visible cursor revokes capture");
recovery.BeginLoading();
Check(!recovery.Observe(false, false, true, true, false, 7000), "native hide transition begins confirmation");
Check(recovery.Observe(false, false, true, true, false, 7200), "native hide transition needs no off-center menu click");
recovery.BeginLoading();
Check(!recovery.Observe(false, false, true, true, false, 9000), "another load discards prior capture");
Check(!recovery.Observe(false, false, true, false, true, 9500), "outside world resets capture");
Check(TouchInputModePolicy.ClassifyModern(true, false, true, true, false, false) == false, "modern local capture resists stale overlay arrow");
Console.WriteLine($"PASS including recovery: {checks} touch checks");

Check(TouchRoutePolicy.UseModern(false, TouchGameBackend.Sdl), "actual SDL backend overrides old metadata");
Check(!TouchRoutePolicy.UseModern(true, TouchGameBackend.Glfw), "actual GLFW backend overrides modern label");
Check(TouchRoutePolicy.UseModern(true, TouchGameBackend.Unknown), "unknown backend uses actual version metadata");
var scroll = new SettingsScrollHintPolicy();
for (var at = 0; at <= 5000; at += 250) Check(!scroll.Observe(at, true), "scroll hint waits strictly longer than five seconds");
Check(scroll.Observe(5250, true), "continuous active scroll triggers after five seconds");
Check(!scroll.Observe(10000, true), "only one hint per opening");
scroll.Reset();
Check(!scroll.Observe(0, true) && !scroll.Observe(6000, false), "programmatic movement cannot trigger hint");
Check(!scroll.Observe(6500, true), "long idle resets scroll session");
Console.WriteLine($"PASS including routing and scroll: {checks} checks");

var activeModes = new TouchModeStability();
Check(activeModes.Observe(true, false, true, 0, 100, 250, allowDuringContact: true) == null, "native capture starts confirmation even during a stuck menu drag");
Check(activeModes.Observe(true, false, true, 100, 100, 250, allowDuringContact: true) == false, "native capture recovers active gesture without lifting every finger");
Check(activeModes.Observe(false, null, true, 200, 100, 250, allowDuringContact: true) == null, "suppressed unknown sample never authorizes active switch");
Console.WriteLine($"PASS total: {checks} touch/routing/scroll checks");

// 旧版视角绝不能把“没有触摸签名”当成物理鼠标。静止按住虚拟 W 时，
// WPF 同步/系统回中仍可能不断产生 WM_MOUSEMOVE，但这些不是新的视角输入。
const ulong touchExtra = 0xFF515780;
Check(LegacyTouchMousePolicy.CanForward(0, 2, 1), "Win32 IMDT_MOUSE=2 / IMO_HARDWARE=1 is physical mouse");
Check(!LegacyTouchMousePolicy.CanForward(0, 8, 1), "Win32 IMDT_PEN=8 must not become camera movement");
Check(!LegacyTouchMousePolicy.CanForward(0, 1, 1), "Win32 IMDT_KEYBOARD=1 is not a mouse source");
Check(!LegacyTouchMousePolicy.CanForward(touchExtra, LegacyTouchMousePolicy.Mouse, LegacyTouchMousePolicy.Hardware),
    "touch signature overrides mouse-looking message source");
Check(!LegacyTouchMousePolicy.CanForward(0, LegacyTouchMousePolicy.Touch, LegacyTouchMousePolicy.Hardware),
    "touch source without MI_WP signature cannot rotate camera");
Check(!LegacyTouchMousePolicy.CanForward(0, LegacyTouchMousePolicy.Pen, LegacyTouchMousePolicy.Hardware),
    "pen source without MI_WP signature cannot rotate camera");
var falseCameraSamples = 0;
for (var frame = 0; frame < 600; frame++)
{
    if (LegacyTouchMousePolicy.CanForward(0, 0, 0)) falseCameraSamples++;
    if (LegacyTouchMousePolicy.CanForward(0, LegacyTouchMousePolicy.Mouse, 4)) falseCameraSamples++;
}
Check(falseCameraSamples == 0, "holding any virtual key cannot relay unowned synchronization or recenter echoes");
Check(!LegacyTouchMousePolicy.CanForward(0, LegacyTouchMousePolicy.Mouse, 2),
    "foreign injected movement is not physical camera input");
Check(LegacyTouchMousePolicy.CanForward(LegacyTouchMousePolicy.InjectedSignature, 0, 0),
    "signed button/menu input survives unavailable source metadata");
Check(LegacyTouchMousePolicy.CanForward(LegacyTouchMousePolicy.InjectedSignature, LegacyTouchMousePolicy.Mouse, 2),
    "our signed buttons are not blocked as foreign injections");
Check(LegacyTouchMousePolicy.CanForward(0, LegacyTouchMousePolicy.Mouse, LegacyTouchMousePolicy.Hardware),
    "genuine physical mouse still rotates camera");
Check(LegacyTouchMousePolicy.CanForward(0, LegacyTouchMousePolicy.Touchpad, LegacyTouchMousePolicy.Hardware),
    "hardware touchpad remains usable");
Check(LegacyTouchMousePolicy.ShouldSuppressPromotion(touchExtra, true, true, true, false),
    "initial virtual-key contact is blocked by client geometry even before WPF capture");
Check(LegacyTouchMousePolicy.ShouldSuppressPromotion(touchExtra, true, true, false, true),
    "captured contact leaving the overlay cannot leak absolute mouse motion");
Check(!LegacyTouchMousePolicy.ShouldSuppressPromotion(touchExtra, true, true, false, false),
    "unrelated title bar and outside-window touch remains usable");
Check(!LegacyTouchMousePolicy.ShouldSuppressPromotion(touchExtra, false, true, true, true),
    "other foreground windows are never blocked");
Check(!LegacyTouchMousePolicy.ShouldSuppressPromotion(touchExtra, true, false, true, true),
    "hidden overlay cannot capture ordinary touch input");
Check(!LegacyTouchMousePolicy.ShouldSuppressPromotion(LegacyTouchMousePolicy.InjectedSignature, true, true, true, true),
    "touch protection never swallows our own relative movement or buttons");
Check(!LegacyTouchMousePolicy.ShouldSuppressPromotion(0, true, true, true, true),
    "physical mouse is not blocked by the low-level touch filter");
Check(!TouchRoutePolicy.UseModern(false, TouchGameBackend.Lwjgl2), "LWJGL2 remains on the isolated legacy path");
Check(TouchRoutePolicy.UseModern(true, TouchGameBackend.Sdl), "SDL3 remains on its unchanged original input path");
Console.WriteLine($"PASS including legacy mouse isolation: {checks} checks");

// 同一 SendInput 在低级钩子仍带签名，而覆盖层消息来源缺失：只恢复确切那条消息。
var receipts = new LegacyMouseMessageReceipts();
receipts.Record(0x0200, 515, 305, 100, 1000);
Check(!receipts.TryConsume(0x0200, 515, 305, 99, 1001), "same point with a different native timestamp cannot borrow a look receipt");
Check(!receipts.TryConsume(0x0200, 500, 300, 100, 1001), "native recenter coordinates cannot borrow a look receipt");
Check(!receipts.TryConsume(0x0201, 515, 305, 100, 1001), "touch click cannot consume an injected look receipt");
var knownMove = receipts.TryConsume(0x0200, 515, 305, 100, 1001);
Check(knownMove && LegacyTouchMousePolicy.Classify(0, 0, 0, knownMove) == LegacyMouseMessageKind.Injected,
    "look survives lost WPF native signature and unavailable message source");
Check(LegacyTouchMousePolicy.Classify(0, 4, 1, knownMove) == LegacyMouseMessageKind.Injected,
    "authenticated input is classified before stale touch-source rejection");
Check(LegacyTouchMousePolicy.Classify(touchExtra, 4, 1, knownMove) == LegacyMouseMessageKind.Rejected,
    "explicit Windows touch signature cannot borrow a coincident injection receipt");
Check(!receipts.TryConsume(0x0200, 515, 305, 100, 1002), "the same native event cannot be replayed by an unsigned system echo");
Check(LegacyTouchMousePolicy.Classify(0, 0, 0) == LegacyMouseMessageKind.Rejected,
    "a recent injection does not authorize arbitrary unknown messages");

receipts.Record(0x0200, 510, 300, 101, 1100);
receipts.Record(0x0201, 510, 300, 102, 1101);
receipts.Record(0x0200, 520, 300, 103, 1102);
Check(receipts.TryConsume(0x0200, 520, 300, 103, 1103), "coalesced mouse move uses its latest exact timestamp and point");
Check(!receipts.TryConsume(0x0200, 510, 300, 101, 1104), "superseded movement is discarded instead of replaying after recenter");
Check(receipts.TryConsume(0x0201, 510, 300, 102, 1104), "move coalescing does not discard a mouse-button receipt");

receipts.Record(0x0200, -250, 900, uint.MaxValue, 1200);
Check(receipts.TryConsume(0x0200, -250, 900, uint.MaxValue, 1201), "negative monitor coordinates and native tick wrap are matched exactly");
receipts.Record(0x0200, -249, 900, 0, 1202);
Check(receipts.TryConsume(0x0200, -249, 900, 0, 1203), "native timestamp zero after rollover is valid");
receipts.Record(0x0200, 505, 300, 201, 2000);
Check(!receipts.TryConsume(0x0200, 505, 300, 201, 2501), "delayed movement is dropped after the bounded receipt lifetime");
receipts.Record(0x0200, 505, 300, 202, 3000);
receipts.Clear();
Check(!receipts.TryConsume(0x0200, 505, 300, 202, 3001), "cancel focus loss and mode switch revoke pending receipts");
receipts.Record(0x0200, 505, 300, 203, 3100);
var otherSession = new LegacyMouseMessageReceipts();
Check(!otherSession.TryConsume(0x0200, 505, 300, 203, 3101), "other game or replacement overlay cannot consume the old session receipt");
receipts.Clear();
for (var n = 0; n <= LegacyMouseMessageReceipts.Capacity; n++)
    receipts.Record(0x0200, 600 + n, 300, (uint)(400 + n), 4000);
Check(!receipts.TryConsume(0x0200, 600, 300, 400, 4001), "receipt buffer remains bounded when overlay messages are not delivered");
Check(receipts.TryConsume(0x0200, 600 + LegacyMouseMessageReceipts.Capacity, 300,
    (uint)(400 + LegacyMouseMessageReceipts.Capacity), 4001), "bounded buffer retains the most recent native input");
Console.WriteLine($"PASS including legacy input receipts: {checks} checks");

LegacyInputDeliveryCases.Run(Check);
LegacySuppressedCaptureCases.Run(Check);
Check(TouchRoutePolicy.NeedsRuntimeRawInputAdvice(TouchGameBackend.Glfw, false, false), "runtime GLFW attach explains live Raw Input requirement");
Check(!TouchRoutePolicy.NeedsRuntimeRawInputAdvice(TouchGameBackend.Glfw, true, false), "known legacy touch launch retains existing temporary raw-input preparation");
Check(!TouchRoutePolicy.NeedsRuntimeRawInputAdvice(TouchGameBackend.Sdl, false, true) &&
    !TouchRoutePolicy.NeedsRuntimeRawInputAdvice(TouchGameBackend.Lwjgl2, false, false), "raw-input guidance does not mislabel SDL or LWJGL2");
Console.WriteLine($"PASS including held controls and signed camera delivery: {checks} checks");
