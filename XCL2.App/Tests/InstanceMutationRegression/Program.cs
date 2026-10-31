using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
void Reject(Action action, string message)
{
    try { action(); }
    catch (InvalidDataException) { checks++; return; }
    throw new Exception(message);
}
var root = Path.Combine(Path.GetTempPath(), "xcl2-instance-mutation-" + Guid.NewGuid().ToString("N"));
var folder = Path.Combine(root, "minecraft");
var versions = Path.Combine(folder, "versions");
Directory.CreateDirectory(Path.Combine(versions, "old"));
var sentinel = Path.Combine(folder, "save-sentinel.txt");
File.WriteAllText(sentinel, "must survive");
try
{
    var cfg = new AppConfig { SelectedFolderPath = folder, SelectedVersionId = "old" };
    foreach (var invalid in new[] { "", ".", "..", "../outside", "..\\outside", "a/b", "a\\b", "C:\\outside", "a:stream", "CON", "nul.txt", "trailing.", "trailing ", "a*", "a?", "a\u0001" })
    {
        Reject(() => InstanceDeletionService.Rename(cfg, folder, "old", invalid), "Unsafe rename accepted: " + invalid);
        Reject(() => InstanceDeletionService.DeletePermanently(cfg, folder, invalid), "Unsafe delete accepted: " + invalid);
        Reject(() => InstanceDeletionService.DeleteToRecycleBin(cfg, folder, invalid), "Unsafe recycle accepted: " + invalid);
    }
    Reject(() => InstanceDeletionService.DeletePermanently(cfg, "relative", "old"), "Relative root accepted");
    Check(File.ReadAllText(sentinel) == "must survive" && Directory.Exists(Path.Combine(versions, "old")), "Rejected operations changed source files");

    cfg.VersionJavaIdOverrides["old"] = "java-17";
    cfg.VersionSkipJavaMismatchPrompt.Add("old");
    var oldKey = InstanceDeletionService.BuildKey(folder, "old");
    var otherKey = InstanceDeletionService.BuildKey(Path.Combine(root, "other"), "old");
    cfg.InstancePersonalizations[oldKey] = new InstancePersonalization { Notes = "my notes" };
    cfg.InstancePersonalizations[otherKey] = new InstancePersonalization { Notes = "other folder" };
    cfg.FavoriteInstanceKeys.AddRange(new[] { oldKey, otherKey });
    cfg.RecentInstances.Add(new RecentInstanceEntry { FolderPath = folder, VersionId = "old" });
    cfg.RecentInstances.Add(new RecentInstanceEntry { FolderPath = Path.Combine(root, "other"), VersionId = "old" });
    InstanceDeletionService.HideFromList(cfg, folder, "old");
    InstanceDeletionService.Rename(cfg, folder, "old", "renamed");
    var renamedKey = InstanceDeletionService.BuildKey(folder, "renamed");
    Check(Directory.Exists(Path.Combine(versions, "renamed")) && !Directory.Exists(Path.Combine(versions, "old")), "Rename failed");
    Check(cfg.VersionJavaIdOverrides["renamed"] == "java-17" && !cfg.VersionJavaIdOverrides.ContainsKey("old"), "Java setting did not follow rename");
    Check(cfg.VersionSkipJavaMismatchPrompt.Contains("renamed") && !cfg.VersionSkipJavaMismatchPrompt.Contains("old"), "Mismatch preference did not follow rename");
    Check(cfg.InstancePersonalizations[renamedKey].Notes == "my notes" && cfg.InstancePersonalizations[otherKey].Notes == "other folder", "Scoped rename damaged another folder");
    Check(cfg.FavoriteInstanceKeys.Contains(renamedKey) && cfg.FavoriteInstanceKeys.Contains(otherKey), "Scoped favorite did not follow rename");
    Check(cfg.RecentInstances[0].VersionId == "renamed" && cfg.RecentInstances[1].VersionId == "old", "Recent list renamed another folder");
    Check(cfg.SelectedVersionId == "renamed" && InstanceDeletionService.IsHidden(cfg, folder, "renamed"), "Selection/hidden setting did not follow rename");

    InstanceDeletionService.DeletePermanently(cfg, folder, "renamed");
    Check(!Directory.Exists(Path.Combine(versions, "renamed")), "Delete failed");
    Check(!cfg.VersionJavaIdOverrides.ContainsKey("renamed") && !cfg.VersionSkipJavaMismatchPrompt.Contains("renamed"), "Deleted instance left Java preferences");
    Check(!cfg.InstancePersonalizations.ContainsKey(renamedKey) && cfg.InstancePersonalizations.ContainsKey(otherKey), "Delete cleared another folder metadata");
    Check(!cfg.FavoriteInstanceKeys.Contains(renamedKey) && cfg.FavoriteInstanceKeys.Contains(otherKey), "Delete cleared another folder favorite");
    Check(cfg.RecentInstances.Count == 1 && cfg.RecentInstances[0].VersionId == "old", "Delete left stale history or removed another folder history");
    Check(cfg.SelectedVersionId == null && !InstanceDeletionService.IsHidden(cfg, folder, "renamed"), "Deleted selection/hidden setting survived");
    Check(File.ReadAllText(sentinel) == "must survive", "Instance delete touched root save");

    var asyncDir = Path.Combine(versions, "async-delete");
    Directory.CreateDirectory(asyncDir);
    for (var i = 0; i < 300; i++) File.WriteAllText(Path.Combine(asyncDir, i + ".jar"), "fixture");
    cfg.SelectedVersionId = "async-delete";
    await InstanceDeletionService.DeletePermanentlyAsync(cfg, folder, "async-delete");
    Check(!Directory.Exists(asyncDir) && cfg.SelectedVersionId == null, "Background deletion did not finish filesystem/config work");
    try { await InstanceDeletionService.DeletePermanentlyAsync(cfg, folder, "../outside"); throw new Exception("Unsafe async delete accepted"); }
    catch (InvalidDataException) { checks++; }

    var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
    File.WriteAllText(Path.Combine(outside, "important.txt"), "untouched");
    var link = Path.Combine(versions, "linked");
    try
    {
        Directory.CreateSymbolicLink(link, outside);
        Reject(() => InstanceDeletionService.DeletePermanently(cfg, folder, "linked"), "Linked instance deletion accepted");
        Reject(() => InstanceDeletionService.Rename(cfg, folder, "linked", "new-link"), "Linked instance rename accepted");
        Check(File.ReadAllText(Path.Combine(outside, "important.txt")) == "untouched", "Link target changed");
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
    { Console.WriteLine("SKIP symlink creation: " + ex.GetType().Name); }
    finally { if (Directory.Exists(link)) Directory.Delete(link); }
    var cache = Path.Combine(folder, "webcache"); Directory.CreateDirectory(cache);
    var cached = Path.Combine(cache, "obsolete.bin"); File.WriteAllText(cached, "cache");
    var options = Path.Combine(folder, "options.txt"); File.WriteAllText(options, "game settings");
    var save = Path.Combine(folder, "saves", "world"); Directory.CreateDirectory(save);
    File.WriteAllText(Path.Combine(save, "level.dat"), "precious world");
    var logs = Path.Combine(folder, "logs"); Directory.CreateDirectory(logs);
    var oldLog = Path.Combine(logs, "old.log.gz"); File.WriteAllText(oldLog, "old log");
    File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-10));
    var recentLog = Path.Combine(logs, "recent.log.gz"); File.WriteAllText(recentLog, "recent log");
    var cacheLink = Path.Combine(cache, "linked-world");
    var linkedCache = false;
    try { Directory.CreateSymbolicLink(cacheLink, save); linkedCache = true; }
    catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
    { Console.WriteLine("SKIP cache symlink creation: " + ex.GetType().Name); }
    try
    {
        var scan = JunkCleanupService.Scan(folder);
        Check(scan.Items.Any(i => i.Path == cached) && scan.Items.Any(i => i.Path == oldLog), "Safe cache/log entries were omitted");
        Check(!scan.Items.Any(i => i.Path == options || i.Path == recentLog || i.Path.Contains("linked-world")), "Scan included settings, recent logs, or linked saves");
        File.WriteAllText(cached, "newly replaced valuable cache content");
        var deleted = JunkCleanupService.Delete(scan.Items);
        Check(deleted.deletedCount == 1 && !File.Exists(oldLog), "Unchanged old log was not removed");
        Check(File.Exists(cached) && File.ReadAllText(options) == "game settings" && File.ReadAllText(Path.Combine(save, "level.dat")) == "precious world",
            "Cleanup removed modified cache, options, or linked save");
        var forged = new JunkCleanupService.JunkItem(options, new FileInfo(options).Length, "cache")
        { ScanRoot = folder, ScannedLastWriteUtc = File.GetLastWriteTimeUtc(options) };
        Check(JunkCleanupService.Delete(new() { forged }).deletedCount == 0 && File.Exists(options), "Cleanup trusted an arbitrary file outside its allowlist");
        var unscanned = new JunkCleanupService.JunkItem(cached, new FileInfo(cached).Length, "cache");
        Check(JunkCleanupService.Delete(new() { unscanned }).deletedCount == 0, "Cleanup accepted an item without scan provenance");
    }
    finally { if (linkedCache && Directory.Exists(cacheLink)) Directory.Delete(cacheLink); }
    // A directory switched to a junction after scanning must be rejected again at delete time.
    var swap = Path.Combine(cache, "swap"); Directory.CreateDirectory(swap);
    var swapFile = Path.Combine(swap, "important.txt"); File.WriteAllText(swapFile, "before");
    var swapScan = JunkCleanupService.Scan(folder);
    var swapEntry = swapScan.Items.Single(i => i.Path == swapFile);
    File.WriteAllText(Path.Combine(outside, "important.txt"), "before");
    File.SetLastWriteTimeUtc(Path.Combine(outside, "important.txt"), swapEntry.ScannedLastWriteUtc);
    Directory.Delete(swap, true);
    try
    {
        Directory.CreateSymbolicLink(swap, outside);
        JunkCleanupService.Delete(swapScan.Items);
        Check(File.ReadAllText(Path.Combine(outside, "important.txt")) == "before", "Cleanup followed a directory swapped after scanning");
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
    { Console.WriteLine("SKIP swapped symlink: " + ex.GetType().Name); }
    finally { if (Directory.Exists(swap)) Directory.Delete(swap); }
    Console.WriteLine($"PASS {checks} instance mutation, configuration, and junk-cleanup safety checks");
}
finally { Directory.Delete(root, recursive: true); }
