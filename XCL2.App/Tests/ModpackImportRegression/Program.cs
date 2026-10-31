using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var root = Path.Combine(Path.GetTempPath(), "XCL2-ModpackRegression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
void Reject<T>(Action action, string name) where T : Exception
{ try { action(); } catch (T) { checks++; Console.WriteLine("PASS " + name); return; } throw new Exception("Expected rejection: " + name); }
async Task RejectAsync<T>(Func<Task> action, string name) where T : Exception
{ try { await action(); } catch (T) { checks++; Console.WriteLine("PASS " + name); return; } throw new Exception("Expected rejection: " + name); }
string Pack(string name, params (string Name, string Text)[] entries)
{
    var path = Path.Combine(root, name);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var entry in entries) { using var writer = new StreamWriter(archive.CreateEntry(entry.Name).Open()); writer.Write(entry.Text); }
    return path;
}
try
{
    var service = new ModpackService();
    var fixtureBytes = Encoding.UTF8.GetBytes("fixture");
    var sha1 = Convert.ToHexString(SHA1.HashData(fixtureBytes)).ToLowerInvariant();
    var sha512 = Convert.ToHexString(SHA512.HashData(fixtureBytes)).ToLowerInvariant();
    var empty = Path.Combine(root, "empty"); Directory.CreateDirectory(empty);
    var existing = Path.Combine(root, "backup.xclpack"); File.WriteAllText(existing, "original backup");
    Reject<InvalidOperationException>(() => service.Export(empty, existing, new ModpackManifest { McVersion = "1.20.1" }), "failed XCL export preserves existing backup");
    Check(File.ReadAllText(existing) == "original backup", "XCL backup bytes unchanged");
    Reject<InvalidOperationException>(() => service.ExportMrpack(empty, existing, new ModpackManifest { McVersion = "1.20.1" }), "failed mrpack export preserves backup");
    Check(File.ReadAllText(existing) == "original backup", "mrpack backup bytes unchanged");
    Reject<IOException>(() => ArchiveImportSafety.ExportAtomically(existing, path => { File.WriteAllText(path, "partial"); throw new IOException("fixture disk failure"); }), "failure after staging does not replace target");
    Check(File.ReadAllText(existing) == "original backup" && !Directory.EnumerateFiles(root, ".xcl2-export-*").Any(), "failed staging cleaned and original kept");
    var source = Path.Combine(root, "source"); Directory.CreateDirectory(Path.Combine(source, "mods"));
    File.WriteAllText(Path.Combine(source, "mods", "fixture.jar"), "jar fixture");
    service.Export(source, existing, new ModpackManifest { Name = "Fixture", McVersion = "1.20.1" });
    using (var zip = ZipFile.OpenRead(existing)) Check(zip.GetEntry("xclpack.json") != null && zip.GetEntry("mods/fixture.jar") != null, "successful export replaces with complete readable archive");
    var install = new ModpackInstallService(new AppConfig());
    Check(install.ReadRequirements(existing).McVersion == "1.20.1", "real exported xclpack manifest is recognized by full installer");
    var gameRoot = Path.Combine(root, "game");
    var installed = await install.InstallToNewInstanceAsync(existing, gameRoot, "My pack", null, null);
    Check(DownloadService.Installed == 1 && File.Exists(Path.Combine(installed.InstanceDir, "My pack.json")) && File.Exists(Path.Combine(installed.InstanceDir, "My pack.jar")), "full pack installation actually provisions game version before marking ready");
    Check(File.Exists(Path.Combine(installed.InstanceDir, "mods", "fixture.jar")), "full installation includes pack content");
    var loaderPack = Pack("loader.mrpack", ("modrinth.index.json", "{\"name\":\"Loader\",\"dependencies\":{\"minecraft\":\"1.20.1\",\"fabric-loader\":\"0.16\"},\"files\":[]}"), ("overrides/config/test.txt", "config"));
    var loaded = await install.InstallToNewInstanceAsync(loaderPack, gameRoot, "Loader pack", null, null);
    Check(ClientLoaderInstallService.Installed == 1 && File.Exists(Path.Combine(loaded.InstanceDir, "Loader pack.json")), "full installation provisions declared loader");
    Check(File.ReadAllText(Path.Combine(loaded.InstanceDir, "Loader pack.json")).Contains("fixture.Loader", StringComparison.Ordinal) && File.Exists(Path.Combine(loaded.InstanceDir, "installer-notes.json")), "auxiliary JSON does not replace the primary version description");
    Check(Directory.Exists(Path.Combine(gameRoot, "versions", "fabric-loader-fixture-1.20.1")), "pack import does not delete loader source version that may be shared");
    var bad = Pack("escape.zip", ("mods/../../Pack-evil/out.txt", "escape"));
    var target = Path.Combine(root, "Pack");
    Reject<InvalidDataException>(() => service.Import(bad, target), "bottom-level writer rejects same-prefix sibling traversal without UI preview");
    Check(!Directory.Exists(target) && !Directory.Exists(Path.Combine(root, "Pack-evil")), "invalid archive creates neither target nor escaped directory");
    foreach (var name in new[] { "mods/../escape.txt", "mods/CON.txt", "mods/file.txt:secret", "mods/dir./file", "mods/./file" })
        Reject<InvalidDataException>(() => ArchiveImportSafety.Resolve(target, name), "reject unsafe path " + name);
    var linkArchive = Path.Combine(root, "symlink.zip");
    using (var zip = ZipFile.Open(linkArchive, ZipArchiveMode.Create))
    { var entry = zip.CreateEntry("mods/link.jar"); entry.ExternalAttributes = unchecked((int)0xA1FF0000); using var writer = new StreamWriter(entry.Open()); writer.Write("outside"); }
    Reject<InvalidDataException>(() => service.Import(linkArchive, target), "ZIP symlink rejected by actual writer");
    try
    {
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateDirectory(target); Directory.CreateSymbolicLink(Path.Combine(target, "mods"), outside);
        Reject<InvalidDataException>(() => service.Import(existing, target), "target directory link rejected independently of preview");
        Check(!File.Exists(Path.Combine(outside, "fixture.jar")), "no file written through directory link");
        Directory.Delete(Path.Combine(target, "mods"));
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException) { Console.WriteLine("SKIP symbolic links: platform permission"); }
    var sidePack = Pack("sides.mrpack",
        ("modrinth.index.json", JsonSerializer.Serialize(new MrpackIndex { Name = "Sides", Files = new()
        {
            new() { Path = "mods/client.jar", Downloads = new() { "https://fixture.invalid/client" }, FileSize = fixtureBytes.Length,
                Hashes = new() { ["sha1"] = sha1, ["sha512"] = sha512 }, Env = new() { Client = "required", Server = "unsupported" } },
            new() { Path = "mods/server.jar", Downloads = new() { "https://fixture.invalid/server" }, FileSize = fixtureBytes.Length,
                Hashes = new() { ["sha1"] = sha1, ["sha512"] = sha512 }, Env = new() { Client = "unsupported", Server = "required" } }
        } })),
        ("server-overrides/config/side.txt", "server"), ("client-overrides/config/side.txt", "client"), ("overrides/config/side.txt", "common"));
    var server = Path.Combine(root, "server"); await service.ImportMrpackAsync(sidePack, server, null, default, server: true);
    Check(File.Exists(Path.Combine(server, "mods", "server.jar")) && !File.Exists(Path.Combine(server, "mods", "client.jar")), "server import filters by server environment");
    Check(File.ReadAllText(Path.Combine(server, "config", "side.txt")) == "server", "server-specific override wins regardless of ZIP entry ordering");
    foreach (var mode in new[] { "missing-hashes", "missing-sha512", "invalid-sha512", "bad-url", "duplicate", "null-file" })
    {
        var entry = new MrpackFile { Path = "mods/safe.jar", Downloads = new() { "https://fixture.invalid/safe" },
            FileSize = fixtureBytes.Length, Hashes = new() { ["sha1"] = sha1, ["sha512"] = sha512 } };
        var index = new MrpackIndex { Files = new() { entry } };
        if (mode == "missing-hashes") entry.Hashes.Clear();
        if (mode == "missing-sha512") entry.Hashes.Remove("sha512");
        if (mode == "invalid-sha512") entry.Hashes["sha512"] = "not-a-sha512";
        if (mode == "bad-url") entry.Downloads = new() { "http://fixture.invalid/unsafe" };
        if (mode == "duplicate") index.Files.Add(entry);
        if (mode == "null-file") index.Files.Add(null!);
        var unsafePack = Pack(mode + ".mrpack", ("modrinth.index.json", JsonSerializer.Serialize(index)),
            ("overrides/config/protected.txt", "must-not-overwrite"));
        var unsafeTarget = Path.Combine(root, mode); Directory.CreateDirectory(Path.Combine(unsafeTarget, "config"));
        var protectedFile = Path.Combine(unsafeTarget, "config", "protected.txt"); File.WriteAllText(protectedFile, "original");
        var downloadsBefore = DownloadEndpoints.Written.Count;
        await RejectAsync<InvalidDataException>(() => service.ImportMrpackAsync(unsafePack, unsafeTarget), mode);
        Check(File.ReadAllText(protectedFile) == "original" && DownloadEndpoints.Written.Count == downloadsBefore,
            mode + " rejected before any content write or download");
    }
    var client = Path.Combine(root, "client"); await service.ImportMrpackAsync(sidePack, client);
    Check(File.Exists(Path.Combine(client, "mods", "client.jar")) && !File.Exists(Path.Combine(client, "mods", "server.jar")) && File.ReadAllText(Path.Combine(client, "config", "side.txt")) == "client", "client imports retain client filtering and overrides");
    var cf = Pack("curseforge.zip", ("manifest.json", "{\"name\":\"CF\",\"minecraft\":{\"version\":\"1.20.1\"},\"files\":[{\"projectID\":12,\"fileID\":34,\"required\":true}],\"overrides\":\"overrides\"}"), ("overrides/config/cf.txt", "cf"));
    var cfTarget = Path.Combine(root, "cf"); var resolved = 0;
    var cfPreview = ImportPreviewService.Inspect(cf, cfTarget, "CF", ImportPreviewKind.ModpackArchive);
    Check(cfPreview.FileCount == 1 && cfPreview.PendingDownloadCount == 1 && !Directory.Exists(cfTarget), "CurseForge preview counts real overrides and marks unresolved downloads separately");
    await service.ImportCurseForgeCoreAsync(cf, cfTarget, (project, file, ct) =>
    {
        Check(project == 12 && file == 34, "CurseForge resolves exact project and file IDs"); resolved++;
        return Task.FromResult(new ModpackService.CurseForgePackFile("cf.jar", "https://fixture.invalid/cf", 7, sha1, null));
    });
    Check(resolved == 1 && File.Exists(Path.Combine(cfTarget, "mods", "cf.jar")) && File.ReadAllText(Path.Combine(cfTarget, "config", "cf.txt")) == "cf", "standard CurseForge package applies resolved mod and overrides");
    var denied = Path.Combine(root, "cf-denied");
    await RejectAsync<InvalidDataException>(() => service.ImportCurseForgeCoreAsync(cf, denied, (_, _, _) => Task.FromResult(new ModpackService.CurseForgePackFile("cf.jar", "", 7, "fixture", null))), "restricted CurseForge file is explicitly rejected");
    Check(!Directory.Exists(denied), "missing download authorization leaves overrides and content untouched");
    foreach (var hashes in new (string? Sha1, string? Md5)[] { (null, null), ("malformed", null), (sha1, "malformed") })
    {
        var unverified = Path.Combine(root, "cf-unverified-" + Guid.NewGuid().ToString("N"));
        var downloadsBefore = DownloadEndpoints.Written.Count;
        await RejectAsync<InvalidDataException>(() => service.ImportCurseForgeCoreAsync(cf, unverified, (_, _, _) =>
            Task.FromResult(new ModpackService.CurseForgePackFile("cf.jar", "https://fixture.invalid/cf", 7, hashes.Sha1, hashes.Md5))),
            "missing or malformed CurseForge integrity metadata");
        Check(!Directory.Exists(unverified) && DownloadEndpoints.Written.Count == downloadsBefore, "unverified CurseForge metadata caused content writes");
    }
    await RejectAsync<InvalidOperationException>(() => service.ImportCurseForgeAsync(cf, Path.Combine(root, "no-key")), "missing API key is an explicit failure");
    var badManifest = Pack("missing-version.xclpack", ("xclpack.json", "{\"Name\":\"Missing\"}"), ("mods/a.jar", "a"));
    await RejectAsync<InvalidOperationException>(() => install.InstallToNewInstanceAsync(badManifest, gameRoot, "Missing", null, null), "missing game version cannot be marked installed");
    Check(!Directory.Exists(Path.Combine(gameRoot, "versions", "Missing")), "missing requirements do not leave a fake instance");
    XCL2.App.Services.Plugins.PluginInterceptionRegistry.BeforeValidate = (minecraftRoot, id) =>
    {
        var contested = Path.Combine(minecraftRoot, "versions", id); Directory.CreateDirectory(contested);
        File.WriteAllText(Path.Combine(contested, "other-task.txt"), "keep");
    };
    try
    {
        await RejectAsync<IOException>(() => install.InstallToNewInstanceAsync(existing, gameRoot, "Contended", null, null), "concurrent target creation cannot be claimed as this installer's own directory");
        Check(File.ReadAllText(Path.Combine(gameRoot, "versions", "Contended", "other-task.txt")) == "keep", "failed competing install does not delete the other task's instance");
    }
    finally { XCL2.App.Services.Plugins.PluginInterceptionRegistry.BeforeValidate = null; }
    var folders = new FolderService();
    var folderSource = Path.Combine(root, "folder-source"); Directory.CreateDirectory(Path.Combine(folderSource, "mods"));
    File.WriteAllText(Path.Combine(folderSource, "fixture.json"), "{\"id\":\"fixture\",\"mainClass\":\"fixture.Main\"}");
    File.WriteAllText(Path.Combine(folderSource, "fixture.jar"), "jar");
    File.WriteAllText(Path.Combine(folderSource, "mods", "safe.jar"), "safe");
    folders.ImportVersionFolder(gameRoot, folderSource, "Folder-import");
    Check(File.Exists(Path.Combine(gameRoot, "versions", "Folder-import", "fixture.json")) && File.Exists(Path.Combine(gameRoot, "versions", "Folder-import", "mods", "safe.jar")), "folder import publishes complete version only after staged copy");
    Reject<InvalidOperationException>(() => folders.ImportVersionFolder(gameRoot, folderSource, "Folder-import"), "folder import never merges into an existing instance");
    var metadataFree = Path.Combine(root, "metadata-free"); Directory.CreateDirectory(metadataFree); File.WriteAllText(Path.Combine(metadataFree, "notes.json"), "{}");
    Reject<InvalidDataException>(() => folders.ImportVersionFolder(gameRoot, metadataFree, "Not-a-version"), "unrelated JSON folder cannot be marked as an imported version");
    Check(!Directory.Exists(Path.Combine(gameRoot, "versions", "Not-a-version")), "invalid folder leaves no final instance");
    Reject<InvalidDataException>(() => folders.ImportVersionFolder(folderSource, folderSource, "Recursive"), "copying into the source tree is rejected rather than recursively copying staging into itself");
    using (var stop = new CancellationTokenSource())
    {
        stop.Cancel();
        await RejectAsync<OperationCanceledException>(() => folders.ImportVersionFolderAsync(gameRoot, folderSource, "Canceled-folder", stop.Token), "folder import respects cancellation");
        Check(!Directory.Exists(Path.Combine(gameRoot, "versions", "Canceled-folder")), "canceled folder never appears as an installed instance");
    }
    try
    {
        var outside = Path.Combine(root, "link-fixture"); Directory.CreateDirectory(outside);
        var linked = Path.Combine(folderSource, "mods", "z-link"); Directory.CreateSymbolicLink(linked, outside);
        Reject<InvalidDataException>(() => folders.ImportVersionFolder(gameRoot, folderSource, "Failed-folder"), "deep link failure cleans only staged content");
        Check(!Directory.Exists(Path.Combine(gameRoot, "versions", "Failed-folder")) && !Directory.EnumerateDirectories(Path.Combine(gameRoot, "versions"), ".xcl2-import-*").Any(), "failed staged folder copy leaves no half instance or staging directory");
        Directory.Delete(linked);
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException) { Console.WriteLine("SKIP folder-link rollback: platform permission"); }
    var wrappedInstance = Pack("wrapped-instance.zip", ("wrapper/.minecraft/versions/fixture/fixture.json", "{\"id\":\"fixture\",\"mainClass\":\"fixture.Main\"}"), ("wrapper/.minecraft/versions/fixture/fixture.jar", "jar"));
    folders.ImportVersionZip(gameRoot, wrappedInstance, "Wrapped-instance");
    Check(File.Exists(Path.Combine(gameRoot, "versions", "Wrapped-instance", "fixture.json")) && File.Exists(Path.Combine(gameRoot, "versions", "Wrapped-instance", "fixture.jar")), "wrapped ZIP stages and publishes the actual instance root");
    void Version(string id, string json, bool jar = false)
    {
        var directory = Path.Combine(gameRoot, "versions", id); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), json);
        if (jar) File.WriteAllText(Path.Combine(directory, id + ".jar"), "client");
    }
    Version("Chain-base", "{\"id\":\"Chain-base\",\"mainClass\":\"fixture.Main\",\"downloads\":{\"client\":{\"url\":\"https://fixture.invalid/base\"}},\"assetIndex\":{\"id\":\"chain-assets\"},\"libraries\":[{\"name\":\"fixture:base:1\"}]}", true);
    Version("Chain-middle", "{\"id\":\"Chain-middle\",\"inheritsFrom\":\"Chain-base\",\"libraries\":[{\"name\":\"fixture:middle:1\"}]}");
    Version("Chain-leaf", "{\"id\":\"Chain-leaf\",\"inheritsFrom\":\"Chain-middle\",\"mainClass\":\"fixture.Loader\"}");
    Directory.CreateDirectory(Path.Combine(gameRoot, "assets", "indexes"));
    File.WriteAllText(Path.Combine(gameRoot, "assets", "indexes", "chain-assets.json"), "{\"objects\":{}}");
    Check(folders.ScanVersions(gameRoot).Single(row => row.Id == "Chain-leaf").IsInstalled, "real folder scan recognizes client JAR three layers away");
    var integrity = new InstanceIntegrityService(new LauncherService());
    var chain = integrity.Check(gameRoot, "Chain-leaf", IntegrityCheckMode.Strict);
    Check(!chain.VersionJsonMissing && !chain.ClientJarMissing && chain.MissingAssets.Count == 0, "real integrity check accepts multi-layer client and inherited assets");
    Check(chain.ClientVersionId == "Chain-base" && chain.Detail?.InheritsFrom == "Chain-middle" && chain.EffectiveDetail?.Libraries.Count == 2, "repair receives selected detail plus actual client owner and merged effective metadata");
    File.Delete(Path.Combine(gameRoot, "versions", "Chain-base", "Chain-base.jar"));
    Check(integrity.Check(gameRoot, "Chain-leaf", IntegrityCheckMode.Simple).ClientJarMissing && !folders.ScanVersions(gameRoot).Single(row => row.Id == "Chain-leaf").IsInstalled, "scan and integrity agree when the actual ancestor client disappears");
    Version("Cycle-A", "{\"id\":\"Cycle-A\",\"inheritsFrom\":\"Cycle-B\"}");
    Version("Cycle-B", "{\"id\":\"Cycle-B\",\"inheritsFrom\":\"Cycle-A\"}");
    Check(integrity.Check(gameRoot, "Cycle-A", IntegrityCheckMode.Simple).VersionJsonMissing && !folders.ScanVersions(gameRoot).Single(row => row.Id == "Cycle-A").IsInstalled, "cycle stays visible but is never treated as complete");
    Version("Jar-owner", "{\"id\":\"Jar-owner\",\"mainClass\":\"fixture.Main\"}", true);
    Version("Jar-alias", "{\"id\":\"Jar-alias\",\"mainClass\":\"fixture.Main\",\"jar\":\"Jar-owner\"}");
    Check(!integrity.Check(gameRoot, "Jar-alias", IntegrityCheckMode.Simple).ClientJarMissing && folders.ScanVersions(gameRoot).Single(row => row.Id == "Jar-alias").IsInstalled, "explicit jar owner is consistent across real scan and integrity entry points");
    Version("Metadata-only", "{}", true);
    Check(integrity.Check(gameRoot, "Metadata-only", IntegrityCheckMode.Simple).VersionJsonMissing && !folders.ScanVersions(gameRoot).Single(row => row.Id == "Metadata-only").IsInstalled, "JSON object without a main class is not a complete runnable instance");
    var badReadyPack = Pack("bad-ready.mrpack", ("modrinth.index.json", "{\"name\":\"Bad metadata\",\"dependencies\":{\"minecraft\":\"1.20.1\"},\"files\":[]}"), ("overrides/Bad-ready.json", "{}"));
    await RejectAsync<InvalidDataException>(() => install.InstallToNewInstanceAsync(badReadyPack, gameRoot, "Bad-ready", null, null), "pack overrides cannot silently turn valid provisioning into fake runnable metadata");
    Check(!Directory.Exists(Path.Combine(gameRoot, "versions", "Bad-ready")) && File.Exists(Path.Combine(gameRoot, "versions", "1.20.1", "1.20.1.jar")), "failed final readiness check removes only this new instance and preserves shared base");
    Console.WriteLine($"{checks} regression assertions passed.");
}
finally { try { Directory.Delete(root, recursive: true); } catch { } }
