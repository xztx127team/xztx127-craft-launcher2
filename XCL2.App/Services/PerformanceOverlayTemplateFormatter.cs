using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

public sealed record PerformanceOverlayTemplateField(string Name, string Description)
{
    public string Placeholder => "{" + Name + "}";
    public string Label => Placeholder + " · " + Description;
}
public sealed record PerformanceOverlayTextResult(string Text, string ToolTip, IReadOnlyList<string> UnknownFields, string? Warning);

/// <summary>仅替换有限的已知字段。不执行表达式、脚本、反射或自定义格式代码；所有字段仍服从原显示开关。</summary>
public static class PerformanceOverlayTemplateFormatter
{
    public const int MaximumTemplateLength = 4096;
    public const string DefaultTemplate = "{system_cpu} · {system_memory}\n{gpu}\n{npu}\n{launcher}\n{game_totals}\n{games}\n{disk}\n{network}\n{hardware_details}";
    public static IReadOnlyList<PerformanceOverlayTemplateField> Fields { get; } = new PerformanceOverlayTemplateField[]
    {
        new PerformanceOverlayTemplateField("system_cpu", "系统 CPU"),
        new("hardware_details", "已选硬件厂商、速率、速度、内存通道与容量"),
        new("cpu_details", "已选 CPU 资料"), new("memory_details", "已选内存资料"),
        new("gpu_details", "已选 GPU 资料"), new("npu_details", "已选 NPU 资料"),
        new("disk_details", "已选磁盘资料"), new("network_details", "已选网卡资料"),
        new("system_memory", "系统内存总览（含已选百分比）"), new("memory_used", "系统已用内存"),
        new("memory_total", "系统总内存"), new("memory_available", "系统可用内存"), new("memory_percent", "已选内存使用百分比"),
        new("gpu", "GPU 所有已选指标"), new("gpu_usage", "GPU 已选使用率"), new("gpu_dedicated", "GPU 已选专用显存"), new("gpu_shared", "GPU 已选共享内存"),
        new("npu", "NPU 所有已选指标"), new("npu_usage", "NPU 已选使用率"), new("npu_dedicated", "NPU 已选专用内存"), new("npu_shared", "NPU 已选共享内存"),
        new("launcher", "启动器 CPU 与内存"), new("launcher_cpu", "启动器 CPU"), new("launcher_memory", "启动器内存"),
        new("games", "所选游戏列表与已选指标"), new("game_cpu", "所选游戏 CPU"), new("game_memory", "所选游戏内存"), new("game_pid", "所选游戏 PID"),
        new("game_totals", "已选游戏合计"), new("game_totals_cpu", "已选游戏 CPU 合计"), new("game_totals_memory", "已选游戏内存合计"),
        new("disk", "已选磁盘读写"), new("disk_read", "已选磁盘读取"), new("disk_write", "已选磁盘写入"),
        new("network", "已选网卡收发"), new("network_receive", "已选网卡接收"), new("network_send", "已选网卡发送"),
        new("system_cpu_value", "系统 CPU 数值与单位，可自定义名称"),
        new("memory_used_value", "已用内存数值与单位"), new("memory_total_value", "总内存数值与单位"),
        new("memory_available_value", "可用内存数值与单位"), new("memory_percent_value", "内存使用百分比数值"),
        new("launcher_cpu_value", "启动器 CPU 数值与单位"), new("launcher_memory_value", "启动器内存数值与单位"),
        new("gpu_usage_value", "GPU 使用率数值，多设备按行显示"), new("gpu_dedicated_value", "GPU 专用显存数值，多设备按行显示"), new("gpu_shared_value", "GPU 共享内存数值，多设备按行显示"),
        new("npu_usage_value", "NPU 使用率数值，多设备按行显示"), new("npu_dedicated_value", "NPU 专用内存数值，多设备按行显示"), new("npu_shared_value", "NPU 共享内存数值，多设备按行显示"),
        new("disk_read_value", "磁盘读取速率数值与单位"), new("disk_write_value", "磁盘写入速率数值与单位"),
        new("network_receive_value", "网卡接收速率数值与单位"), new("network_send_value", "网卡发送速率数值与单位")
    };

