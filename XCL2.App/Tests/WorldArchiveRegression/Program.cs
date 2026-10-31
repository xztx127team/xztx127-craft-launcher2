using System.IO.Compression;
using XCL2.App.Services;

var root = Path.Combine(Path.GetTempPath(), "xcl2-world-archive-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Expect(bool value, string message)
{
    checks++;
    if (!value) throw new InvalidOperationException(message);
}
string Zip(string name, params (string Path, string Text)[] entries)
{
    var path = Path.Combine(root, name);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (relative, text) in entries)
    {
        using var writer = new StreamWriter(archive.CreateEntry(relative).Open());
        writer.Write(text);
    }
    return path;
}
async Task ExpectFailure(string source, string target, string message)
{
    try { await WorldArchiveInstaller.InstallAsync(source, target); }
    catch (Exception e) when (e is IOException or InvalidDataException) { checks++; return; }
    throw new InvalidOperationException(message);
}
try
{
    var saves = Path.Combine(root, "saves");
    var ordinary = Zip("ordinary.zip", ("First/level.dat", "world-one"), ("First/region/r.0.0.mca", "region"));
    await WorldArchiveInstaller.InstallAsync(ordinary, saves);
    Expect(File.ReadAllText(Path.Combine(saves, "First", "level.dat")) == "world-one", "World data preserved.");
    Expect(File.Exists(Path.Combine(saves, "First", "region", "r.0.0.mca")), "Nested content preserved.");

    var overwrite = Zip("overwrite.zip", ("First/level.dat", "destructive"), ("Second/level.dat", "new"));
    await ExpectFailure(overwrite, saves, "Existing world must never be overwritten.");
    Expect(File.ReadAllText(Path.Combine(saves, "First", "level.dat")) == "world-one", "Existing world remains byte intact.");
    Expect(!Directory.Exists(Path.Combine(saves, "Second")), "Preflight conflict causes no partial second world.");

    var flat = Zip("temporary-download.zip", ("level.dat", "flat"), ("region/r.0.0.mca", "flat-region"));
    await WorldArchiveInstaller.InstallAsync(flat, saves, suggestedName: "A new world.zip");
    Expect(File.ReadAllText(Path.Combine(saves, "World-A new world", "level.dat")) == "flat", "Flat archives get a usable world folder.");
    Expect(!File.Exists(Path.Combine(saves, "level.dat")), "No level.dat mixed into saves root.");

    var traversal = Zip("traversal.zip", ("../escaped.txt", "escape"));
    await ExpectFailure(traversal, saves, "Traversal must fail.");
    Expect(!File.Exists(Path.Combine(root, "escaped.txt")), "Traversal cannot escape.");
    var duplicates = Zip("duplicates.zip", ("Duplicate/level.dat", "a"), ("Duplicate/LEVEL.DAT", "b"));
    await ExpectFailure(duplicates, saves, "Case aliases must fail on all build hosts.");
    Expect(!Directory.Exists(Path.Combine(saves, "Duplicate")), "Duplicate validation happens before publication.");
    var alias = Zip("alias.zip", ("World/level.dat", "a"), ("WORLD/region/r.0.0.mca", "b"));
    await ExpectFailure(alias, saves, "Directory prefix case aliases must fail consistently on Windows and Linux.");

    var invalidStructure = Zip("invalid-structure.zip", ("Broken/file", "plain"), ("Broken/file/child", "child"));
    await ExpectFailure(invalidStructure, saves, "File/directory conflicts must fail while staging.");
    Expect(!Directory.Exists(Path.Combine(saves, "Broken")), "A staging error must not publish a partial world.");

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    var cancelledTarget = Path.Combine(root, "cancelled");
    try { await WorldArchiveInstaller.InstallAsync(ordinary, cancelledTarget, cancelled.Token); throw new Exception("Cancellation lost."); }
    catch (OperationCanceledException) { checks++; }
    Expect(!Directory.Exists(cancelledTarget), "Pre-cancellation performs no writes.");

    var raceTarget = Path.Combine(root, "race");
    async Task<bool> Race()
    {
        try { await WorldArchiveInstaller.InstallAsync(ordinary, raceTarget); return true; }
        catch (IOException) { return false; }
    }
    var results = await Task.WhenAll(Race(), Race());
    Expect(results.Count(v => v) == 1, "Exactly one conflicting import wins.");
    Expect(File.ReadAllText(Path.Combine(raceTarget, "First", "level.dat")) == "world-one", "Losing import cannot roll back the winner.");
    Expect(!Directory.EnumerateDirectories(saves, ".xcl2-world-import-*").Any(), "Staging directories are cleaned.");
    Expect(!Directory.EnumerateDirectories(raceTarget, ".xcl2-world-import-*").Any(), "Concurrent staging directories are cleaned.");

    // Emulate an external writer appearing after preflight but before the second root move.
    // Already published worlds must survive: they may already have been opened by the game.
    var stage = Path.Combine(root, "commit-stage");
    var commitTarget = Path.Combine(root, "commit-target");
    Directory.CreateDirectory(Path.Combine(stage, "A"));
    Directory.CreateDirectory(Path.Combine(stage, "B"));
    Directory.CreateDirectory(Path.Combine(commitTarget, "B"));
    File.WriteAllText(Path.Combine(stage, "A", "level.dat"), "published");
    File.WriteAllText(Path.Combine(commitTarget, "B", "level.dat"), "external-world");
    try { WorldArchiveInstaller.PublishStagedRoots(stage, commitTarget, new[] { "A", "B" }); throw new Exception("Expected a conflict."); }
    catch (IOException e) { Expect(e.Message.Contains("已导入并保留"), "Partial completion is explicitly reported."); }
    Expect(File.ReadAllText(Path.Combine(commitTarget, "A", "level.dat")) == "published", "Never roll back a published world.");
    Expect(File.ReadAllText(Path.Combine(commitTarget, "B", "level.dat")) == "external-world", "Concurrent external world remains intact.");
    Console.WriteLine($"PASS: {checks} world archive assertions.");
}
finally { try { Directory.Delete(root, recursive: true); } catch { } }
