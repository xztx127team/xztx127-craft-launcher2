using System.IO.Compression;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string scenario)
{
    checks++;
    if (!condition) throw new InvalidOperationException(scenario);
}
var root = Path.Combine(Path.GetTempPath(), "xcl-mod-management-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var filters = ModManagementPresentation.DownloadFilters(" 1.21.1 ", " NeoForge ");
    Check(filters.MinecraftVersion == "1.21.1" && filters.Loader == "neoforge", "instance download filters normalize loader casing and whitespace");
    Check(ModManagementPresentation.DownloadFilters("b1.7.3", "Fabric").MinecraftVersion == "b1.7.3",
        "legacy Minecraft identifiers do not silently turn into an unfiltered modern search");
    Check(ModManagementPresentation.DownloadFilters("1.20.1", "QuIlT").Loader == "quilt", "all supported loader tags use their actual ComboBox casing");
    Check(ModManagementPresentation.DownloadFilters("1.12.2", "Cleanroom").Loader == "", "unknown loader families do not leave a stale previous selection");
    var overrides = new Dictionary<string, bool>();
    var instance = Path.Combine(root, "versions", "my-fabric-instance");
    Directory.CreateDirectory(instance);
    Check(ModManagementPresentation.GameDirectory(root, "my-fabric-instance", true, overrides) == instance,
        "isolated installs and the mod manager use the same instance directory");
    Check(ModManagementPresentation.GameDirectory(root, "my-fabric-instance", false, overrides) == root,
        "shared game directories do not silently download into an unused isolated directory");
    overrides["my-fabric-instance"] = false;
    Check(ModManagementPresentation.GameDirectory(root, "my-fabric-instance", true, overrides) == root,
        "a per-instance shared override takes precedence over the global default");
    overrides["my-fabric-instance"] = true;
    Check(ModManagementPresentation.GameDirectory(root, "my-fabric-instance", false, overrides) == instance,
        "a per-instance isolated override takes precedence over the global default");
    Check(ModManagementPresentation.GameDirectory(root, null, true, overrides) == root,
        "a missing selection does not invent an instance directory");

    var service = new LocalModService();
    var modsDirectory = Path.Combine(instance, "mods");
    Check(service.ScanMods(instance).Count == 0 && !Directory.Exists(modsDirectory), "reading an empty instance does not create folders");
    File.WriteAllText(modsDirectory, "a file cannot be used as a mods directory");
    bool invalidModsLocationFailed = false;
    try { service.ScanMods(instance); } catch (IOException) { invalidModsLocationFailed = true; }
    Check(invalidModsLocationFailed, "an invalid mods location is a scan failure, never an empty instance");
    File.Delete(modsDirectory);
    Check(ModManagementPresentation.WatchDirectory(modsDirectory) == instance,
        "the absent mods directory is monitored through its existing parent");
    Check(ModManagementPresentation.AffectsMods(modsDirectory, modsDirectory), "first mods directory creation is relevant");
    Check(ModManagementPresentation.AffectsMods(Path.Combine(modsDirectory, "first.jar"), modsDirectory), "the first jar copied into mods is relevant");
    Check(!ModManagementPresentation.AffectsMods(Path.Combine(instance, "mods-backup", "old.jar"), modsDirectory), "a similarly named sibling must not cause rescans");
    Check(!ModManagementPresentation.AffectsMods(Path.Combine(instance, "logs", "latest.log"), modsDirectory), "game log writes must not continuously rescan mods");
    Check(ModManagementPresentation.AffectsMods(instance, modsDirectory), "renaming or deleting an ancestor invalidates the watched location");

    Check(ModManagementPresentation.Resolve(false, true, false, 0, 0) == ModManagementState.Empty, "a verified empty instance displays the add-mods invitation");
    Check(ModManagementPresentation.Resolve(true, true, false, 0, 0) == ModManagementState.Loading, "a scan in progress does not flash the empty invitation");
    Check(ModManagementPresentation.Resolve(false, false, false, 0, 0) == ModManagementState.SelectionRequired, "missing selection asks for a version");
    Check(ModManagementPresentation.Resolve(false, true, true, 0, 0) == ModManagementState.Failed, "a failed scan is not reported as no installed mods");

    Directory.CreateDirectory(modsDirectory);
    var jar = Path.Combine(modsDirectory, "sample.jar");
    using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()))
        writer.Write("{\"id\":\"sample\",\"name\":\"Sample Mod\"}");
    File.WriteAllText(Path.Combine(modsDirectory, "readme.txt"), "not a mod");
    var installed = service.ScanMods(instance);
    Check(installed.Count == 1 && installed[0].DisplayName == "Sample Mod", "real jar metadata replaces the empty invitation with installed mods");
    Check(ModManagementPresentation.Resolve(false, true, false, installed.Count, 0) == ModManagementState.FilterEmpty,
        "a zero-result filter keeps installed-mod actions and never shows add-first-mod copy");
    var disabledPath = service.Disable(jar);
    installed = service.ScanMods(instance);
    Check(installed.Count == 1 && !installed[0].IsEnabled, "disabled jar files still count as installed mods");
    Check(ModManagementPresentation.Resolve(false, true, false, installed.Count, installed.Count) == ModManagementState.Ready,
        "an instance containing only disabled mods still displays its list");
    Check(ModManagementPresentation.WatchDirectory(modsDirectory) == modsDirectory, "the watcher narrows to mods after the folder exists");
    service.Delete(disabledPath);
    Check(service.ScanMods(instance).Count == 0, "deleting the last mod returns to the genuine empty state");
    var brokenJar = Path.Combine(modsDirectory, "broken.jar");
    File.WriteAllText(brokenJar, "not a zip archive");
    var brokenMods = service.ScanMods(instance);
    Check(brokenMods.Count == 1 && brokenMods[0].DisplayName == "broken.jar",
        "a corrupt jar keeps its filename fallback instead of making the instance appear empty");
    var disabledBrokenJar = service.Disable(brokenJar);
    Check(service.ScanMods(instance) is [{ IsEnabled: false, DisplayName: "broken.jar" }],
        "a disabled corrupt jar is still an installed mod");
    service.Delete(disabledBrokenJar);
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    bool canceledScan = false;
    try { service.ScanMods(instance, canceled.Token); } catch (OperationCanceledException) { canceledScan = true; }
    Check(canceledScan, "a canceled scan cannot publish a stale empty result");
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"PASS: {checks} installed-mod state and directory checks.");
