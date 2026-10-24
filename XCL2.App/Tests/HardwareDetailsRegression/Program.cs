using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
var cfg = new AppConfig();
var hardware = new PerformanceHardwareSnapshot(Array.Empty<PerformanceDeviceSample>(), null, null, null, null, null, null, null)
{
    Metadata = new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        ["Memory"] = new Dictionary<string, string> { ["Manufacturer"] = "Detected vendor", ["StandardRate"] = "6000 MT/s", ["Speed"] = "5600 MT/s", ["MemoryCapacity"] = "32 GiB" },
        ["Network"] = new Dictionary<string, string> { ["Speed"] = "Ethernet 2.5 Gbit/s" }
    }
};
Check(PerformanceHardwareDetails.Format(hardware, cfg) == "", "New metadata must not crowd the existing overlay without opt-in.");
cfg.PerformanceOverlayHardwareDetails["Memory"] = new() { Show = true, ShowStandardRate = true, ShowMemoryChannels = true, ShowMemoryCapacity = true };
var automatic = PerformanceHardwareDetails.Format(hardware, cfg);
Check(automatic.Contains("6000 MT/s") && automatic.Contains("5600 MT/s"), "Rated and configured speed remain separate.");
Check(automatic.Contains("内存通道 暂不可用"), "Capacity or DIMM count does not imply channels.");
Check(automatic.Contains("已安装容量 32 GiB"), "Installed capacity has an unambiguous label.");
var draft = PerformanceHardwareDetails.Clone(cfg);
draft["Memory"].Manufacturer = "My DIMM vendor";
draft["Memory"].MemoryChannels = "双通道";
Check(cfg.PerformanceOverlayHardwareDetails["Memory"].Manufacturer == "", "Cancelled draft cannot mutate config.");
cfg.PerformanceOverlayHardwareDetails = draft;
var manual = PerformanceHardwareDetails.Format(hardware, cfg);
Check(manual.Contains("My DIMM vendor（手动）") && manual.Contains("双通道（手动）"), "Overrides remain visibly manual.");
Check(hardware.Metadata["Memory"]["Manufacturer"] == "Detected vendor", "Overrides never alter measured data.");
draft["Memory"].ShowManufacturer = false;
Check(!PerformanceHardwareDetails.Format(hardware, cfg).Contains("My DIMM vendor"), "Individual hidden fields stay hidden.");
draft["Memory"].Manufacturer = "";
draft["Memory"].ShowManufacturer = true;
Check(PerformanceHardwareDetails.Format(hardware, cfg).Contains("Detected vendor"), "Clearing override restores automatic metadata.");
cfg.PerformanceOverlayHardwareDetails["Network"] = new() { Show = true, ShowManufacturer = false };
Check(PerformanceHardwareDetails.Format(hardware, cfg).Contains("链路速度 Ethernet 2.5 Gbit/s"), "Network speed remains bits/sec link speed, not byte throughput.");
Check(PerformanceHardwareDetails.Clean(new string('x', 200)).Length == 120, "Imported overrides are bounded too.");
Check(!PerformanceHardwareDetails.Clean("one\n\rtwo\t").Any(char.IsControl), "Overrides cannot inject multiline/status content.");
var restored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg))!;
Check(PerformanceHardwareDetails.Format(hardware, restored) == PerformanceHardwareDetails.Format(hardware, cfg), "Display flags and manual data survive serialization.");
var legacy = JsonSerializer.Deserialize<AppConfig>("{}")!;
Check(PerformanceHardwareDetails.Format(null, legacy) == "", "Older configurations keep existing display.");
var invalid = JsonSerializer.Deserialize<AppConfig>("{\"PerformanceOverlayHardwareDetails\":null}")!;
Check(PerformanceHardwareDetails.Clone(invalid).Count == 6, "Null imported dictionary is normalized safely.");
// Template suggestions are explicit, preserve user text and treat escaped literals as literals.
var originalTemplate = "我的面板  \r\nCPU {system_cpu_value}\r\n{ gpu_details }";
var missing = PerformanceHardwareDetails.MissingTemplateFields(originalTemplate, new[] { "GPU", "Memory", "Memory" });
Check(missing.SequenceEqual(new[] { "memory_details" }), "Existing fields and repeated selections are not duplicated.");
Check(PerformanceHardwareDetails.TryAppendTemplateFields(originalTemplate, new[] { "GPU", "Memory" }, 4096,
    out var suggested, out var added, out var suggestionWarning) && suggestionWarning == null,
    "Valid missing-field suggestion can be inserted into a draft.");
