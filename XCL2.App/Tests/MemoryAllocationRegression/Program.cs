using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
}
void Heap(MemoryAllocationPolicy.Allocation value, int minimum, int maximum, string description)
    => Check(value.MinimumMb == minimum && value.MaximumMb == maximum, description);
void LaunchHeap(MemoryOptimizerService.LaunchMemory value, int minimum, int maximum, bool dynamic, string description)
    => Check(value.MinMemoryMb == minimum && value.MaxMemoryMb == maximum && value.IsDynamic == dynamic, description);

var range = new MemoryAllocationPolicy.Options();
Heap(MemoryAllocationPolicy.Calculate(32768, range), 500, 8192, "default range on a large PC is 500MB–8GB");
Heap(MemoryAllocationPolicy.Calculate(2048, range), 500, 1433, "small PC adapts the maximum instead of blindly choosing 8GB");
Heap(MemoryAllocationPolicy.Calculate(512, range), 500, 500, "configured range minimum remains valid on a 512MB PC");
Heap(MemoryAllocationPolicy.Calculate(256, range), 256, 256, "extreme low memory never creates an inverted or impossible initial heap");
Heap(MemoryAllocationPolicy.Calculate(16384, range with { MinimumMb = 768, MaximumMb = 6144 }), 768, 6144,
    "custom MB bounds are respected");
var percentages = range with { Mode = MemoryAllocationMode.TotalPercentage };
Heap(MemoryAllocationPolicy.Calculate(16384, percentages), 819, 11468, "default percentage uses 5% and 70% of total RAM");
Heap(MemoryAllocationPolicy.Calculate(4096, percentages with { MinimumPercent = 10, MaximumPercent = 50 }), 409, 2048,
    "custom percentages use total RAM");
Heap(MemoryAllocationPolicy.Calculate(128, percentages), 6, 89,
    "percentage mode does not apply the other mode's 500MB floor");
Heap(MemoryAllocationPolicy.Calculate(8192, percentages with { FixedMb = 2300 }), 2300, 2300,
    "an explicit MB value overrides percentage bounds");
Heap(MemoryAllocationPolicy.Calculate(256, percentages with { FixedMb = 4096 }), 256, 256,
    "explicit values still cannot exceed total physical RAM");
try { MemoryAllocationPolicy.Calculate(0, range); throw new Exception("zero physical RAM accepted"); }
catch (ArgumentOutOfRangeException) { checks++; }
var corrupt = range with { MinimumMb = -5, MaximumMb = -8, MinimumPercent = double.NaN, MaximumPercent = double.PositiveInfinity };
var normalized = MemoryAllocationPolicy.Normalize(corrupt);
Check(normalized.MinimumMb > 0 && normalized.MaximumMb >= normalized.MinimumMb &&
    normalized.MinimumPercent == 5 && normalized.MaximumPercent == 70, "corrupt bounds and non-finite percentages recover safely");
var inverted = MemoryAllocationPolicy.Normalize(percentages with { MinimumPercent = 90, MaximumPercent = 5 });
Check(inverted.MinimumPercent == 90 && inverted.MaximumPercent == 90, "reversed percentage bounds normalize coherently");
Heap(MemoryAllocationPolicy.Calculate(ulong.MaxValue, range), 500, 8192, "very large physical totals cannot overflow heap MB values");

var cfg = new AppConfig { EnableMemoryOptimization = true, MemoryAllocationMode = MemoryAllocationMode.TotalPercentage };
var roomy = new MemoryOptimizerService.PhysicalMemory(16384, 14000);
var busy = new MemoryOptimizerService.PhysicalMemory(16384, 800);
var totalDisplay = MemoryOptimizerService.Calculate(cfg, roomy);
var busyDisplay = MemoryOptimizerService.Calculate(cfg, busy);
Check(totalDisplay.RecommendedMinMemoryMb == busyDisplay.RecommendedMinMemoryMb &&
    totalDisplay.RecommendedMaxMemoryMb == busyDisplay.RecommendedMaxMemoryMb,
    "other apps consuming free RAM cannot change a total-RAM percentage");
cfg.MemoryDisplayBasis = MemoryDisplayBasis.Available;
var availableDisplay = MemoryOptimizerService.Calculate(cfg, busy);
Check(availableDisplay.RecommendedMaxMemoryMb == totalDisplay.RecommendedMaxMemoryMb &&
    availableDisplay.Explanation.StartsWith("当前剩余内存 800MB"), "remaining-RAM display changes only presentation");
