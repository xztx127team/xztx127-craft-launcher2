using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string scenario)
{
    checks++;
    if (!condition) throw new InvalidOperationException(scenario);
}
static LegacyTouchModeRecoveryPolicy NewJoin()
{
    var mode = new LegacyTouchModeRecoveryPolicy();
    mode.ResetContext(LegacyTouchWorldPhase.Loading);
    mode.ObservePhase(LegacyTouchWorldPhase.Joined, 1000);
    return mode;
}

var policy = new LegacyTouchModeRecoveryPolicy();
policy.ResetContext(LegacyTouchWorldPhase.Joined);
Check(!policy.ShouldRecoverLook(true, false, true, false, 1000), "Attaching to Joined must not manufacture a join edge");
Check(!policy.ObservePhase(LegacyTouchWorldPhase.Joined, 1100), "Repeated Joined has no new transition");

policy = NewJoin();
Check(!policy.ShouldRecoverLook(true, false, true, false, 1000), "One hidden sample is insufficient");
Check(!policy.ShouldRecoverLook(true, false, true, false, 1299), "Hidden samples must settle");
Check(policy.ShouldRecoverLook(true, false, true, false, 1300), "A genuine join with stable hidden cursor may recover");
var stability = new TouchModeStability();
Check(stability.Observe(true, false, false, 1300, 100, 150) == null, "Recovery still goes through mode confirmation");
Check(policy.ShouldRecoverLook(true, false, true, false, 1400), "Candidate survives until confirmed");
Check(stability.Observe(true, false, false, 1400, 100, 150) == false, "Second stable sample confirms look");
policy.CancelJoinedRecovery();
Check(!policy.ShouldRecoverLook(true, false, true, false, 1500), "Confirmed edge cannot be reused");
policy.ObservePhase(LegacyTouchWorldPhase.Joined, 1600);
Check(!policy.ShouldRecoverLook(true, false, true, false, 2000), "Remaining in Joined cannot rearm capture");

policy = NewJoin();
policy.ShouldRecoverLook(true, false, true, false, 1000);
Check(!policy.ShouldRecoverLook(true, true, true, false, 1100), "A visible menu cancels recovery");
Check(!policy.ShouldRecoverLook(true, false, true, false, 1800), "Later hiding the menu cannot reuse a canceled edge");
policy = NewJoin();
Check(!policy.ShouldRecoverLook(true, false, true, true, 1000), "ESC/E intent cancels join recovery");
Check(!policy.ShouldRecoverLook(true, false, true, false, 2000), "Menu intent cancellation persists");
policy = NewJoin();
policy.ShouldRecoverLook(true, false, true, false, 1000);
Check(!policy.ShouldRecoverLook(true, false, false, false, 1300), "Moving/resizing window resets stability");
Check(!policy.ShouldRecoverLook(true, false, true, false, 1400), "Stable window starts a fresh interval");
Check(policy.ShouldRecoverLook(true, false, true, false, 1700), "Stable window can recover after settling");
policy = NewJoin();
Check(!policy.ShouldRecoverLook(false, false, true, false, 1000), "Cursor API failure is not hidden evidence");
Check(!policy.ShouldRecoverLook(true, false, true, false, 6001), "A join signal expires");
policy = NewJoin();
Check(!policy.ShouldRecoverLook(true, false, true, false, 999), "Clock rollback cancels stale recovery");

policy.ResetContext(LegacyTouchWorldPhase.Unknown);
Check(!policy.ShouldRecoverLook(true, false, true, false, 10000), "Suppression in Unknown alone cannot select look");
policy.ObservePhase(LegacyTouchWorldPhase.Loading, 11000);
Check(!policy.ShouldRecoverLook(true, false, true, false, 12000), "Suppression in Loading alone cannot select look");
policy.Cycle();
Check(policy.Selection == LegacyTouchModeSelection.Look && policy.MenuOverride == false,
    "The first explicit mode choice escapes Unknown/Loading to look");
policy.ShouldRecoverLook(true, true, true, false, 999999);
Check(policy.MenuOverride == false, "Manual look has no three-second timeout and ignores cursor samples");
Check(!policy.ObservePhase(LegacyTouchWorldPhase.Loading, 1000000) && policy.MenuOverride == false,
    "Remaining in Loading must not repeatedly undo an explicit choice");
Check(policy.ApplyMenuAction(TouchMenuAction.Toggle, false) == true && policy.MenuOverride == true,
    "Manual ESC/E opens overlay menu mode");
Check(policy.ApplyMenuAction(TouchMenuAction.Toggle, true) == false && policy.MenuOverride == false,
    "Manual ESC/E returns to overlay look mode");
Check(policy.ApplyMenuAction(TouchMenuAction.Open, false) == true, "Chat/Open explicitly selects menu");
Check(policy.ApplyMenuAction(TouchMenuAction.Close, true) == false, "Close explicitly selects look");
policy.Cycle();
Check(policy.Selection == LegacyTouchModeSelection.Menu, "Look cycles to menu");
policy.Cycle();
Check(policy.Selection == LegacyTouchModeSelection.Automatic && !policy.MenuOverride.HasValue, "Menu cycles to automatic");
Check(policy.ApplyMenuAction(TouchMenuAction.Toggle, false) == null,
    "Automatic mode still treats keys as intent rather than permanent capture evidence");