Check(suggested == originalTemplate + "\r\n{memory_details}" && added.SequenceEqual(new[] { "memory_details" }),
    "Appending preserves original custom text, trailing spaces and CRLF layout exactly.");
Check(originalTemplate == "我的面板  \r\nCPU {system_cpu_value}\r\n{ gpu_details }",
    "Preparing a suggestion does not mutate the original template.");
Check(PerformanceHardwareDetails.TryAppendTemplateFields(suggested, new[] { "GPU", "Memory" }, 4096,
    out var again, out var duplicateFields, out _) && again == suggested && duplicateFields.Count == 0,
    "Repeated insertion is idempotent.");
Check(PerformanceHardwareDetails.MissingTemplateFields("{ HARDWARE_DETAILS }", new[] { "CPU", "Memory", "NPU" }).Count == 0,
    "The shared field covers every selected hardware kind with formatter-compatible whitespace and casing.");
Check(PerformanceHardwareDetails.MissingTemplateFields("{{hardware_details}} {{memory_details}}", new[] { "Memory" })
    .SequenceEqual(new[] { "memory_details" }), "Escaped field examples do not count as active output.");
Check(PerformanceHardwareDetails.MissingTemplateFields("{{{MEMORY_DETAILS}}}", new[] { "Memory" }).Count == 0,
    "A real field inside escaped outer braces still counts.");
Check(PerformanceHardwareDetails.MissingTemplateFields("{gpu_details}", new[] { "UnrecognizedHardware" }).Count == 0,
    "Only supported hardware fields may be suggested.");
Check(PerformanceHardwareDetails.TryAppendTemplateFields("Title\n", new[] { "CPU" }, 4096,
    out var trailingNewline, out _, out _) && trailingNewline == "Title\n{cpu_details}",
    "An existing final line break does not create an extra blank row.");
Check(!PerformanceHardwareDetails.TryAppendTemplateFields(new string('x', 4090), new[] { "Memory" }, 4096,
    out var overlong, out _, out var lengthWarning) && overlong == new string('x', 4090) && lengthWarning != null,
    "Oversized insertion never truncates the user's text.");
Check(!PerformanceHardwareDetails.TryAppendTemplateFields("My label {unfinished", new[] { "GPU" }, 4096,
    out var malformedTemplate, out _, out var syntaxWarning) && malformedTemplate == "My label {unfinished" && syntaxWarning != null,
    "An unmatched opening brace cannot swallow a supposedly inserted field.");
Check(PerformanceHardwareDetails.TryAppendTemplateFields("", new[] { "Memory", "GPU" }, 4096,
    out var fromEmpty, out _, out _) && fromEmpty == "{memory_details}\n{gpu_details}",
    "An empty template starts directly with only the selected fields.");
Check(PerformanceHardwareDetails.TryAppendTemplateFields("Concurrent edit\n{memory_details}", new[] { "Memory", "GPU" }, 4096,
    out var mergedLatest, out _, out _) && mergedLatest == "Concurrent edit\n{memory_details}\n{gpu_details}",
    "Saving requested additions can merge into the latest template without overwriting concurrent text.");
Check(PerformanceHardwareDetails.TryAppendTemplateFields("Keep unchanged", Array.Empty<string>(), 4096,
    out var noRequest, out var noRequestFields, out _) && noRequest == "Keep unchanged" && noRequestFields.Count == 0,
    "No explicit request leaves the template untouched.");
