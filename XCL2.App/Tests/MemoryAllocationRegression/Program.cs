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
// 预览始终在实际启动解析结果上应用相同的“后参数覆盖前参数”顺序。
var baseHeap = new MemoryOptimizerService.LaunchMemory(1024, 4096, false, null);
var basicPreview = MemoryLaunchPreviewPolicy.Apply(baseHeap, Array.Empty<string>());
Check(basicPreview.InitialBytes == 1024L * 1024 * 1024 && basicPreview.MaximumBytes == 4096L * 1024 * 1024 &&
    !basicPreview.HasOverride && basicPreview.Error == null, "preview starts with the launch allocation without changing it");
var customPreview = MemoryLaunchPreviewPolicy.Apply(baseHeap, new[] { "-Xmx6G", "-Xms512M", "-Xmx2G" });
Check(customPreview.InitialBytes == 512L * 1024 * 1024 && customPreview.MaximumBytes == 2048L * 1024 * 1024 &&
    customPreview.HasOverride && customPreview.Error == null, "last custom JVM heap arguments win in the live preview");
var aliases = MemoryLaunchPreviewPolicy.Apply(baseHeap, new[] { "-XX:InitialHeapSize=256M", "-XX:MaxHeapSize=3G" });
Check(aliases.InitialBytes == 256L * 1024 * 1024 && aliases.MaximumBytes == 3072L * 1024 * 1024,
    "HotSpot heap aliases are included instead of showing misleading launcher defaults");
Check(MemoryLaunchPreviewPolicy.Apply(baseHeap, new[] { "-Xmx4096" }).Error != null,
    "a missing heap unit does not misleadingly preview 4096 MB");
Check(MemoryLaunchPreviewPolicy.Apply(baseHeap, new[] { "-Xmx2.5G" }).Error != null &&
    MemoryLaunchPreviewPolicy.Apply(baseHeap, new[] { "-Xmx999999999999999999G" }).Error != null,
    "malformed and overflowing heap overrides produce a preview error");
Check(MemoryLaunchPreviewPolicy.Apply(baseHeap, new[] { "-Xms8G" }).Error != null,
    "preview detects effective initial heap greater than maximum including custom overrides");
Check(MemoryLaunchPreviewPolicy.Apply(baseHeap, new[] { "@jvm.args" }).Uncertainty != null,
    "external argument files explicitly qualify the preview instead of silently claiming exactness");
Check(MemoryLaunchPreviewPolicy.Apply(baseHeap, new[] { "--module-path", "-Xmx8G" }).MaximumBytes == basicPreview.MaximumBytes,
    "a separate option value is not mistaken for a heap flag");
Check(MemoryLaunchPreviewPolicy.Apply(baseHeap, new[] { "-Xms0" }).Error != null,
    "zero initial heap receives the same invalid-memory warning as launch preflight");
var sessionMemory = CommandLineSessionPolicy.ResolveMemory(1024, 4096, null, 512);
var sessionPreview = MemoryLaunchPreviewPolicy.Apply(baseHeap with
    { MinMemoryMb = sessionMemory.MinMb, MaxMemoryMb = sessionMemory.MaxMb }, Array.Empty<string>());
Check(sessionPreview.InitialBytes == 512L * 1024 * 1024 && sessionPreview.MaximumBytes == 512L * 1024 * 1024,
    "session-only memory override uses the launch policy before previewing effective heaps");
Check(CommandLineSessionPolicy.CombineJvmArguments(false, "-Xmx8G", "-Xmx6G", "-Xmx2G") == "-Xmx2G",
    "ordinary mode ignores saved JVM overrides while preserving explicitly supplied session arguments");
Console.WriteLine($"PASS: {checks} memory allocation and preview checks.");
