using XCL2.App.Models;
using XCL2.App.Services;

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}");
}
Equal("fabric", ServerResourceInstallPolicy.GetModLoader(ServerCoreType.Fabric));
Equal("fabric", ServerResourceInstallPolicy.GetModLoader(ServerCoreType.LegacyFabric));
Equal("forge", ServerResourceInstallPolicy.GetModLoader(ServerCoreType.Cleanroom));
Equal("quilt", ServerResourceInstallPolicy.GetModLoader(ServerCoreType.Quilt));
Equal<string?>(null, ServerResourceInstallPolicy.GetModLoader(ServerCoreType.Paper));
Equal<string?>(null, ServerResourceInstallPolicy.GetModLoader(ServerCoreType.Vanilla));
Equal(true, ServerResourceInstallPolicy.MatchesLoader(new[] { "Fabric", "quilt" }, "fabric"));
Equal(false, ServerResourceInstallPolicy.MatchesLoader(new[] { "forge" }, "neoforge"));
var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "xcl-server-resource-regression"));
Equal(Path.Combine(root, "mods"), ServerResourceInstallPolicy.GetDestination(root, ModrinthResourceType.Mod, null));
Equal(Path.Combine(root, "my_world", "datapacks"), ServerResourceInstallPolicy.GetDestination(root, ModrinthResourceType.DataPack, "my_world"));
Equal(Path.Combine(root, "plugins"), ServerResourceInstallPolicy.GetDestination(root, ModrinthResourceType.Plugin, null));
foreach (var name in new[] { "../outside", "..\\outside", ".", "..", "/absolute", "", "  " })
{
    try
    {
        ServerResourceInstallPolicy.GetDestination(root, ModrinthResourceType.DataPack, name);
        throw new Exception("Unsafe world name accepted: " + name);
    }
    catch (ArgumentException) { }
}
Console.WriteLine("Server resource install policy: 18 assertions passed.");
