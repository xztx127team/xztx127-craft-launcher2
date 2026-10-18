using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
void Rejected(Action action, string name) { try { action(); } catch (IOException) { checks++; return; } throw new Exception(name); }
var root = Path.Combine(Path.GetTempPath(), "xcl2-ux-backups-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    var saves = Path.Combine(root, "saves"); var world = Path.Combine(saves, "Survival"); Directory.CreateDirectory(Path.Combine(world, "region"));
    File.WriteAllText(Path.Combine(world, "level.dat"), "original level"); File.WriteAllText(Path.Combine(world, "region", "r.0.0.mca"), "chunks");
    var store = new SaveBackupService(Path.Combine(root, "backups")); var backup = store.Create(world, "Survival");
    Check(store.List(world).Single() == backup, "Durable backup metadata was not listed");
    Check(File.ReadAllText(Path.Combine(world, "level.dat")) == "original level", "Backup modified original save");
    File.WriteAllText(Path.Combine(world, "level.dat"), "newer progress");
    var restored = store.RestoreCopy(world, backup.Id, "Survival-restored");
    Check(File.ReadAllText(Path.Combine(restored, "level.dat")) == "original level", "Copy restore failed");
    Check(File.ReadAllText(Path.Combine(world, "level.dat")) == "newer progress", "Restore overwrote later save changes");
    Check(File.ReadAllText(Path.Combine(restored, "region", "r.0.0.mca")) == "chunks", "Restore lost region files");
    Rejected(() => store.RestoreCopy(world, backup.Id, "Survival-restored"), "Existing copy overwritten");
    foreach (var name in new[] { "..", "../outside", "a/b", "a\\b", "C:stream", "a.", "a ", "CON", "aux.txt", "LPT1" })
        Rejected(() => SaveBackupService.CopyTarget(world, name), "Unsafe restore name accepted: " + name);
    Rejected(() => new SaveBackupService(Path.Combine(world, "backups")).Create(world, "world"), "Recursive backup allowed");
    var scheduled1 = store.Create(world, "Survival", true); var scheduled2 = store.Create(world, "Survival", true);
    store.TrimScheduled(world, 1);
    Check(store.List(world).Count(r => r.Scheduled) == 1 && store.List(world).Any(r => r.Id == backup.Id), "Retention removed manual backup or kept excess scheduled backups");
    using (var cancel = new CancellationTokenSource())
    {
        cancel.Cancel(); var canceled = false;
        try { store.Create(world, "world", ct: cancel.Token); } catch (OperationCanceledException) { canceled = true; }
        Check(canceled && !Directory.EnumerateFiles(store.DirectoryPath, "*.part", SearchOption.AllDirectories).Any(), "Canceled backup left complete/partial artifacts");
    }
    var deep = Path.Combine(saves, "deep"); Directory.CreateDirectory(deep); File.WriteAllText(Path.Combine(deep, "level.dat"), "x");
    var cursor = deep; for (var i = 0; i < 130; i++) { cursor = Path.Combine(cursor, "d"); Directory.CreateDirectory(cursor); }
    Rejected(() => store.Create(deep, "deep"), "Deep tree was not bounded");
    using (var held = new FileStream(Path.Combine(world, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        Rejected(() => store.Create(world, "locked"), "Backup read a locked running world");
    using (var savedZip = ZipFile.OpenRead(Path.Combine(store.DirectoryPath, SaveBackupService.Key(world), backup.Id + ".zip")))
        Check(!savedZip.Entries.Any(e => e.FullName == "session.lock"), "Backup included active session lock");
    var archivePath = Path.Combine(store.DirectoryPath, SaveBackupService.Key(world), backup.Id + ".zip");
    File.AppendAllText(archivePath, "corruption");
    Rejected(() => store.RestoreCopy(world, backup.Id, "corrupt-copy"), "Corrupt archive accepted");
    Check(!Directory.Exists(Path.Combine(saves, "corrupt-copy")), "Corrupt restore wrote target");

    var manyStore = new SaveBackupService(Path.Combine(root, "many-backups")); var firstRecord = manyStore.Create(world, "world");
    var manyFolder = Path.Combine(manyStore.DirectoryPath, SaveBackupService.Key(world)); var firstArchive = Path.Combine(manyFolder, firstRecord.Id + ".zip");
    SaveBackupRecord? lastRecord = null;
    for (var i = 0; i < 2001; i++)
    {
        var id = Guid.NewGuid().ToString("N"); lastRecord = firstRecord with { Id = id, CreatedUtc = DateTime.UtcNow.AddTicks(i), Scheduled = i == 2000 };
        File.Copy(firstArchive, Path.Combine(manyFolder, id + ".zip")); File.WriteAllText(Path.Combine(manyFolder, id + ".json"), JsonSerializer.Serialize(lastRecord));
    }
    Check(manyStore.List(world).Count == 2002 && manyStore.List(world).First(r => r.Scheduled).Id == lastRecord!.Id, "Older enumeration cap hid latest backup/scheduled record");

    // Even a syntactically valid manifest/hash must not allow archive traversal.
    var hostile = Guid.NewGuid().ToString("N"); var hostileZip = Path.Combine(store.DirectoryPath, SaveBackupService.Key(world), hostile + ".zip");
    using (var zip = ZipFile.Open(hostileZip, ZipArchiveMode.Create))
    { using var first = new StreamWriter(zip.CreateEntry("../escape.dat").Open()); first.Write("x"); }
    var hostileRecord = new SaveBackupRecord(hostile, world, "world", DateTime.UtcNow, 1, 1, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hostileZip))), false);
    File.WriteAllText(Path.ChangeExtension(hostileZip, ".json"), JsonSerializer.Serialize(hostileRecord));
    Rejected(() => store.RestoreCopy(world, hostile, "unsafe-copy"), "Zip traversal was accepted");
    Check(!File.Exists(Path.Combine(saves, "escape.dat")), "Archive escaped restoration directory");

    Check(ModCompatibilityPreviewService.MatchesMinecraft("1.20.1", ">=1.20 <1.21") == true, "AND range matching failed");
    Check(ModCompatibilityPreviewService.MatchesMinecraft("1.21.1", ">=1.20 <1.21") == false, "Incompatible numeric range accepted");
    Check(ModCompatibilityPreviewService.MatchesMinecraft("1.20.1", "1.20.x") == true, "Wildcard range matching failed");
    Check(ModCompatibilityPreviewService.MatchesMinecraft("1.20.1", "~1.20") == null, "Unsupported syntax guessed compatible/incompatible");
    Check(ModCompatibilityPreviewService.MatchesMinecraft("1.20.1", "~1.19 || 1.20.1") == true, "Known OR branch ignored");
    Check(ModCompatibilityPreviewService.MatchesMinecraft("23w31a", ">=1.20") == null, "Snapshot version incorrectly compared numerically");
    var mods = Path.Combine(root, "mods"); Directory.CreateDirectory(mods);
    LocalModInfo Fabric(string name, string metadata, bool enabled = true)
    {
        var path = Path.Combine(mods, name + ".jar");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()); writer.Write(metadata); }
        return new LocalModInfo { FilePath = path, DisplayName = name, IsEnabled = enabled };
    }
    var main = Fabric("main", "{\"id\":\"main\",\"depends\":{\"minecraft\":\">=1.20 <1.21\",\"library\":\">=2\"}}");
    var library = Fabric("library", "{\"id\":\"library\"}", false);
    var report = ModCompatibilityPreviewService.Analyze(new[] { main, library }, "1.21.1", "forge");
    Check(report.EnabledCount == 1 && report.RecognizedCount == 1, "Disabled mod participated in compatibility scan");
    Check(report.Issues.Any(i => i.Kind == "加载器不匹配") && report.Issues.Any(i => i.Kind == "游戏版本不匹配") && report.Issues.Any(i => i.Kind == "缺少声明依赖"), "Compatibility warnings missing");
    library.IsEnabled = true; report = ModCompatibilityPreviewService.Analyze(new[] { main, library }, "1.20.1", "fabric");
    Check(!report.Issues.Any(i => i.Kind == "缺少声明依赖" || i.Kind == "游戏版本不匹配" || i.Kind == "加载器不匹配"), "Installed dependency or compatible version falsely warned");
    Check(report.Declarations.Any(s => s.Contains("library >=2")), "Dependency version declaration lost");
    var forgePath = Path.Combine(mods, "forge.jar");
    using (var zip = ZipFile.Open(forgePath, ZipArchiveMode.Create))
    { using var writer = new StreamWriter(zip.CreateEntry("META-INF/mods.toml").Open()); writer.Write("[[mods]]\nmodId=\"forge_mod\"\n[[dependencies.forge_mod]]\nmodId=\"required_mod\"\nmandatory=true\nversionRange=\"[1,)\"\n"); }
    report = ModCompatibilityPreviewService.Analyze(new[] { new LocalModInfo { FilePath = forgePath, DisplayName = "forge", IsEnabled = true } }, "1.20.1", "forge");
    Check(report.Issues.Any(i => i.Kind == "缺少声明依赖" && i.Detail.Contains("required_mod")), "Forge mandatory dependency ignored");
    var legacyNeoReport = ModCompatibilityPreviewService.Analyze(new[] { new LocalModInfo { FilePath = forgePath, DisplayName = "forge", IsEnabled = true } }, "1.20.1", "neoforge");
    Check(legacyNeoReport.Issues.Any(i => i.Kind == "加载器需要核对"), "Legacy descriptor guessed cross-loader compatibility");
    var explicitForgePath = Path.Combine(mods, "explicit-forge.jar");
    using (var zip = ZipFile.Open(explicitForgePath, ZipArchiveMode.Create))
    { using var writer = new StreamWriter(zip.CreateEntry("META-INF/mods.toml").Open()); writer.Write("[[mods]]\nmodId=\"forge_only\"\n[[dependencies.forge_only]]\nmodId=\"forge\"\nmandatory=true\nversionRange=\"[47,)\"\n"); }
    var explicitReport = ModCompatibilityPreviewService.Analyze(new[] { new LocalModInfo { FilePath = explicitForgePath, DisplayName = "forge-only", IsEnabled = true } }, "1.20.1", "neoforge");
    Check(explicitReport.Issues.Any(i => i.Kind == "加载器不匹配"), "Explicit Forge requirement incorrectly allowed NeoForge");
    Console.WriteLine($"UX save/mod regression: {checks} checks passed");
}
finally { try { Directory.Delete(root, true); } catch { } }
