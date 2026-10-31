using System.IO.Compression;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
void Reject(Action action)
{
    try { action(); } catch (InvalidDataException) { checks++; return; }
    throw new Exception("Unsafe input was accepted");
}
var root = Path.Combine(Path.GetTempPath(), "xcl-boundary-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string Zip(string name, params (string Path, string Content)[] files)
{
    var path = Path.Combine(root, name + ".zip");
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var file in files) { using var writer = new StreamWriter(archive.CreateEntry(file.Path).Open()); writer.Write(file.Content); }
    return path;
}
try
{
    var current = new AppConfig { PreLaunchCommand = "local-command", CustomJvmArgs = "local-jvm", JavaPath = "local-java",
        SelectedJavaId = "local-id", InstalledJavas = new() { "local-java" }, VersionJavaIdOverrides = new() { ["v"] = "local-id" } };
    var imported = new AppConfig { PreLaunchCommand = "imported-command", CustomJvmArgs = "-javaagent:untrusted.jar", JavaPath = "untrusted.exe", AutoStartOnBoot = true };
    ConfigArchivePolicy.PreserveLocalAuthority(imported, current);
    Check(ReferenceEquals(imported.AiAssistant, current.AiAssistant), "Imported AI routing/grants replaced local authority");
    Check(imported.PreLaunchCommand == "local-command" && imported.CustomJvmArgs == "local-jvm" && imported.JavaPath == "local-java", "Imported execution configuration survived");
    Check(ReferenceEquals(imported.InstalledJavas, current.InstalledJavas) && ReferenceEquals(imported.VersionJavaIdOverrides, current.VersionJavaIdOverrides), "Imported Java selection survived");
    Check(!imported.AutoStartOnBoot, "Import enabled automatic startup");
    Check(ReferenceEquals(imported.DownloadMirrors, current.DownloadMirrors) && ReferenceEquals(imported.DefaultAuthServerApiRoot, current.DefaultAuthServerApiRoot), "Import redirected authentication or downloads");
    Check(ReferenceEquals(imported.TerracottaExecutablePath, current.TerracottaExecutablePath) && ReferenceEquals(imported.MiniWorldLocalInstallations, current.MiniWorldLocalInstallations), "Import replaced a registered executable");
    Check(ReferenceEquals(imported.Folders, current.Folders) && ReferenceEquals(imported.CustomBackgroundImagePath, current.CustomBackgroundImagePath) && ReferenceEquals(imported.SaveBackupProfiles, current.SaveBackupProfiles), "Import added a filesystem/network source");
    var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
    Check(ConfigArchivePolicy.ResolveInstanceDirectory(game + "|valid", new[] { game }) == Path.Combine(game, "versions", "valid"), "Valid local instance was rejected");
    foreach (var key in new[] { game + "|../outside", game + "|..\\outside", game + "|.", game + "|CON", game + "|a|b" })
        Reject(() => ConfigArchivePolicy.ResolveInstanceDirectory(key, new[] { game }));
    Check(ConfigArchivePolicy.ResolveInstanceDirectory(root + "|outside", new[] { game }) == null, "Archive authorized an unregistered root");
    var instance = new InstanceSettings { CustomJvmArgs = "-javaagent:imported.jar", JavaId = "imported" };
    ConfigArchivePolicy.PreserveInstanceAuthority(instance, null);
    Check(instance.CustomJvmArgs == null && instance.JavaId == null, "New instance acquired imported executable settings");

    var drops = new DragDropInstallService();
    var world = Zip("valid-world", ("wrapped/level.dat", "world"), ("wrapped/region/chunk.mca", "chunk"));
    var good = drops.InstallMany(new[] { world }, game);
    Check(good.Installed.Count == 1 && File.ReadAllText(Path.Combine(game, "saves", "valid-world", "level.dat")) == "world", "Wrapped world import changed behavior");
    var repeated = drops.InstallMany(new[] { world }, game);
    Check(repeated.Installed.Count == 1 && Directory.Exists(Path.Combine(game, "saves", "valid-world (2)")), "Second import replaced an existing world");
    var evil = Zip("evil-world", ("level.dat", "world"), ("../outside.txt", "unsafe"));
    var bad = drops.InstallMany(new[] { evil }, game);
    Check(bad.Installed.Count == 0 && bad.Skipped.Count == 1 && !Directory.Exists(Path.Combine(game, "saves", "evil-world")), "Unsafe world was partially imported");
    var linkedZip = Zip("linked-world", ("level.dat", "world"), ("escape", "../outside"));
    using (var zip = ZipFile.Open(linkedZip, ZipArchiveMode.Update)) zip.GetEntry("escape")!.ExternalAttributes = unchecked((int)0xA1FF0000);
    Check(drops.InstallMany(new[] { linkedZip }, game).Installed.Count == 0, "ZIP link was accepted");

    var server = Path.Combine(root, "server"); var mods = Path.Combine(server, "mods"); Directory.CreateDirectory(mods);
    File.WriteAllText(Path.Combine(mods, "keep.jar"), "keep");
    Check(LeviLaminaService.ResolveModsDirectory(server, true) == mods && File.ReadAllText(Path.Combine(mods, "keep.jar")) == "keep", "Installer deleted an existing mod directory");
    XCL2.App.App.DataDir = Path.Combine(root, "launcher-data");
    var installer = new LeviLaminaService();
    var installedDll = Path.Combine(server, "config", "BedrockBoot2", "mods", "PreLoader.dll");
    Directory.CreateDirectory(Path.GetDirectoryName(installedDll)!); File.WriteAllText(installedDll, "old-dll");
    var modsConfig = Path.Combine(server, "config", "BedrockBoot2", "mods.json"); File.WriteAllText(modsConfig, "[]");
    var badPreloader = Zip("invalid-preloader", ("bin/PreLoader.dll", "new-dll"), ("../outside.dll", "unsafe"));
    Reject(() => installer.InstallPreLoader(badPreloader, server).GetAwaiter().GetResult());
    Check(File.ReadAllText(installedDll) == "old-dll" && File.ReadAllText(modsConfig) == "[]", "Rejected preloader modified an existing installation");
    var missingPreloader = Zip("missing-preloader", ("other.txt", "no dll"));
    try { installer.InstallPreLoader(missingPreloader, server).GetAwaiter().GetResult(); throw new Exception("Missing DLL was accepted"); }
    catch (FileNotFoundException) { checks++; }
    Check(File.ReadAllText(installedDll) == "old-dll" && File.ReadAllText(modsConfig) == "[]", "Missing DLL removed the existing installation");
    installer.InstallPreLoader(Zip("valid-preloader", ("bin/PreLoader.dll", "new-dll")), server).GetAwaiter().GetResult();
    Check(File.ReadAllText(installedDll) == "new-dll" && File.ReadAllText(modsConfig).Contains("PreLoader.dll"), "Valid preloader installation failed");
    Check(!Directory.EnumerateDirectories(Path.Combine(XCL2.App.App.DataDir, "BedrockBoot.LeviLamina", "Temp")).Any(), "Preloader staging directories were left behind");
    var linkedServer = Path.Combine(root, "linked-server"); Directory.CreateDirectory(linkedServer);
    var destination = Path.Combine(linkedServer, "config", "BedrockBoot2", "mods"); Directory.CreateDirectory(destination);
    var link = Path.Combine(linkedServer, "mods");
    try
    {
        Directory.CreateSymbolicLink(link, destination);
        Check(LeviLaminaService.ResolveModsDirectory(linkedServer) == destination, "Launcher-owned internal link must resolve to its checked real directory");
        Directory.Delete(link); Directory.CreateSymbolicLink(link, root);
        Reject(() => LeviLaminaService.ResolveModsDirectory(linkedServer)); Directory.Delete(link);
        var resourceLink = Path.Combine(game, "resourcepacks"); Directory.CreateSymbolicLink(resourceLink, root);
        var resource = Zip("resource", ("pack.mcmeta", "{}"), ("assets/a", "a"));
        Check(drops.InstallMany(new[] { resource }, game).Installed.Count == 0, "Resource import wrote through a target link");
        Directory.Delete(resourceLink);
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
    { Console.WriteLine("SKIP symbolic-link creation: unavailable on this platform"); }
    Console.WriteLine($"Security boundary regression: {checks} checks passed.");
}
finally { Directory.Delete(root, recursive: true); }
