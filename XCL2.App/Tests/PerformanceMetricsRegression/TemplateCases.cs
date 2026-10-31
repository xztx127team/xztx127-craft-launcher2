using System.Globalization;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class TemplateCases
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var cfg = new AppConfig { PerformanceOverlayLayoutMode = "Template", PerformanceOverlayTextTemplate = "{system_cpu}\n{gpu}\n{npu}\n{games}" };
            var snapshot = new PerformanceSnapshot(DateTimeOffset.UtcNow, 8, 25, 4L << 30, 2L << 30, 5, 100,
                new[] { Game(1, "first", "First", 10), Game(2, "second", "Second", 20) }, null, null, null, null, null)
            { Hardware = new PerformanceHardwareSnapshot(new[] { new PerformanceDeviceSample("gpu", "Actual GPU", "GPU", 0, 1048576, 2048, null) }, 0, null, null, null, null, "No NPU", null) };
            Require(PerformanceOverlayTemplateFormatter.Format(snapshot, cfg).Text.StartsWith("系统 CPU 25.0%\n"), "Real CPU sample and template Enter");
            Require(PerformanceOverlayTemplateFormatter.Format(snapshot, cfg).Text.Contains("GPU 使用率 0.0%"), "Real idle GPU must stay zero");
            Require(PerformanceOverlayTemplateFormatter.Format(snapshot, cfg).Text.Contains("NPU 使用率 暂不可用"), "Missing NPU must not become zero");
            var invalidCpu = PerformanceOverlayTemplateFormatter.Format(snapshot with { SystemCpuPercent = -1 }, cfg).Text;
            Require(!invalidCpu.Contains("系统 CPU 0.0%") && invalidCpu.Contains("系统 CPU 采样中 / 不可用"), "Invalid CPU reading is unavailable, never a manufactured idle reading");
            cfg.PerformanceOverlayShowSystemCpu = cfg.PerformanceOverlayShowGpu = cfg.PerformanceOverlayShowNpu = cfg.PerformanceOverlayShowGames = false;
            var hidden = PerformanceOverlayTemplateFormatter.Format(snapshot, cfg);
            Require(hidden.Text.Length == 0 && hidden.Warning != null, "Template may not enable any unchecked display item");
            cfg.PerformanceOverlayShowGpu = true;
            cfg.PerformanceOverlayGpuShowUtilization = false;
            cfg.PerformanceOverlayGpuShowDedicatedMemory = true;
            cfg.PerformanceOverlayGpuDedicatedMemoryUnit = "KiB";
            cfg.PerformanceOverlayGpuShowSharedMemory = false;
            cfg.PerformanceOverlayTextTemplate = "{gpu_usage}\n{gpu_dedicated}\n{gpu_shared}";
            var gpu = PerformanceOverlayTemplateFormatter.Format(snapshot, cfg).Text;
            Require(gpu.Contains("专用显存 1024.00 KiB") && !gpu.Contains("使用率") && !gpu.Contains("共享"), "GPU parent/child switches and independent memory units still own values");
            cfg.PerformanceOverlayShowGpu = false;
            Require(PerformanceOverlayTemplateFormatter.Format(snapshot, cfg).Text.Length == 0, "A GPU child placeholder may not bypass its parent checkbox");
            cfg.PerformanceOverlayShowSystemCpu = true;
            cfg.PerformanceOverlayTextTemplate = "{system_cpu}\r\nnext\n{system_cpu}";
            cfg.PerformanceOverlayLineBreakMode = "SingleLine";
            var single = PerformanceOverlayTemplateFormatter.Format(snapshot, cfg);
            Require(!single.Text.Contains('\r') && !single.Text.Contains('\n') && !PerformanceOverlayTemplateFormatter.AutoWrap(cfg), "All line breaks removed and soft wrap disabled for true single-line mode");
            cfg.PerformanceOverlayLineBreakMode = "Preserve";
            Require(PerformanceOverlayTemplateFormatter.Format(snapshot, cfg).Text.Contains("\nnext\n"), "Preserve mode keeps user-authored lines");
            cfg.PerformanceOverlayTextTemplate = "| {system_cpu} |\n  note";
            Require(PerformanceOverlayTemplateFormatter.Format(snapshot, cfg).Text == "| 系统 CPU 25.0% |\n  note", "Custom literal separators and indentation stay intact");
            cfg.PerformanceOverlayAutoWrap = false;
            Require(!PerformanceOverlayTemplateFormatter.AutoWrap(cfg), "No-wrap with preserved hard line breaks");
            cfg.PerformanceOverlayLineBreakMode = "EachField";
            cfg.PerformanceOverlayTextTemplate = "{system_cpu}{system_memory}";
            Require(PerformanceOverlayTemplateFormatter.Format(snapshot, cfg).Text.Contains("25.0%\n内存"), "Adjacent placeholders receive separate lines in each-field mode");
            cfg.PerformanceOverlayTextTemplate = "{system_cpu} · {system_memory}\n\n";
            var separated = PerformanceOverlayTemplateFormatter.Format(snapshot, cfg).Text;
            Require(!separated.Contains("\n\n") && !separated.Split('\n').Any(line => line.Trim() == "·"), "Each-field policy removes separator-only/empty lines");
            cfg.PerformanceOverlayLineBreakMode = "Preserve";
            cfg.PerformanceOverlayTextTemplate = "{{system_cpu}}\n{unknown}\n{system_cpu+1}";
            var unknown = PerformanceOverlayTemplateFormatter.Format(snapshot, cfg);
            Require(unknown.Text == "{system_cpu}\n{unknown}\n{system_cpu+1}" && unknown.UnknownFields.Count == 2 && unknown.Warning!.Contains("未知字段"), "Escaped braces and unknown fields preserve user text without evaluating expressions");
            cfg.PerformanceOverlayTextTemplate = "我的电脑 · CPU {system_cpu_value}\n内存 {memory_used_value} / {memory_total_value}";
            var customLabels = PerformanceOverlayTemplateFormatter.Format(snapshot, cfg);
            Require(customLabels.Text == "我的电脑 · CPU 25.0%\n内存 2.00 GB / 4.00 GB", "Value-only fields support user-authored labels without duplicated built-in names");
            cfg.PerformanceOverlayShowSystemCpu = false;
            cfg.PerformanceOverlayTextTemplate = "CPU {system_cpu_value}";
            var disabledValue = PerformanceOverlayTemplateFormatter.Format(snapshot, cfg);
            Require(disabledValue.Text == "CPU " && disabledValue.Warning != null, "Value-only placeholders respect display switches and preserve literal text");
            cfg.PerformanceOverlayShowSystemCpu = true;
            cfg.PerformanceOverlayShowGpu = true;
            cfg.PerformanceOverlayTextTemplate = "显存 {gpu_dedicated_value}";
            Require(PerformanceOverlayTemplateFormatter.Format(snapshot, cfg).Text == "显存 1024.00 KiB", "GPU value-only placeholder respects its independent display unit");
            cfg.PerformanceOverlayShowGpu = false;
            cfg.PerformanceOverlayTextTemplate = "纯文字标题\n{{自定义花括号}}";
            var literal = PerformanceOverlayTemplateFormatter.Format(snapshot, cfg);
            Require(literal.Text == "纯文字标题\n{自定义花括号}" && literal.Warning == null, "Literal-only templates remain valid and preserve line breaks");
            cfg.PerformanceOverlayTextTemplate = "{system_cpu";
            Require(PerformanceOverlayTemplateFormatter.Format(snapshot, cfg).Warning!.Contains("未配对"), "Malformed template gives a friendly diagnostic");
            cfg.PerformanceOverlayTextTemplate = new string('x', 5000);
            var bounded = PerformanceOverlayTemplateFormatter.Format(snapshot, cfg);
            Require(bounded.Text.Length <= 4096 && bounded.Warning != null, "Damaged overlong templates have a bounded fallback");
            cfg.PerformanceOverlayShowGames = true;
            cfg.PerformanceOverlayAllInstances = false;
            cfg.PerformanceOverlayInstanceId = "first";
            cfg.PerformanceOverlayTextTemplate = "{games}\n{game_cpu}\n{game_memory}\n{game_pid}\n{game_totals_cpu}";
            cfg.PerformanceOverlayShowGameMemory = false;
            cfg.PerformanceOverlayShowProcessId = false;
            cfg.PerformanceOverlayShowGameTotals = false;
            var game = PerformanceOverlayTemplateFormatter.Format(snapshot, cfg).Text;
            Require(game.Contains("First") && !game.Contains("Second") && !game.Contains("PID") && !game.Contains("内存") && !game.Contains("合计"), "Game templates retain selected scope, child fields and totals toggles");
            cfg.PerformanceOverlayShowGames = false;
            Require(PerformanceOverlayTemplateFormatter.Format(snapshot, cfg).Text.Length == 0, "Game child placeholders may not bypass the game-list parent");
            cfg.PerformanceOverlayTextTemplate = "{system_cpu}\n{gpu}";
            var unavailable = PerformanceOverlayTemplateFormatter.Format(null, cfg);
            Require(unavailable.Text.Contains("不可用") && !unavailable.Text.Contains("25.0%") && unavailable.ToolTip.Contains("没有填入示例"), "No snapshot preview never uses fabricated example readings");
            cfg.PerformanceOverlayLayoutMode = "Default";
            cfg.PerformanceOverlayLineBreakMode = "Preserve";
            Require(!PerformanceOverlayTemplateFormatter.UsesCustomText(cfg), "Existing compact/detailed rendering remains the default");
            cfg.PerformanceOverlayLineBreakMode = "SingleLine";
            Require(PerformanceOverlayTemplateFormatter.UsesCustomText(cfg) && !PerformanceOverlayTemplateFormatter.Format(snapshot, cfg).Text.Contains('\n'), "Global single-line switch works on the old layout too");
            cfg.PerformanceOverlayTextTemplate = "{system_cpu}\n{gpu}";
            var restored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg))!;
            Require(restored.PerformanceOverlayTextTemplate == cfg.PerformanceOverlayTextTemplate && restored.PerformanceOverlayLineBreakMode == "SingleLine"
                && !restored.PerformanceOverlayAutoWrap && !restored.PerformanceOverlayShowGpu, "Layout and newline preferences survive save without changing selected fields");
            var old = JsonSerializer.Deserialize<AppConfig>("{}")!;
            Require(old.PerformanceOverlayLayoutMode == "Default" && old.PerformanceOverlayLineBreakMode == "Preserve" && old.PerformanceOverlayAutoWrap, "Older config preserves the existing layout and wrapping");
            Require(!old.PerformanceOverlayClickThrough, "Missing click-through preference uses ordinary interactive display");
            cfg.PerformanceOverlayClickThrough = true;
            Require(JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg))!.PerformanceOverlayClickThrough, "Explicitly enabling click-through survives save and reload");
            cfg.PerformanceOverlayClickThrough = false;
            Require(!JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg))!.PerformanceOverlayClickThrough, "Explicitly disabling click-through survives save and reload");
            var detailCfg = new AppConfig { PerformanceOverlayLayoutMode = "Template", PerformanceOverlayTextTemplate = "{hardware_details}\n{memory_details}", PerformanceOverlayShowGames = false };
            detailCfg.PerformanceOverlayHardwareDetails["Memory"] = new() { Show = true, ShowManufacturer = false, ShowSpeed = false, ShowMemoryChannels = true, MemoryChannels = "双通道" };
            var detailText = PerformanceOverlayTemplateFormatter.Format(snapshot, detailCfg);
            Require(detailText.UnknownFields.Count == 0 && detailText.Text.Contains("内存通道 双通道（手动）"), "Hardware-detail placeholders work even when game statistics are disabled");
            detailCfg.PerformanceOverlayHardwareDetails["Memory"].Show = false;
            Require(PerformanceOverlayTemplateFormatter.Format(snapshot, detailCfg).Text.Length == 0, "Detail templates never bypass their own hardware display switch");
            detailCfg.PerformanceOverlayHardwareDetails["Memory"].Show = true;
            detailCfg.PerformanceOverlayLayoutMode = "Default";
            detailCfg.PerformanceOverlayLineBreakMode = "SingleLine";
            var detailSingle = PerformanceOverlayTemplateFormatter.Format(snapshot, detailCfg).Text;
            Require(detailSingle.Contains("双通道（手动）") && !detailSingle.Contains('\n'), "Hardware details follow default single-line layout and wrapping policy");
            Console.WriteLine("Performance field selection, templates, global line policies, unknown values and persistence regression cases passed.");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
    private static PerformanceGameSample Game(int pid, string id, string name, double cpu)
        => new(pid, id, name, "Java", cpu, 1048576, null, null);
}
