using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
void Reject<T>(Action action, string name) where T : Exception
{ try { action(); } catch (T) { checks++; return; } throw new Exception(name); }
var root = Path.Combine(Path.GetTempPath(), "XCL2-MiniWorld-launch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    var ordinary = Path.Combine(root, "ordinary"); Directory.CreateDirectory(ordinary);
    var newer = Path.Combine(ordinary, "MicroMiniNew.exe"); File.WriteAllText(newer, "test fixture - never run");
    File.WriteAllText(Path.Combine(ordinary, "MicroMini.exe"), "test fixture - never run");
    File.WriteAllText(Path.Combine(ordinary, "unins000.exe"), "test fixture - never run");
    var detected = MiniWorldLaunchService.ScanDirectory(ordinary);
    Check(detected.Count == 1 && detected[0].ExecutablePath == newer, "game entry preferred over legacy executable and uninstaller");
    var installation = MiniWorldLaunchService.CreateInstallation(newer, "official", "官方正式版");
    var start = MiniWorldLaunchService.CreateStartInfo(installation);
    Check(start.FileName == newer && start.WorkingDirectory == ordinary && !start.UseShellExecute, "ordinary game uses explicit executable and its working directory");
    installation.Arguments = new() { "--name=two words", "& calc.exe", "literal quote\"value" };
    start = MiniWorldLaunchService.CreateStartInfo(installation);
    Check(start.Arguments.Length == 0 && start.ArgumentList.SequenceEqual(installation.Arguments), "each argument stays separate without shell command interpolation");
    installation.Arguments = new() { "contains\0nul" };
    Reject<ArgumentException>(() => MiniWorldLaunchService.CreateStartInfo(installation), "NUL cannot enter process arguments");
    var installerOnly = Path.Combine(root, "installer-only"); Directory.CreateDirectory(installerOnly);
    var setup = Path.Combine(installerOnly, "miniworldoffice.exe"); File.WriteAllText(setup, "test fixture - never run");
    Check(MiniWorldLaunchService.ScanDirectory(installerOnly).Count == 0, "downloaded installer is not automatically discovered as a game");
    Reject<InvalidOperationException>(() => MiniWorldLaunchService.CreateInstallation(setup), "installer cannot be bound as game entry");
    Reject<InvalidOperationException>(() => MiniWorldLaunchService.CreateInstallation(Path.Combine(ordinary, "unins000.exe")), "uninstaller cannot be bound as game entry");
    File.WriteAllText(Path.Combine(ordinary, "cbl_game_info.json"), "[]");
    Check(MiniWorldLaunchService.CreateInstallation(newer).ExecutablePath == newer, "unrelated invalid metadata cannot block an ordinary game entry");
    var archive = Path.Combine(root, "community"); Directory.CreateDirectory(archive);
    var wrapper = Path.Combine(archive, "CBLministart.exe"); File.WriteAllText(wrapper, "test fixture - never run");
    Reject<FileNotFoundException>(() => MiniWorldLaunchService.CreateInstallation(wrapper), "community wrapper requires its accompanying metadata");
    File.WriteAllText(Path.Combine(archive, "cbl_game_info.json"), "{\"Name\":\"XHG local\",\"Version\":\"0.44.2\",\"ModifiedVersion\":\"4\",\"Channel\":\"121\"}");
    var community = MiniWorldLaunchService.CreateInstallation(wrapper, "community", "星辉阁", MiniWorldCatalog.ClientKey("community", "0.44.2-r4"));
    Check(community.Name == "XHG local" && community.VersionName.Contains("社区修订 4") && community.Arguments.SequenceEqual(new[] { "CBL_121" }), "community metadata supplies version and channel argument");
    community.Arguments.Clear();
    start = MiniWorldLaunchService.CreateStartInfo(community);
    Check(start.WorkingDirectory == archive && start.ArgumentList.Single() == "CBL_121", "legacy path-only wrapper binding receives its required launch protocol");
    community.WorkingDirectory = ordinary;
    Reject<InvalidOperationException>(() => MiniWorldLaunchService.CreateStartInfo(community), "community wrapper cannot accidentally launch with another version's working directory");
    File.WriteAllText(Path.Combine(archive, "cbl_game_info.json"), "{\"Channel\":\"1 & unwanted\"}");
    Reject<InvalidDataException>(() => MiniWorldLaunchService.CreateInstallation(wrapper), "invalid channel metadata is rejected");
    File.WriteAllText(Path.Combine(archive, "cbl_game_info.json"), "{\"Name\":\"default channel\"}");
    Check(MiniWorldLaunchService.SuggestedArguments(wrapper).Single() == "CBL_1", "missing channel uses maintainer's documented default");
    var staging = Path.Combine(root, ".staging"); Directory.CreateDirectory(staging); File.WriteAllText(Path.Combine(staging, "MicroMiniNew.exe"), "never run");
    Check(MiniWorldLaunchService.ScanDirectory(root).All(candidate => !candidate.ExecutablePath.Contains(".staging")), "download staging files are not exposed as installed games");
    Check(MiniWorldLaunchService.ScanDirectory(root).Count == 2, "known ordinary and community entries discovered independently");
    var missing = new MiniWorldLocalInstallation { ExecutablePath = Path.Combine(root, "missing.exe") };
    Reject<FileNotFoundException>(() => MiniWorldLaunchService.CreateStartInfo(missing), "moved game executable produces a real launch validation failure");
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"MiniWorld launch regression: {checks} checks passed (no fixture executable was run)");