policy.Select(LegacyTouchModeSelection.Look);
policy.ResetContext(LegacyTouchWorldPhase.Joined);
Check(policy.Selection == LegacyTouchModeSelection.Automatic, "Focus loss/window replacement clears manual state");
Check(!policy.ShouldRecoverLook(true, false, true, false, 2000000), "Restoring focus cannot replay the old join edge");
policy.Select(LegacyTouchModeSelection.Look);
Check(policy.ObservePhase(LegacyTouchWorldPhase.Outside, 2000001) && !policy.MenuOverride.HasValue,
    "Leaving a world resets to automatic menu");
policy.Select(LegacyTouchModeSelection.Look);
Check(policy.ObservePhase(LegacyTouchWorldPhase.Loading, 2000002) && !policy.MenuOverride.HasValue,
    "Starting another world resets the previous manual selection");

// Production composition: joining while a movement button remains held used to block recovery
// until its five-second lifetime expired. A genuine stable edge may pass the contact gate.
policy = NewJoin();
stability = new TouchModeStability();
bool ObserveHeldJoin(long now, bool activeMenuGesture = false)
{
    var recovered = policy.ShouldRecoverLook(true, false, true, false, now);
    return TouchInputModePolicy.AllowLegacyLookDuringContact(true, recovered ? false : null,
        nativeLookConfirmed: false, joinedRecoveryConfirmed: recovered, activeMenuGesture: activeMenuGesture);
}
Check(!ObserveHeldJoin(1000), "A held movement key cannot turn the first hidden sample into look");
Check(!ObserveHeldJoin(1299), "Joined recovery with held controls still requires the hidden stability interval");
var allowHeldJoin = ObserveHeldJoin(1300);
Check(allowHeldJoin, "A stable genuine join can recover while the movement key is still held");
Check(stability.Observe(true, false, true, 1300, 100, 150, allowHeldJoin) == null,
    "Held-control recovery still requires consecutive mode samples");
Check(stability.Observe(true, false, true, 1400, 100, 150, ObserveHeldJoin(1400)) == false,
    "Held controls no longer starve the joined-to-look transition");
policy.CancelJoinedRecovery();
Check(!ObserveHeldJoin(1500), "An adopted held-control recovery consumes the join edge");
policy.ObservePhase(LegacyTouchWorldPhase.Joined, 1600);
Check(!ObserveHeldJoin(1700), "Permanent Joined status cannot manufacture a second held-control recovery");

policy = NewJoin();
stability = new TouchModeStability();
ObserveHeldJoin(1000);
Check(!ObserveHeldJoin(1300, activeMenuGesture: true), "A stable join cannot steal an existing menu drag");
Check(stability.Observe(true, false, true, 1300, 100, 150,
    ObserveHeldJoin(1300, activeMenuGesture: true)) == null &&
    stability.Observe(true, false, true, 1500, 100, 150,
    ObserveHeldJoin(1500, activeMenuGesture: true)) == null,
    "Repeated joined candidates never interrupt an active menu drag");
Check(TouchInputModePolicy.AllowLegacyLookDuringContact(true, false, true, false, true),
    "Confirmed native capture can end a menu gesture while preserving held controls and rebasing the view");
Check(ObserveHeldJoin(1600), "After the menu finger lifts, an unexpired edge may still preserve the movement key");

policy = NewJoin();
ObserveHeldJoin(1000);
Check(ObserveHeldJoin(1300), "Stable edge is available immediately before a new view finger lands");
// AdoptLookBeforeGesture consumes the established edge before creating a menu pointer/press.
policy.CancelJoinedRecovery();
Check(!ObserveHeldJoin(1301), "A new view gesture cannot replay its adopted recovery");

policy.ResetContext(LegacyTouchWorldPhase.Unknown);
Check(!ObserveHeldJoin(2000) && !ObserveHeldJoin(3000),
    "Ordinary touch suppression without a join edge cannot bypass held controls");
policy.ResetContext(LegacyTouchWorldPhase.Joined);
Check(!ObserveHeldJoin(4000) && !ObserveHeldJoin(5000),
    "Attaching to an already joined game cannot bypass held controls");
policy = NewJoin();
ObserveHeldJoin(1000);
Check(!policy.ShouldRecoverLook(true, false, true, true, 1300) && !ObserveHeldJoin(1400),
    "Explicit menu intent cancels a pending held-control recovery");
policy = NewJoin();
ObserveHeldJoin(1000);
Check(!policy.ShouldRecoverLook(true, true, true, false, 1300) && !ObserveHeldJoin(1400),
    "Visible menu evidence cancels recovery before it can bypass held controls");
policy = NewJoin();
ObserveHeldJoin(1000);
Check(!ObserveHeldJoin(6001), "Held controls cannot revive an expired join edge");
Check(!TouchInputModePolicy.AllowLegacyLookDuringContact(true, null, false, true) &&
    !TouchInputModePolicy.AllowLegacyLookDuringContact(false, true, false, true),
    "Recovery cannot admit unknown samples or reverse a look-to-menu transition");

Console.WriteLine($"PASS: {checks} legacy mode recovery checks.");