    public static string LayoutMode(AppConfig cfg) => cfg.PerformanceOverlayLayoutMode == "Template" ? "Template" : "Default";
    public static string LineBreakMode(AppConfig cfg) => cfg.PerformanceOverlayLineBreakMode is "SingleLine" or "EachField" ? cfg.PerformanceOverlayLineBreakMode : "Preserve";
    public static bool UsesCustomText(AppConfig cfg) => LayoutMode(cfg) == "Template" || LineBreakMode(cfg) != "Preserve";
    public static bool AutoWrap(AppConfig cfg) => LineBreakMode(cfg) != "SingleLine" && cfg.PerformanceOverlayAutoWrap;

    public static PerformanceOverlayTextResult Format(PerformanceSnapshot? snapshot, AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var fields = BuildFields(snapshot, cfg);
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        string text;
        if (LayoutMode(cfg) == "Template")
        {
            var template = cfg.PerformanceOverlayTextTemplate ?? "";
            if (template.Length > MaximumTemplateLength)
            { template = template[..MaximumTemplateLength]; warnings.Add("模板超过 4096 字符，仅显示允许长度内的内容。"); }
            text = Expand(template, fields, LineBreakMode(cfg), unknown, warnings, out var knownCount, out var activeCount);
            if (knownCount > 0 && activeCount == 0) warnings.Add("模板引用的显示项目均未勾选；固定文字会保留，字段不会自动启用。");
            if (string.IsNullOrWhiteSpace(template)) warnings.Add("模板为空，未显示性能字段。");
        }
        else
        {
            if (LineBreakMode(cfg) == "Preserve") text = LegacyPreviewText(snapshot, cfg);
            else
            {
                var order = new[] { "system_cpu", "system_memory", "gpu", "npu", "disk", "network", "hardware_details", "launcher", "game_totals", "games" };
                var items = order.SelectMany(key => fields[key]).ToList();
                if (PerformanceOverlaySettings.Detailed(cfg)) items.InsertRange(fields["system_cpu"].Count + fields["system_memory"].Count, fields["memory_available"]);
                text = string.Join("\n", items);
            }
        }
        text = NormalizeLines(text, LineBreakMode(cfg));
        if (unknown.Count > 0) warnings.Add("未知字段按原文保留，请检查字段名称：" + string.Join("、", unknown.Take(12).Select(name => "{" + name + "}")) + (unknown.Count > 12 ? "…" : ""));
        if (string.IsNullOrWhiteSpace(text) && warnings.Count == 0) warnings.Add("当前没有可显示的已选字段；模板不会自动启用已取消的项目。");
        return new(text, BuildToolTip(snapshot), unknown.ToArray(), warnings.Count == 0 ? null : string.Join("\n", warnings.Distinct()));
    }

