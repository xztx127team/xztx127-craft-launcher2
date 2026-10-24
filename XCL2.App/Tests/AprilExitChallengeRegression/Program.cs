using XCL2.App.Services;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
Check(AprilFoolsExitChallengeState.ReadingCount > 6, "More than six confirmation stages");
Check(AprilFoolsExitChallengeState.MiniGameCount == 12, "Twelve mini games include additional word and rule puzzles");
Check(AprilFoolsExitChallengeState.StageCount == 20, "Seven readings, twelve games and one multi-step local human stage");
Check(AprilFoolsExitChallengeState.Questions.Count == AprilFoolsExitChallengeState.ReadingCount, "Reading count matches the questions");
var password = AprilFoolsExitChallengeState.SessionPassword;
Check(password.Length == 16 && password.All(c => char.IsAsciiHexDigit(c)), "Session password is a sixteen-character random token");
Check(AprilFoolsExitChallengeState.IsShortcutCode(password) &&
    AprilFoolsExitChallengeState.IsShortcutCode(" " + password.ToLowerInvariant() + "\t"), "Session answer is accepted with normal input normalization");
Check(new AprilFoolsExitChallengeState().TryUnlockShortcut(password), "Session password unlocks a new challenge");
var independentlyGenerated = Enumerable.Range(0, 32).Select(_ => AprilFoolsExitChallengeState.GenerateSessionPassword()).ToHashSet();
Check(independentlyGenerated.Count == 32, "New process-password generation is not a fixed/default token");
Check(AprilFoolsExitChallengeState.SessionPassword == password, "Independent generation and reopening do not rotate this process password");
Check(AprilFoolsExitChallengeState.MatchesPuzzleAnswer(" ｍ ｋ\tＡ ", "MKA"), "Text-game entry accepts case, whitespace and fullwidth typing");
Check(!AprilFoolsExitChallengeState.MatchesPuzzleAnswer("MAK", "MKA"), "Wrong word ordering is not accepted");
Check(!AprilFoolsExitChallengeState.MatchesPuzzleAnswer("\uD800", "MKA"), "Malformed pasted Unicode is rejected without throwing");
Check(AprilFoolsExitChallengeState.ReverseLettersWithoutDigits("A3k7M2P") == "PMkA", "Text-game operation removes digits before reversing letters");
Check(AprilFoolsExitChallengeState.ReverseVerificationCode("C4B2ZA") == "AZ2B4C", "Final verification reverses digits and letters together");
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
for (var i = 0; i < AprilFoolsExitChallengeState.MiniGameCount; i++) state.Pass();
Check(state.IsHumanCheck && !state.Completed && state.Stage == 19, "Games do not omit the final local human stage");
state.Fail(); Check(state.IsHumanCheck && state.Failures == 3, "Human failure preserves progress");
state.Pass(); Check(state.Completed && state.CanClose && !state.ShortcutUnlocked, "All stages complete and allow closing without a shortcut");
state.Pass(); state.Fail(); Check(state.Stage == 20 && state.Failures == 3, "Completed flow is stable");
Console.WriteLine("April exit challenge state checks passed.");
