using XCL2.App.Models;
using XCL2.App.Services;

static void Require(bool result, string description)
{
    if (!result) throw new Exception(description);
}

var cpu = PerformanceMetricsMath.ProcessCpuPercent(0, TimeSpan.TicksPerSecond, 1_000_000, 1_000_000, 8);
Require(cpu == 12.5, "One fully occupied core must be 12.5% on an eight-core machine");
Require(PerformanceMetricsMath.ProcessCpuPercent(0, 8 * TimeSpan.TicksPerSecond, 1, 1, 8) == 100,
    "All occupied cores must be 100%, not 800%");
Require(PerformanceMetricsMath.ProcessCpuPercent(0, 0, 1, 1, 8) == 0, "Actual zero CPU was lost");
Require(PerformanceMetricsMath.ProcessCpuPercent(1, 0, 1, 1, 8) == null,
    "Restarted/wrapped process counter produced CPU data");
Require(PerformanceMetricsMath.ProcessCpuPercent(0, 1, 0, 1, 8) == null,
    "Zero wall interval produced CPU data");
Require(PerformanceMetricsMath.ProcessCpuPercent(0, 1, 1, 0, 8) == null,
    "Invalid frequency produced CPU data");
Require(PerformanceMetricsMath.ProcessCpuPercent(0, 1, 1, 1, 0) == null,
    "Zero logical processors produced CPU data");

Require(PerformanceMetricsMath.SystemCpuPercent(100, 200, 100, 125, 250, 150) == 75,
    "Kernel's included idle time was incorrectly added twice");
Require(PerformanceMetricsMath.SystemCpuPercent(0, 0, 0, 100, 100, 0) == 0,
    "Fully idle system must be zero");
Require(PerformanceMetricsMath.SystemCpuPercent(0, 0, 0, 0, 100, 100) == 100,
    "Fully active system must be 100%");
Require(PerformanceMetricsMath.SystemCpuPercent(1, 2, 3, 0, 5, 6) == null,
    "Reversed system counter produced CPU data");
Require(PerformanceMetricsMath.SystemCpuPercent(0, 0, 0, 200, 100, 0) == null,
    "Idle greater than total produced plausible looking data");
Require(PerformanceMetricsMath.SystemCpuPercent(1, 2, 3, 1, 2, 3) == null,
    "No system counter progress produced CPU data");

var instance = PerformanceMetricsMath.CreateInstanceId("same", @"C:\Games\One\", 10);
Require(instance == PerformanceMetricsMath.CreateInstanceId("same", "c:/games/one", 20),
    "Same normalized instance had different identities across processes");
Require(instance != PerformanceMetricsMath.CreateInstanceId("same", @"C:\Games\Two", 10),
    "Different directories with equal instance names collided");
Require(instance != PerformanceMetricsMath.CreateInstanceId("different", @"C:\Games\One", 10),
    "Different versions in one directory collided");
Require(!instance.Contains("Games", StringComparison.OrdinalIgnoreCase), "Instance key exposed a directory");
Require(PerformanceMetricsMath.CreateInstanceId("external", "", 10) !=
    PerformanceMetricsMath.CreateInstanceId("external", "", 20), "Unknown external instances merged");

PerformanceGameSample Game(int pid, double? usage, long? memory)
    => new(pid, "instance", "Minecraft", "Java", usage, memory,
        usage.HasValue ? null : "waiting", memory.HasValue ? null : "access denied");
var totals = PerformanceMetricsMath.Summarize(new[] { Game(1, 12.5, 100), Game(2, 25, 200) });
Require(totals is { GameCount: 2, CpuPercent: 37.5, WorkingSetBytes: 300 }, "Complete totals incorrect");
var incompleteCpu = PerformanceMetricsMath.Summarize(new[] { Game(1, null, 100), Game(2, 25, 200) });
Require(incompleteCpu.CpuPercent == null && incompleteCpu.CpuUnavailableReason != null &&
    incompleteCpu.WorkingSetBytes == 300, "Missing CPU was silently summed as zero");
var incompleteMemory = PerformanceMetricsMath.Summarize(new[] { Game(1, 12.5, null), Game(2, 25, 200) });
Require(incompleteMemory.WorkingSetBytes == null && incompleteMemory.MemoryUnavailableReason != null &&
    incompleteMemory.CpuPercent == 37.5, "Missing memory was silently summed as zero");
var empty = PerformanceMetricsMath.Summarize(Array.Empty<PerformanceGameSample>());
Require(empty is { GameCount: 0, CpuPercent: 0, WorkingSetBytes: 0 }, "No games did not produce true zero total");
var overflow = PerformanceMetricsMath.Summarize(new[] { Game(1, 0, long.MaxValue), Game(2, 0, 1) });
Require(overflow.WorkingSetBytes == null && overflow.MemoryUnavailableReason != null, "Overflow looked like memory data");

Require(PerformanceMetricsMath.MemoryUsedPercent(4, 16) == 25, "Memory percent must divide by total, not available memory");
Require(PerformanceMetricsMath.MemoryUsedPercent(0, 16) == 0 && PerformanceMetricsMath.MemoryUsedPercent(16, 16) == 100, "Memory boundaries");
Require(PerformanceMetricsMath.MemoryUsedPercent(1, 0) == null && PerformanceMetricsMath.MemoryUsedPercent(null, 16) == null
    && PerformanceMetricsMath.MemoryUsedPercent(17, 16) == null, "Unknown or inconsistent memory must not appear as a real percent");
Require(PerformanceMetricsMath.Summarize(new[] { Game(1, double.NaN, -1) }) is { CpuPercent: null, WorkingSetBytes: null }, "Invalid readings do not create plausible totals");

var sameName = new[] { Game(20, null, null) with { InstanceId = "first" }, Game(10, 25, 200) with { InstanceId = "second" }, Game(30, 10, 100) with { InstanceId = "first" } };
var sample = new PerformanceSnapshot(DateTimeOffset.UtcNow, 8, 50, 1000, 500, 5, 100, sameName, null, null, null, null, null);
var config = new AppConfig { PerformanceOverlayAllInstances = false, PerformanceOverlayInstanceId = "first" };
Require(PerformanceOverlaySettings.SelectGames(sample, config).Select(game => game.ProcessId).SequenceEqual(new[] { 20, 30 }), "Same names in distinct instance directories must not mix");
config.PerformanceOverlayInstanceId = "not-running";
Require(PerformanceOverlaySettings.SelectGames(sample, config).Count == 0, "Exited selection does not fall back to another game");
config.PerformanceOverlayAllInstances = true;
config.PerformanceOverlayGameSort = "Cpu";
Require(PerformanceOverlaySettings.SelectGames(sample, config).Select(game => game.ProcessId).SequenceEqual(new[] { 10, 30, 20 }), "CPU descending; unavailable readings last");
config.PerformanceOverlayGameSort = "Memory";
Require(PerformanceOverlaySettings.SelectGames(sample, config).Select(game => game.ProcessId).SequenceEqual(new[] { 10, 30, 20 }), "Memory descending; unavailable readings last");
config.PerformanceOverlayStyle = "Detailed";
Require(PerformanceOverlaySettings.WindowWidth(config) == 420, "Automatic width follows detailed layout");
config.PerformanceOverlayWindowWidth = 520;
config.PerformanceOverlayStyle = "Compact";
Require(PerformanceOverlaySettings.WindowWidth(config) == 520, "Explicit width survives style change");

Console.WriteLine("Performance math, instance filtering, sort order and layout settings regression checks passed.");