// A settings-page edit uses its latest unsaved template while persisting only hardware details.
var persisted = new AppConfig { PerformanceOverlayTextTemplate = "已保存模板 {system_cpu}" };
var page = new AppConfig { PerformanceOverlayLayoutMode = "Template", PerformanceOverlayTextTemplate = "未保存文字 {system_cpu}\n我的注释" };
var draftBeforeDialog = page.PerformanceOverlayTextTemplate;
var pageEdit = PerformanceHardwareDetails.StageSettingsPageEdit(page, target =>
{
    if (!PerformanceHardwareDetails.TryAppendTemplateFields(target.PerformanceOverlayTextTemplate,
        new[] { "Memory" }, 4096, out var nextTemplate, out _, out _)) return;
    target.PerformanceOverlayHardwareDetails = new() { ["Memory"] = new() { Show = true } };
    target.PerformanceOverlayTextTemplate = nextTemplate;
});
Check(pageEdit != null && pageEdit.Template == draftBeforeDialog + "\n{memory_details}",
    "Hardware-dialog additions use the unsaved settings-page template, not the stored template.");
Check(page.PerformanceOverlayTextTemplate == draftBeforeDialog && page.PerformanceOverlayHardwareDetails.Count == 0,
    "Staging is detached until the page accepts the result.");
persisted.PerformanceOverlayHardwareDetails = pageEdit!.HardwareDetails;
page.PerformanceOverlayTextTemplate = pageEdit.Template;
Check(persisted.PerformanceOverlayTextTemplate == "已保存模板 {system_cpu}" && persisted.PerformanceOverlayHardwareDetails["Memory"].Show,
    "Explicitly saving hardware details does not silently save or overwrite the template.");
Check(page.PerformanceOverlayTextTemplate.StartsWith(draftBeforeDialog, StringComparison.Ordinal),
    "The template text returned to the page preserves the user's pending edits.");
var latestPage = new AppConfig { PerformanceOverlayTextTemplate = "随后更新的页面草稿\n{memory_details}" };
var concurrentStoredTemplate = "另一个入口保存的模板 {gpu_details}";
var latestEdit = PerformanceHardwareDetails.StageSettingsPageEdit(latestPage, target =>
{
    if (!PerformanceHardwareDetails.TryAppendTemplateFields(target.PerformanceOverlayTextTemplate,
        new[] { "Memory", "GPU" }, 4096, out var nextTemplate, out _, out _)) return;
    target.PerformanceOverlayHardwareDetails = new() { ["GPU"] = new() { Show = true } };
    target.PerformanceOverlayTextTemplate = nextTemplate;
});
Check(latestEdit!.Template == "随后更新的页面草稿\n{memory_details}\n{gpu_details}" &&
    concurrentStoredTemplate == "另一个入口保存的模板 {gpu_details}",
    "The latest page draft receives only missing fields while separately persisted template text remains untouched.");
var rejectedPage = new AppConfig { PerformanceOverlayTextTemplate = "不完整 {" };
var rejected = PerformanceHardwareDetails.StageSettingsPageEdit(rejectedPage, target =>
{
    if (!PerformanceHardwareDetails.TryAppendTemplateFields(target.PerformanceOverlayTextTemplate,
        new[] { "GPU" }, 4096, out var nextTemplate, out _, out _)) return;
    target.PerformanceOverlayHardwareDetails = new();
    target.PerformanceOverlayTextTemplate = nextTemplate;
});
Check(rejected == null && rejectedPage.PerformanceOverlayTextTemplate == "不完整 {",
    "A rejected insertion produces no hardware save or page mutation.");
Check(PerformanceHardwareDetails.StageSettingsPageEdit(page, _ => { }) == null,
    "No accepted dialog edit produces no settings-page save request.");
Console.WriteLine($"Hardware details regression passed: {checks} checks.");