    private static Dictionary<string, List<string>> BuildFields(PerformanceSnapshot? sample, AppConfig cfg)
    {
        var fields = Fields.ToDictionary(field => field.Name, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var unit = PerformanceOverlaySettings.MemoryUnit(cfg);
        void Add(string key, string value, bool selected = true) { if (selected) fields[key].Add(value); }
        void AddLabeled(string key, string label, string value, bool selected = true)
        {
            Add(key, label + value, selected);
            Add(key + "_value", value, selected);
        }
        void Group(string group, params string[] children) { foreach (var child in children) fields[group].AddRange(fields[child]); }
        AddLabeled("system_cpu", "系统 CPU ", PerformanceOverlaySettings.FormatCpu(sample?.SystemCpuPercent), cfg.PerformanceOverlayShowSystemCpu);
        if (cfg.PerformanceOverlayShowSystemMemory)
        {
            var percent = PerformanceMetricsMath.MemoryUsedPercent(ValidUsedMemory(sample), sample?.TotalMemoryBytes);
            var percentText = percent.HasValue ? $"{percent.Value:0.0}%" : "暂不可用";
            Add("system_memory", "内存 " + PerformanceOverlaySettings.FormatMemory(ValidUsedMemory(sample), unit) + " / " + PerformanceOverlaySettings.FormatMemory(sample?.TotalMemoryBytes, unit) +
                (cfg.PerformanceOverlayShowMemoryPercent ? "（" + percentText + "）" : ""));
            AddLabeled("memory_used", "已用内存 ", PerformanceOverlaySettings.FormatMemory(ValidUsedMemory(sample), unit));
            AddLabeled("memory_total", "总内存 ", PerformanceOverlaySettings.FormatMemory(sample?.TotalMemoryBytes, unit));
            AddLabeled("memory_available", "可用内存 ", PerformanceOverlaySettings.FormatMemory(sample?.AvailableMemoryBytes, unit));
            AddLabeled("memory_percent", "内存使用 ", percentText, cfg.PerformanceOverlayShowMemoryPercent);
        }
        foreach (var kind in new[] { "GPU", "NPU" })
        {
            var options = PerformanceOverlaySettings.HardwareDisplay(cfg, kind);
            if (!options.Show) continue;
            var key = kind.ToLowerInvariant();
            var devices = sample?.Hardware?.Devices.Where(device => device.Kind == kind).ToArray() ?? Array.Empty<PerformanceDeviceSample>();
            if (devices.Length == 0)
            {
                AddLabeled(key + "_usage", kind + " 使用率 ", "暂不可用", options.ShowUtilization);
                AddLabeled(key + "_dedicated", kind + (kind == "GPU" ? " 专用显存 " : " 专用内存 "), "暂不可用", options.ShowDedicatedMemory);
                AddLabeled(key + "_shared", kind + " 共享内存 ", "暂不可用", options.ShowSharedMemory);
            }
            foreach (var device in devices)
            {
                var label = kind + (devices.Length > 1 || PerformanceOverlaySettings.Detailed(cfg) ? " · " + Inline(device.DisplayName) : "");
                AddLabeled(key + "_usage", label + " 使用率 ", PerformanceOverlaySettings.FormatHardwareUtilization(device.UtilizationPercent, options.UtilizationFormat), options.ShowUtilization);
                AddLabeled(key + "_dedicated", label + (kind == "GPU" ? " 专用显存 " : " 专用内存 "), PerformanceOverlaySettings.FormatHardwareMemory(device.DedicatedMemoryBytes, options.DedicatedMemoryUnit), options.ShowDedicatedMemory);
                AddLabeled(key + "_shared", label + " 共享内存 ", PerformanceOverlaySettings.FormatHardwareMemory(device.SharedMemoryBytes, options.SharedMemoryUnit), options.ShowSharedMemory);
            }
            Group(key, key + "_usage", key + "_dedicated", key + "_shared");
        }
        AddLabeled("launcher_cpu", "启动器 CPU ", PerformanceOverlaySettings.FormatCpu(sample?.LauncherCpuPercent), cfg.PerformanceOverlayShowLauncher);
        AddLabeled("launcher_memory", "启动器内存 ", PerformanceOverlaySettings.FormatMemory(sample?.LauncherWorkingSetBytes, unit), cfg.PerformanceOverlayShowLauncher);
        Group("launcher", "launcher_cpu", "launcher_memory");
        AddLabeled("disk_read", "磁盘读 ", Rate(sample?.Hardware?.DiskReadBytesPerSecond), cfg.PerformanceOverlayShowDisk);
        AddLabeled("disk_write", "磁盘写 ", Rate(sample?.Hardware?.DiskWriteBytesPerSecond), cfg.PerformanceOverlayShowDisk);
        Group("disk", "disk_read", "disk_write");
        AddLabeled("network_receive", "网卡合计 ↓ ", Rate(sample?.Hardware?.NetworkReceivedBytesPerSecond), cfg.PerformanceOverlayShowNetwork);
        AddLabeled("network_send", "网卡合计 ↑ ", Rate(sample?.Hardware?.NetworkSentBytesPerSecond), cfg.PerformanceOverlayShowNetwork);
        Group("network", "network_receive", "network_send");
        foreach (var kind in PerformanceHardwareDetails.Kinds)
        {
            var text = PerformanceHardwareDetails.FormatKind(sample?.Hardware, cfg, kind.Key);
            if (text.Length == 0) continue;
            Add(kind.Key.ToLowerInvariant() + "_details", text);
            Add("hardware_details", text);
        }
        if (!cfg.PerformanceOverlayShowGames) return fields;
        var games = sample == null ? Array.Empty<PerformanceGameSample>() : PerformanceOverlaySettings.SelectGames(sample, cfg);
        if (sample == null) Add("games", "游戏数据暂不可用");
        else if (games.Count == 0) Add("games", cfg.PerformanceOverlayAllInstances ? "暂无运行中的游戏实例" : "所选实例未运行：" + Inline(cfg.PerformanceOverlayInstanceName));
        foreach (var game in games)
        {
            var label = Inline(game.DisplayName);
            var name = label + (cfg.PerformanceOverlayShowProcessId ? $" [PID {game.ProcessId}]" : "");
            var parts = new List<string>();
            if (cfg.PerformanceOverlayShowGameCpu) parts.Add("CPU " + PerformanceOverlaySettings.FormatCpu(game.CpuPercent));
            if (cfg.PerformanceOverlayShowGameMemory) parts.Add("内存 " + PerformanceOverlaySettings.FormatMemory(game.WorkingSetBytes, unit));
            Add("games", name + (parts.Count == 0 ? "" : " · " + string.Join(" · ", parts)));
            Add("game_cpu", label + " CPU " + PerformanceOverlaySettings.FormatCpu(game.CpuPercent), cfg.PerformanceOverlayShowGameCpu);
            Add("game_memory", label + " 内存 " + PerformanceOverlaySettings.FormatMemory(game.WorkingSetBytes, unit), cfg.PerformanceOverlayShowGameMemory);
            Add("game_pid", label + $" PID {game.ProcessId}", cfg.PerformanceOverlayShowProcessId);
        }
        if (cfg.PerformanceOverlayShowGameTotals)
        {
            var totals = sample == null ? null : PerformanceMetricsMath.Summarize(games);
            Add("game_totals", "游戏合计 · " + (totals == null ? "数量暂不可用" : totals.GameCount + " 个进程"));
            Add("game_totals_cpu", "游戏合计 CPU " + PerformanceOverlaySettings.FormatCpu(totals?.CpuPercent), cfg.PerformanceOverlayShowGameCpu);
            Add("game_totals_memory", "游戏合计内存 " + PerformanceOverlaySettings.FormatMemory(totals?.WorkingSetBytes, unit), cfg.PerformanceOverlayShowGameMemory);
            Group("game_totals", "game_totals_cpu", "game_totals_memory");
        }
        return fields;
    }

    private static string LegacyPreviewText(PerformanceSnapshot? sample, AppConfig cfg)
    {
        var blocks = new List<string>();
        var detailed = PerformanceOverlaySettings.Detailed(cfg);
        var unit = PerformanceOverlaySettings.MemoryUnit(cfg);
        var system = new List<string>();
        if (cfg.PerformanceOverlayShowSystemCpu) system.Add("系统 CPU " + PerformanceOverlaySettings.FormatCpu(sample?.SystemCpuPercent));
        if (cfg.PerformanceOverlayShowSystemMemory)
        {
            var percent = PerformanceMetricsMath.MemoryUsedPercent(ValidUsedMemory(sample), sample?.TotalMemoryBytes);
            system.Add("内存 " + PerformanceOverlaySettings.FormatMemory(ValidUsedMemory(sample), unit) + " / " + PerformanceOverlaySettings.FormatMemory(sample?.TotalMemoryBytes, unit) +
                (cfg.PerformanceOverlayShowMemoryPercent && percent.HasValue ? $"（{percent.Value:0.0}%）" : ""));
            if (detailed) system.Add("可用 " + PerformanceOverlaySettings.FormatMemory(sample?.AvailableMemoryBytes, unit));
        }
        if (system.Count > 0) blocks.Add(string.Join(detailed ? "\n" : "  ·  ", system));
        foreach (var kind in new[] { "GPU", "NPU" })
        {
            var display = PerformanceOverlaySettings.HardwareDisplay(cfg, kind);
            if (!display.Show || (!display.ShowUtilization && !display.ShowDedicatedMemory && !display.ShowSharedMemory)) continue;
            var devices = sample?.Hardware?.Devices.Where(device => device.Kind == kind).ToArray() ?? Array.Empty<PerformanceDeviceSample>();
            if (devices.Length == 0) blocks.Add(kind + " 暂不可用");
            foreach (var device in devices)
            {
                var label = kind + (detailed || devices.Length > 1 ? " · " + Inline(device.DisplayName) : "");
                blocks.Add(label + (display.ShowUtilization ? "  " + PerformanceOverlaySettings.FormatHardwareUtilization(device.UtilizationPercent, display.UtilizationFormat) : ""));
                var memory = new List<string>();
                if (display.ShowDedicatedMemory) memory.Add((kind == "GPU" ? "专用显存 " : "专用内存 ") + PerformanceOverlaySettings.FormatHardwareMemory(device.DedicatedMemoryBytes, display.DedicatedMemoryUnit));
                if (display.ShowSharedMemory) memory.Add("共享内存 " + PerformanceOverlaySettings.FormatHardwareMemory(device.SharedMemoryBytes, display.SharedMemoryUnit));
                if (memory.Count > 0) blocks.Add("  " + string.Join(" · ", memory));
            }
        }
        if (cfg.PerformanceOverlayShowDisk) blocks.Add("磁盘 读 " + Rate(sample?.Hardware?.DiskReadBytesPerSecond) + " · 写 " + Rate(sample?.Hardware?.DiskWriteBytesPerSecond));
        if (cfg.PerformanceOverlayShowNetwork) blocks.Add("网卡合计 ↓ " + Rate(sample?.Hardware?.NetworkReceivedBytesPerSecond) + " · ↑ " + Rate(sample?.Hardware?.NetworkSentBytesPerSecond));
        var details = PerformanceHardwareDetails.Format(sample?.Hardware, cfg);
        if (details.Length > 0) blocks.Add(details);
        if (cfg.PerformanceOverlayShowLauncher) blocks.Add("启动器  CPU " + PerformanceOverlaySettings.FormatCpu(sample?.LauncherCpuPercent) + "  ·  " + PerformanceOverlaySettings.FormatMemory(sample?.LauncherWorkingSetBytes, unit));
        if (cfg.PerformanceOverlayShowGames)
        {
            var games = sample == null ? Array.Empty<PerformanceGameSample>() : PerformanceOverlaySettings.SelectGames(sample, cfg);
            if (cfg.PerformanceOverlayShowGameTotals)
            {
                var totals = sample == null ? null : PerformanceMetricsMath.Summarize(games);
                var values = new List<string> { "游戏合计 · " + (totals == null ? "数量暂不可用" : totals.GameCount + " 个进程") };
                if (cfg.PerformanceOverlayShowGameCpu) values.Add("CPU " + PerformanceOverlaySettings.FormatCpu(totals?.CpuPercent));
                if (cfg.PerformanceOverlayShowGameMemory) values.Add(PerformanceOverlaySettings.FormatMemory(totals?.WorkingSetBytes, unit));
                blocks.Add(string.Join("  ·  ", values));
            }
            if (sample == null) blocks.Add("游戏数据暂不可用");
            else if (games.Count == 0) blocks.Add(cfg.PerformanceOverlayAllInstances ? "暂无运行中的游戏实例" : "所选实例未运行：" + Inline(cfg.PerformanceOverlayInstanceName));
            foreach (var game in games)
            {
                var name = Inline(game.DisplayName) + (cfg.PerformanceOverlayShowProcessId ? $" [PID {game.ProcessId}]" : "");
                var values = new List<string>();
                if (cfg.PerformanceOverlayShowGameCpu) values.Add("CPU " + PerformanceOverlaySettings.FormatCpu(game.CpuPercent));
                if (cfg.PerformanceOverlayShowGameMemory) values.Add(PerformanceOverlaySettings.FormatMemory(game.WorkingSetBytes, unit));
                blocks.Add(name + (values.Count == 0 ? "" : (detailed ? "\n" : "  ") + string.Join("  ·  ", values)));
            }
        }
        return string.Join("\n", blocks);
    }

    private static string Expand(string template, Dictionary<string, List<string>> fields, string mode,
        HashSet<string> unknown, List<string> warnings, out int knownCount, out int activeCount)
    {
        knownCount = 0;
        activeCount = 0;
        var output = new StringBuilder();
        var literal = new StringBuilder();
        void FlushLiteral()
        {
            if (mode == "EachField")
            {
                var value = literal.ToString().Trim();
                if (value.Length > 0 && value.Any(c => !char.IsWhiteSpace(c) && c is not ('·' or '|')))
                { if (output.Length > 0) output.Append('\n'); output.Append(value); }
            }
            else output.Append(literal);
            literal.Clear();
        }
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (i + 1 < template.Length && ((c == '{' && template[i + 1] == '{') || (c == '}' && template[i + 1] == '}')))
            { literal.Append(c); i++; continue; }
            if (c != '{') { literal.Append(c); continue; }
            var close = template.IndexOf('}', i + 1);
            if (close < 0) { literal.Append(template.AsSpan(i)); warnings.Add("有未配对的左花括号，已按普通文字显示。"); break; }
            FlushLiteral();
            var key = template[(i + 1)..close].Trim();
            if (fields.TryGetValue(key, out var values))
            {
                knownCount++;
                if (values.Count > 0)
                {
                    activeCount++;
                    if (mode == "EachField" && output.Length > 0 && output[^1] != '\n') output.Append('\n');
                    output.Append(string.Join("\n", values));
                }
            }
            else
            {
                unknown.Add(key.Length <= 60 ? key : key[..60] + "…");
                // Keep user-authored text visible, including typos, so it can be corrected in the editor.
                if (mode == "EachField" && output.Length > 0 && output[^1] != '\n') output.Append('\n');
                output.Append(template.AsSpan(i, close - i + 1));
            }
            i = close;
        }
        FlushLiteral();
        return output.ToString();
    }

