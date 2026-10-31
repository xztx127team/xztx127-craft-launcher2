using XCL2.App.Services;

var checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
void Rejected(Action action, string message)
{
    try { action(); } catch (IOException) { checks++; return; }
    throw new Exception(message);
}
var root = Path.Combine(Path.GetTempPath(), "xcl2-recovery-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var mods = Path.Combine(root, "mods"); Directory.CreateDirectory(mods);
    var old = Path.Combine(mods, "old.jar"); var newer = Path.Combine(mods, "new.jar");
    File.WriteAllText(old, "original bytes");
    var store = new ModUpdateSnapshotStore(Path.Combine(root, "snapshots"));
    var snapshot = store.Create(mods, new[] { "old.jar", "new.jar" }, 10, 1024 * 1024, default);
    Check(File.ReadAllText(old) == "original bytes" && !File.Exists(newer), "Taking snapshot modified live files");
    File.WriteAllText(newer, "verified update"); File.Delete(old);
    store.RecordAfter(snapshot, "new.jar", ModUpdateSnapshotStore.Hash(newer));
    store.RecordAfter(snapshot, "old.jar", null); store.Complete(snapshot);
    store.Restore(snapshot.Id);
    Check(File.ReadAllText(old) == "original bytes" && !File.Exists(newer), "Rename update rollback lost original or left new mod installed");
    Check(store.List().Single().State == "Restored", "Restored state not durable");
    Rejected(() => store.Restore(snapshot.Id), "Completed rollback was repeated");

    snapshot = store.Create(mods, new[] { "old.jar", "new.jar" }, 10, 1024 * 1024, default);
    File.WriteAllText(old, "updated"); store.RecordAfter(snapshot, "old.jar", ModUpdateSnapshotStore.Hash(old));
    store.Complete(snapshot); File.WriteAllText(old, "user's later edit");
    Rejected(() => store.Restore(snapshot.Id), "Rollback overwrote a subsequent user edit");
    Check(File.ReadAllText(old) == "user's later edit", "Conflict check still changed data");

    snapshot = store.Create(mods, new[] { "old.jar" }, 10, 1024 * 1024, default);
    File.WriteAllText(old, "next update"); store.RecordAfter(snapshot, "old.jar", ModUpdateSnapshotStore.Hash(old)); store.Complete(snapshot);
    File.WriteAllText(Path.Combine(root, "snapshots", snapshot.Id, "0.bin"), "corrupt backup");
    Rejected(() => store.Restore(snapshot.Id), "Corrupt snapshot was accepted");
    Check(File.ReadAllText(old) == "next update", "Backup verification was too late");
    foreach (var invalid in new[] { "../outside.jar", "a/b.jar", "a\\b.jar", "a:stream", "..", "trailing." })
        Rejected(() => ModUpdateSnapshotStore.ResolveFile(mods, invalid), "Invalid resource path accepted: " + invalid);

    var pendingStore = new ModUpdateSnapshotStore(Path.Combine(root, "pending-snapshots"));
    pendingStore.Create(mods, new[] { "old.jar" }, 1, 1024 * 1024, default);
    Rejected(() => pendingStore.Create(mods, new[] { "old.jar" }, 1, 1024 * 1024, default), "Unfinished snapshot was evicted");
    Rejected(() => store.Create(mods, new[] { "old.jar" }, 10, 1, default), "Snapshot space limit ignored");

    var request = new DownloadRecoveryRequest(mods, "1.0", "https://launchermeta.mojang.com/test.json");
    Check(request.IsAllowed(new[] { mods }), "Valid configured public manifest rejected");
    Check(!request.IsAllowed(new[] { root }), "Recovered folder escaped configured exact folder");
    var missingFolder = Path.Combine(root, "disconnected-drive");
    Check((request with { Folder = missingFolder }).IsAllowed(new[] { missingFolder }, requireDirectory: false)
        && !(request with { Folder = missingFolder }).IsAllowed(new[] { missingFolder }),
        "Temporarily unavailable configured drive lost its paused checkpoint or was allowed to execute");
    foreach (var url in new[] { "http://example.org/v.json", "https://user:secret@example.org/v.json", "https://example.org/v.json?token=secret", "https://example.org/v.json#secret" })
        Check(!(request with { VersionUrl = url }).IsAllowed(new[] { mods }), "Private or unsafe URL persisted");
    Check(!(request with { VersionId = "../other" }).IsAllowed(new[] { mods }), "Recovered version escaped its directory");

    var recoveryDir = Path.Combine(root, "downloads");
    using var oldSession = new DownloadRecoveryStore(recoveryDir);
    var record = new DownloadRecoveryRecord(Guid.NewGuid(), "install 1.0", request);
    oldSession.Save(new[] { record });
    using var nextSession = new DownloadRecoveryStore(recoveryDir);
    var imported = 0;
    nextSession.ImportPending(items => { imported += items.Count; nextSession.Save(items); });
    Check(imported == 0, "Active launcher session's tasks were stolen");
    oldSession.Dispose();
    nextSession.ImportPending(items => { imported += items.Count; nextSession.Save(items); });
    Check(imported == 1, "Interrupted session was not recovered");
    nextSession.ImportPending(items => { imported += items.Count; nextSession.Save(items); });
    Check(imported == 1, "Imported record was recovered twice");

    using var failedSession = new DownloadRecoveryStore(recoveryDir);
    failedSession.Save(new[] { record with { Id = Guid.NewGuid() } }); failedSession.Dispose();
    nextSession.ImportPending(_ => throw new IOException("simulated checkpoint save failure"));
    var retry = 0;
    nextSession.ImportPending(items => { retry += items.Count; nextSession.Save(items); });
    Check(retry == 1, "Failed transfer removed the old recovery record");
    Console.WriteLine($"PASS {checks} snapshot integrity, conflict, path, space and session recovery checks");
}
finally { Directory.Delete(root, true); }