var normal = MemoryOptimizerService.ResolveLaunchMemory(cfg, null, roomy);
LaunchHeap(normal, 819, 11468, true, "global percentage policy applies at launch");
LaunchHeap(MemoryOptimizerService.ResolveLaunchMemory(cfg, null, new(8192, 7000)), 409, 5734, true,
    "a later launch resamples total RAM rather than using an earlier cached heap");
Check(cfg.MinMemoryMb == 1024 && cfg.MaxMemoryMb == 4096, "dynamic calculations never overwrite manual configuration");
LaunchHeap(MemoryOptimizerService.ResolveLaunchMemory(cfg, new() { MinMemoryMb = 4096, MaxMemoryMb = 1024 }, roomy), 4096, 1024, false,
    "invalid explicit manual bounds remain visible to the existing JVM diagnostics");
LaunchHeap(MemoryOptimizerService.ResolveLaunchMemory(cfg, new() { MinMemoryMb = 768, MaxMemoryMb = 2500 }, roomy), 768, 2500, false,
    "explicit instance manual bounds outrank global dynamic mode");
LaunchHeap(MemoryOptimizerService.ResolveLaunchMemory(cfg, new() { MaxMemoryMb = 2500 }, roomy), 1024, 2500, false,
    "a partial instance manual override also keeps its precedence");
LaunchHeap(MemoryOptimizerService.ResolveLaunchMemory(cfg, new() { MinMemoryMb = 768, MaxMemoryMb = 2500, EnableMemoryOptimization = true }, roomy),
    819, 11468, true, "an instance explicitly enabling dynamic mode opts into the dynamic policy");
LaunchHeap(MemoryOptimizerService.ResolveLaunchMemory(cfg, new() { EnableMemoryOptimization = false }, roomy), 1024, 4096, false,
    "an instance explicitly disabling dynamic mode uses manual values");
LaunchHeap(MemoryOptimizerService.ResolveLaunchMemory(cfg, null, null), 1024, 4096, false,
    "failed physical memory sampling falls back to manual configuration");
cfg.EnableMemoryOptimization = false;
LaunchHeap(MemoryOptimizerService.ResolveLaunchMemory(cfg, null, roomy), 1024, 4096, false,
    "existing manual configurations remain manual after upgrade");
LaunchHeap(MemoryOptimizerService.ResolveLaunchMemory(cfg, new() { EnableMemoryOptimization = true }, roomy), 819, 11468, true,
    "an instance can opt into dynamic allocation when global mode is manual");
var capped = MemoryOptimizerService.ConstrainForJava(cfg, normal, 32);
LaunchHeap(capped, 819, 1536, true, "generated heap adapts to a detected 32-bit Java runtime");
Check(MemoryOptimizerService.ConstrainForJava(cfg, normal, null) == normal &&
    MemoryOptimizerService.ConstrainForJava(cfg, normal, 64) == normal, "unknown or 64-bit Java never receives a guessed 32-bit cap");
var manual = new MemoryOptimizerService.LaunchMemory(2048, 4096, false, null);
Check(MemoryOptimizerService.ConstrainForJava(cfg, manual, 32) == manual, "explicit manual memory is preserved for diagnostics");
cfg.FixedMemoryAllocationMb = 4096;
Check(MemoryOptimizerService.ConstrainForJava(cfg, normal, 32) == normal, "explicit fixed MB override is preserved for diagnostics");
var highMin = normal with { MinMemoryMb = 3000 };
cfg.FixedMemoryAllocationMb = null;
LaunchHeap(MemoryOptimizerService.ConstrainForJava(cfg, highMin, 32), 1536, 1536, true,
    "32-bit cap also prevents an initial heap larger than the maximum");
cfg.DynamicMemoryMinimumPercent = double.NaN;
cfg.MemoryDisplayBasis = (MemoryDisplayBasis)99;
MemoryOptimizerService.NormalizeConfig(cfg);
Check(cfg.DynamicMemoryMinimumPercent == 5 && cfg.MemoryDisplayBasis == MemoryDisplayBasis.Total &&
    cfg.MinMemoryMb == 1024 && cfg.MaxMemoryMb == 4096 && !cfg.EnableMemoryOptimization,
    "configuration normalization repairs dynamic fields without enabling or altering manual memory");
Console.WriteLine($"PASS: {checks} memory allocation checks.");
