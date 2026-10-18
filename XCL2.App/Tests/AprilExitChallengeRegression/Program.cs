using XCL2.App.Services;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
Check(AprilFoolsExitChallengeState.ReadingCount > 6, "More than six confirmation stages");
Check(AprilFoolsExitChallengeState.MiniGameCount == 8, "Exactly eight mini games");
Check(AprilFoolsExitChallengeState.StageCount == 16, "Seven readings, eight games and one local human stage");
foreach (var code in new[] { "跳过", "  跳过\t", "xztx127", " XZTX127 ", "xZtX127" })
{
    Check(AprilFoolsExitChallengeState.IsShortcutCode(code), "Valid shortcut accepts surrounding whitespace and Latin letter case");
    var shortcut = new AprilFoolsExitChallengeState();
    Check(shortcut.TryUnlockShortcut(code) && shortcut.ShortcutUnlocked && shortcut.CanClose, "Valid code unlocks this challenge");
    Check(!shortcut.Completed && shortcut.Stage == 0 && shortcut.Failures == 0, "Shortcut does not complete or advance the challenge");
    Check(!shortcut.TryUnlockShortcut("invalid") && shortcut.CanClose && shortcut.ShortcutUnlocked, "Invalid retry does not clear an existing unlock");
}
var locked = new AprilFoolsExitChallengeState();
Check(!locked.ShortcutUnlocked && !locked.CanClose, "Shortcut is not shared with a newly opened challenge");
locked.Fail();
foreach (var code in new string?[] { null, "", " ", "\t\r\n", "跳 过", "跳过一下", "xztx127x", "xztx 127", "invalid" })
{
    Check(!AprilFoolsExitChallengeState.IsShortcutCode(code), "Invalid shortcut is rejected");
    Check(!locked.TryUnlockShortcut(code) && !locked.ShortcutUnlocked && !locked.CanClose, "Invalid code cannot unlock closing");
    Check(locked.Stage == 0 && locked.Failures == 1, "Invalid code does not change stage or failures");
}
locked.Pass();
Check(locked.TryUnlockShortcut("跳过") && locked.Stage == 1 && locked.Failures == 1, "Unlock preserves existing challenge progress and failures");
Check(!new AprilFoolsExitChallengeState().CanClose, "Reopening after an unlock requires a new code or full completion");
var state = new AprilFoolsExitChallengeState();
Check(!state.CanClose && !state.ShortcutUnlocked, "Normal challenge starts locked");
Check(!state.ShouldOfferConfigExit(TimeSpan.FromSeconds(19.999)), "No premature timeout");
Check(state.ShouldOfferConfigExit(TimeSpan.FromSeconds(20)), "Twenty seconds is sufficient");
Check(!state.AnswerReading(-1) && state.Stage == 0 && state.Failures == 1, "Wrong answer does not advance or reset stage");
Check(!state.ShouldOfferConfigExit(TimeSpan.Zero), "One failure is not threshold");
Check(!state.AnswerReading(-1) && state.Failures == 2 && state.ShouldOfferConfigExit(TimeSpan.Zero), "Two failures offer config escape immediately");
foreach (var question in AprilFoolsExitChallengeState.Questions)
{
    Check(question.Options.Length >= 3 && question.Answer >= 0 && question.Answer < question.Options.Length, "Question has a valid answer");
    Check(state.AnswerReading(question.Answer), "Correct reading advances");
}
Check(state.Stage == 7 && !state.IsReading && !state.IsHumanCheck, "Reading transitions to games");
var failures = state.Failures;
Check(!state.AnswerReading(0) && state.Failures == failures, "Reading answers cannot advance a game");
for (var i = 0; i < 8; i++) state.Pass();
Check(state.IsHumanCheck && !state.Completed && state.Stage == 15, "Games do not skip local human stage");
state.Fail(); Check(state.IsHumanCheck && state.Failures == 3, "Human failure preserves progress");
state.Pass(); Check(state.Completed && state.CanClose && !state.ShortcutUnlocked, "All stages complete and allow closing without a shortcut");
state.Pass(); state.Fail(); Check(state.Stage == 16 && state.Failures == 3, "Completed flow is stable");
Console.WriteLine("April exit challenge state checks passed.");
