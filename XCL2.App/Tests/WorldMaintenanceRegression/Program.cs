using System.Buffers.Binary;
using XCL2.App.Services.Worlds;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
void Reject<T>(Action action, string message) where T : Exception
{ try { action(); } catch (T) { checks++; return; } throw new InvalidOperationException(message); }
NbtTag Compound(params (string Name, NbtTag Tag)[] fields) => new(10, fields.ToDictionary(p => p.Name, p => p.Tag));
void CreateLevel(string path, string name) => new NbtDocument("", Compound(("Data", Compound(
    ("LevelName", new(8, name)), ("GameType", new(3, 0)), ("Difficulty", new(1, (sbyte)2)),
    ("mod:opaque", new(12, new[] { long.MaxValue })))))).Save(path);
var root = Path.Combine(Path.GetTempPath(), "xcl2-world-maintenance-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    var server = Path.Combine(root, "server"); var world = Path.Combine(server, "custom-world"); Directory.CreateDirectory(world);
    var level = Path.Combine(world, "level.dat"); var old = Path.Combine(world, "level.dat_old");
    CreateLevel(level, "new"); CreateLevel(old, "old");
    var discovered = WorldArchiveService.DiscoverServerWorlds(new[] { server });
    Check(discovered.Count == 1 && discovered[0].Source == "服务器", "Registered server worlds are discovered with server source");
    var playerFolder = Path.Combine(world, "playerdata"); Directory.CreateDirectory(playerFolder); var player = Path.Combine(playerFolder, "user.dat"); File.WriteAllText(player, "do not modify player data");
    var regionFolder = Path.Combine(world, "region"); Directory.CreateDirectory(regionFolder); var region = Path.Combine(regionFolder, "r.0.0.mca");
    var bytes = new byte[12288]; BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), (2u << 8) | 1u); File.WriteAllBytes(region, bytes);
    var report = WorldArchiveService.Inspect(world);
    Check(report.MetadataReadable && report.RegionFilesChecked == 1 && report.Candidates.Count == 1, "Inspect reads Java metadata and finds level.dat_old");
    Check(!report.Findings.Any(f => f.Contains("区域需人工检查")), "Well-bounded region location is not reported as corruption");
    File.WriteAllText(level, "damaged bytes"); var broken = File.ReadAllBytes(level);
    report = WorldArchiveService.Inspect(world);
    Check(!report.MetadataReadable, "Corrupted level.dat can still be inspected and repaired");
    var candidate = report.Candidates.Single(); var originalCandidateBytes = File.ReadAllBytes(old);
    var backup = WorldArchiveService.RestoreMetadata(world, candidate);
    Check(File.ReadAllBytes(backup).SequenceEqual(broken), "Recovery retains exact damaged metadata bytes first");
    Check(WorldArchiveService.Read(world).Settings.Name == "old", "Recovery installs valid old metadata");
    Check(File.ReadAllBytes(old).SequenceEqual(originalCandidateBytes), "Recovery never replaces level.dat_old");
    Check(File.ReadAllBytes(region).SequenceEqual(bytes) && File.ReadAllText(player) == "do not modify player data", "Recovery preserves region and server player data");
    Check(((long[])NbtDocument.Load(level).Root.Get("Data")!.Get("mod:opaque")!.Value)[0] == long.MaxValue, "Recovery preserves opaque mod metadata");
    using (var held = new FileStream(Path.Combine(world, "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Reject<IOException>(() => WorldArchiveService.RestoreMetadata(world, candidate), "Running world must not be repaired");
    var beforeReject = File.ReadAllBytes(level);
    File.Delete(old); CreateLevel(old, "changed since inspection");
    Reject<IOException>(() => WorldArchiveService.RestoreMetadata(world, candidate), "Changed repair candidate must require rescan");
    Check(File.ReadAllBytes(level).SequenceEqual(beforeReject), "Rejected repair leaves current metadata intact");
    var outsider = Path.Combine(root, "outside.dat"); CreateLevel(outsider, "outside");
    Reject<IOException>(() => WorldArchiveService.RestoreMetadata(world, candidate with { Path = outsider }), "Repair cannot consume unrelated world files");
    bytes[0] = 0x7f; File.WriteAllBytes(region, bytes);
    report = WorldArchiveService.Inspect(world);
    Check(report.Findings.Any(f => f.Contains("区域需人工检查")), "Out-of-range region sectors are reported");
    Check(File.ReadAllBytes(region).SequenceEqual(bytes), "Region inspection never attempts destructive chunk repair");
    File.Delete(level); report = WorldArchiveService.Inspect(world); backup = WorldArchiveService.RestoreMetadata(world, report.Candidates.Single());
    Check(File.Exists(backup) && WorldArchiveService.Read(world).Settings.Name == "changed since inspection", "Missing metadata is recoverable with a recorded pre-repair missing state");
    var cancel = new CancellationTokenSource(); cancel.Cancel();
    Reject<OperationCanceledException>(() => WorldArchiveService.Inspect(world, cancel.Token), "Inspection honors cancellation");
    if (!OperatingSystem.IsWindows())
    {
        var linked = Path.Combine(root, "linked-world"); Directory.CreateSymbolicLink(linked, world);
        Reject<IOException>(() => WorldArchiveService.Inspect(linked), "Linked world paths are rejected"); Directory.Delete(linked);
    }
    Console.WriteLine($"WorldMaintenanceRegression: {checks} checks passed.");
}
finally { Directory.Delete(root, true); }
