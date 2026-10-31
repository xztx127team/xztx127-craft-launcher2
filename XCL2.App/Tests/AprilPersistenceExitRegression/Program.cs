using XCL2.App.Services;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var root = Path.Combine(Path.GetTempPath(), "XCL2-state-regression-" + Guid.NewGuid().ToString("N"));
try
{
    var primary = Path.Combine(root, "user"); var legacy = Path.Combine(root, "legacy");
    Directory.CreateDirectory(legacy);
    File.WriteAllText(Path.Combine(legacy, "state.json"), "{\"FlagClicked\":true}");
    var store = new AprilFoolsStateStore(primary, legacy);
    Check(store.TryLoad<State>(out var state, out var warning) && state?.FlagClicked == true && warning is null,
        "Legacy dismissal is read and migrated without replaying the prank");
    Check(File.Exists(store.StatePath), "Migration writes only to user directory");
    Check(store.TrySave(new State { FlagClicked = false }, out warning) && warning is null, "State writes atomically");
    Check(store.TryLoad<State>(out state, out warning) && state?.FlagClicked == false, "User state wins over legacy state");
    File.WriteAllText(store.StatePath, "broken JSON");
    Check(store.TryLoad<State>(out state, out warning) && state?.FlagClicked == true, "Corrupt primary can recover valid legacy state");
    var blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "not a directory");
    var denied = new AprilFoolsStateStore(blocked, Path.Combine(root, "absent"));
    Check(!denied.TrySave(new State(), out warning) && !string.IsNullOrWhiteSpace(warning), "Write failure returns visible warning data");
    Check(!Directory.EnumerateFiles(primary, "*.tmp").Any(), "No temporary files remain after atomic writes");
}
finally { try { Directory.Delete(root, recursive: true); } catch { } }
Check(!LauncherExitPolicy.ShouldCloseGames(LauncherExitMode.Normal, false), "Ordinary launcher close never implicitly kills games");
Check(LauncherExitPolicy.ShouldCloseGames(LauncherExitMode.Normal, false, true), "Explicit close choice authorizes cleanup");
Check(!LauncherExitPolicy.ShouldCloseGames(LauncherExitMode.Normal, true), "Explicit keep-game choice wins");
Check(!LauncherExitPolicy.ShouldCloseGames(LauncherExitMode.PreserveGamesAfterLaunch, false), "Post-launch close preserves games without requiring touch mode");
Check(!LauncherExitPolicy.ShouldCloseGames(LauncherExitMode.PreserveGamesForUpdate, false), "Updater exit preserves games and servers");
Console.WriteLine("April persistence and launcher exit policy checks passed.");
public sealed class State { public bool FlagClicked { get; set; } }