    private static string NormalizeLines(string text, string mode)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (mode == "EachField") return string.Join("\n", normalized.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)));
        if (mode != "SingleLine") return normalized.Trim('\n');
        return string.Join(" · ", normalized.Split('\n').Select(line => line.Trim(' ', '\t')).Where(line => line.Length > 0));
    }
    private static long? ValidUsedMemory(PerformanceSnapshot? sample)
    {
        if (sample?.TotalMemoryBytes is not { } total || total <= 0 || sample.AvailableMemoryBytes is not { } available || available < 0 || available > total) return null;
        return total - available;
    }
    private static string Inline(string? text) => (text ?? "").Replace('\r', ' ').Replace('\n', ' ');
    private static string Rate(double? bytes)
        => bytes is { } value && double.IsFinite(value) && value >= 0 ? value >= 1048576 ? $"{value / 1048576:0.0} MB/s" : $"{value / 1024:0.0} KB/s" : "暂不可用";
    private static string BuildToolTip(PerformanceSnapshot? sample)
    {
        if (sample == null) return "尚未获得有效采样。此预览没有填入示例占用数据。";
        var reasons = new[] { sample.SystemCpuUnavailableReason, sample.MemoryUnavailableReason, sample.LauncherCpuUnavailableReason,
            sample.LauncherMemoryUnavailableReason, sample.Hardware?.GpuUnavailableReason, sample.Hardware?.NpuUnavailableReason, sample.Hardware?.IoUnavailableReason, sample.DiscoveryWarning }
            .Concat(sample.Games.SelectMany(game => new[] { game.CpuUnavailableReason, game.MemoryUnavailableReason }))
            .Concat(sample.Hardware?.Devices.Select(device => device.UnavailableReason) ?? Array.Empty<string?>())
            .Where(value => !string.IsNullOrWhiteSpace(value));
        return string.Join("\n", reasons.Distinct()) + "\nGPU/NPU 使用率为最繁忙引擎；网卡合计可能包含虚拟网卡。";
    }
}
