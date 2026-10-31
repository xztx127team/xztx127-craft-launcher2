using System.Buffers.Binary;
using System.IO.Compression;
using XCL2.App.Services;

var checks = 0;
var skippedLinkFixtures = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
async Task Rejected(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { checks++; return; }
    throw new Exception(message);
}
void Zip(string path, params (string Name, string Text, bool Link)[] files)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var file in files)
    {
        var entry = archive.CreateEntry(file.Name);
        if (file.Link) entry.ExternalAttributes = 0xA000 << 16;
        using var writer = new StreamWriter(entry.Open()); writer.Write(file.Text);
    }
}
bool TryCreateLink(string path, string target, bool directory)
{
    try
    {
        if (directory) Directory.CreateSymbolicLink(path, target);
        else File.CreateSymbolicLink(path, target);
        return true;
    }
    catch (Exception ex) when (ex is PlatformNotSupportedException || OperatingSystem.IsWindows() &&
        (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xffff) is 5 or 50 or 1314))
    {
        // Windows may require Developer Mode or the create-symbolic-link privilege.
        // These fixtures are counted as skipped, never as a passing security check.
        skippedLinkFixtures++;
        return false;
    }
}

var root = Path.Combine(Path.GetTempPath(), "xcl2-backup-security-" + Guid.NewGuid().ToString("N"));
var game = Path.Combine(root, "game");
var instance = Path.Combine(game, "versions", "survival");
var outside = Path.Combine(root, "private");
Directory.CreateDirectory(Path.Combine(instance, "empty"));
Directory.CreateDirectory(outside);
var existing = Path.Combine(instance, "survival.json");
var secret = Path.Combine(outside, "private-data.txt");
File.WriteAllText(existing, "original instance");
File.WriteAllText(secret, "private data must stay outside backups");
try
{
    foreach (var id in new[] { "../private", "a/b", "a\\b", "..", ".", "CON", "aux.txt", "C:escape", "survival ", ".instance_backups", "survival*" })
    {
        await Rejected(() => InstanceBackupService.CreateBackupAsync(game, id, "test"), "Invalid instance identifier accepted: " + id);
        await Rejected(() => Task.Run(() => InstanceBackupService.ListBackups(game, id)), "Invalid backup-list identifier accepted: " + id);
    }

    // The normal public API still preserves files and empty directories.
    var backup = await InstanceBackupService.CreateBackupAsync(game, "survival", "manual/\\reason");
    var second = await InstanceBackupService.CreateBackupAsync(game, "survival", "manual/\\reason");
    Check(backup != second && File.Exists(backup) && File.Exists(second), "Successive backups overwrote one another.");
    Check(InstanceBackupService.ListBackups(game, "survival").Count == 2, "Completed backups were not listed.");
    using (var archive = ZipFile.OpenRead(backup))
    {
        Check(archive.Entries.Any(e => e.FullName == "empty/"), "Empty directory was lost.");
        using var reader = new StreamReader(archive.GetEntry("survival.json")!.Open());
        Check(reader.ReadToEnd() == "original instance", "Backup file content changed.");
    }

    // File, directory and dangling links are rejected before any ZIP can be published.
    var link = Path.Combine(instance, "leak");
    for (var mode = 0; mode < 3; mode++)
    {
        if (!TryCreateLink(link, mode == 1 ? outside : mode == 0 ? secret : Path.Combine(outside, "missing"), mode == 1)) continue;
        try
        {
            await Rejected(() => InstanceBackupService.CreateBackupAsync(game, "survival", "links"), "Backup traversed a filesystem link.");
            Check(InstanceBackupService.ListBackups(game, "survival").Count == 2, "Rejected backup left a publishable ZIP.");
        }
        finally { if (mode == 1) Directory.Delete(link); else File.Delete(link); }
    }
    Check(File.ReadAllText(secret) == "private data must stay outside backups", "Backup modified linked data.");

    var bad = Path.Combine(root, "bad.zip");
    File.WriteAllText(bad, "not a zip");
    await Rejected(() => InstanceBackupService.RestoreBackupAsync(game, "survival", bad), "Invalid ZIP was accepted.");
    Check(File.ReadAllText(existing) == "original instance", "Corrupt restore deleted the existing instance.");

    // A ZIP can have valid structure while its central directory lies about the
    // uncompressed length. Reading to EOF alone must not publish this restore.
    var wrongLength = Path.Combine(root, "wrong-length.zip");
    Zip(wrongLength, ("survival.json", "incomplete replacement", false));
    var zipBytes = File.ReadAllBytes(wrongLength);
    var centralDirectory = -1;
    for (var i = 0; i <= zipBytes.Length - 28; i++)
        if (BinaryPrimitives.ReadUInt32LittleEndian(zipBytes.AsSpan(i, 4)) == 0x02014b50)
        { centralDirectory = i; break; }
    Check(centralDirectory >= 0, "Wrong-length fixture is missing its central directory.");
    var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(zipBytes.AsSpan(centralDirectory + 24, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(zipBytes.AsSpan(centralDirectory + 24, 4), uncompressedSize + 1);
    File.WriteAllBytes(wrongLength, zipBytes);
    await Rejected(() => InstanceBackupService.RestoreBackupAsync(game, "survival", wrongLength), "ZIP with incorrect uncompressed size was published.");
    Check(File.ReadAllText(existing) == "original instance", "Truncated ZIP restore replaced the current instance.");

    foreach (var (name, entries) in new[]
    {
        ("traversal", new[] { ("survival.json", "replacement", false), ("../escape.txt", "escape", false) }),
        ("link", new[] { ("survival.json", "replacement", false), ("linked", outside, true) }),
        ("duplicate", new[] { ("survival.json", "first", false), ("SURVIVAL.JSON", "second", false) }),
        ("file-directory-conflict", new[] { ("config", "file", false), ("config/settings.txt", "nested", false) })
    })
    {
        var archivePath = Path.Combine(root, name + ".zip"); Zip(archivePath, entries);
        await Rejected(() => InstanceBackupService.RestoreBackupAsync(game, "survival", archivePath), "Unsafe ZIP was accepted: " + name);
        Check(File.ReadAllText(existing) == "original instance", "Failed restore changed the current instance: " + name);
        Check(!File.Exists(Path.Combine(game, "versions", "escape.txt")), "ZIP traversal escaped the staging directory.");
    }

    using (var cancellation = new CancellationTokenSource())
    {
        cancellation.Cancel();
        try { await InstanceBackupService.RestoreBackupAsync(game, "survival", backup, cancellation.Token); throw new Exception("Canceled restore succeeded."); }
        catch (OperationCanceledException) { checks++; }
        Check(File.ReadAllText(existing) == "original instance", "Cancellation removed the old instance.");
    }

    File.WriteAllText(existing, "later instance"); File.WriteAllText(Path.Combine(instance, "later-only.txt"), "later");
    await InstanceBackupService.RestoreBackupAsync(game, "survival", backup);
    Check(File.ReadAllText(existing) == "original instance" && !File.Exists(Path.Combine(instance, "later-only.txt")), "Valid restore did not replace the instance.");
    Check(Directory.Exists(Path.Combine(instance, "empty")), "Valid restore lost an empty directory.");
    Check(!Directory.EnumerateDirectories(Path.Combine(game, "versions"), ".xcl2-restore-*").Any(), "Temporary restore data survived completion.");
    Check(!Directory.EnumerateFiles(Path.GetDirectoryName(backup)!, ".xcl2-export-*").Any(), "Failed backup left temporary exports.");

    // A symlink selected as the current instance must not be replaced or traversed.
    var linkedInstance = Path.Combine(game, "versions", "linked-instance");
    if (TryCreateLink(linkedInstance, outside, directory: true))
    {
        try
        {
            await Rejected(() => InstanceBackupService.RestoreBackupAsync(game, "linked-instance", backup), "Restore followed a linked instance.");
            Check(File.ReadAllText(secret) == "private data must stay outside backups", "Restore modified a link target.");
        }
        finally { Directory.Delete(linkedInstance); }
    }

    Console.WriteLine($"Instance backup security regression: {checks} checks passed; {skippedLinkFixtures} symbolic-link fixtures skipped (unsupported or permission denied)");
}
finally { try { Directory.Delete(root, recursive: true); } catch { } }
