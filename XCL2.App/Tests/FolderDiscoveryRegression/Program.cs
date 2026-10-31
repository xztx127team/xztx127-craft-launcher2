using XCL2.App.Models;
using XCL2.App.Services;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var root = Path.Combine(Path.GetTempPath(), "xcl-folder-discovery-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string Make(params string[] segments)
    {
        var path = segments.Aggregate(root, Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }
    var first = Make(".minecraft");
    var third = Make("Games", "Minecraft", ".minecraft");
    var fourth = Make("Deep", "Games", "Minecraft", ".minecraft");
    var excluded = Make("Excluded", ".minecraft");
    File.WriteAllText(Path.Combine(root, "Excluded", ".NOXCL"), "");
    var gone = Make("Gone", ".minecraft");
    var lateExcluded = Make("LateExcluded", ".minecraft");
    var candidates = MinecraftFolderScanService.ScanCandidates(new[] { root, Path.Combine(root, "missing") }, null);
    Require(candidates.Contains(first) && candidates.Contains(third), "A valid first/third-level directory was not found.");
    Require(!candidates.Contains(fourth), "Discovery exceeded the advertised three-level boundary.");
    Require(!candidates.Contains(excluded), "An exclusion marker on a parent directory was ignored.");
    var config = new AppConfig();
    // Simulate a manual UI addition after discovery has completed but before its snapshot is committed.
    var manuallyAdded = new GameFolder { Path = first, Name = "Keep my name" };
    config.Folders.Add(manuallyAdded);
    Directory.Delete(gone);
    File.WriteAllText(Path.Combine(lateExcluded, ".NOXCL"), "");
    var added = MinecraftFolderScanService.RegisterCandidates(config, candidates.Concat(new[] { first, third }));
    Require(ReferenceEquals(config.Folders.First(folder => folder.Path == first), manuallyAdded)
        && manuallyAdded.Name == "Keep my name", "Committing discovery replaced a concurrent manual edit.");
    Require(config.Folders.Count(folder => folder.Path == first) == 1
        && config.Folders.Count(folder => folder.Path == third) == 1, "Discovery added duplicate paths.");
    Require(added.Count == 1 && added[0].Path == third, "Reported additions include existing/missing/excluded directories.");
    Require(!Directory.Exists(gone), "Discovery recreated a directory removed before commit.");
    Require(!config.Folders.Any(folder => folder.Path == gone || folder.Path == lateExcluded), "Stale/excluded candidates were registered.");
    Require(MinecraftFolderScanService.RegisterCandidates(config, candidates).Count == 0, "Repeated commit is not idempotent.");
    Console.WriteLine("PASS: bounded discovery, exclusions, missing roots, concurrent additions, deduplication, stale paths, idempotent commit.");
}
finally { Directory.Delete(root, recursive: true); }
