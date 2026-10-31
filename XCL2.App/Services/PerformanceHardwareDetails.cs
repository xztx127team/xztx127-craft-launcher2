using XCL2.App.Models;

namespace XCL2.App.Services;

public sealed record PerformanceHardwarePageEdit(
    Dictionary<string, PerformanceHardwareDetailOptions> HardwareDetails, string Template);

/// <summary>Pure, bounded display projection; manual metadata is always distinguished from measured data.</summary>
public static class PerformanceHardwareDetails
{
    public const int MaximumValueLength = 120;
    public static IReadOnlyList<(string Key, string Label)> Kinds { get; } = new[]
    {
        ("CPU", "CPU"), ("Memory", "内存"), ("GPU", "GPU"), ("NPU", "NPU"), ("Disk", "磁盘"), ("Network", "网卡")
    };
    public static IReadOnlyList<(string Key, string Label)> Fields { get; } = new[]
    {
        ("Manufacturer", "厂商"), ("DefaultRate", "默认/基准速率"), ("StandardRate", "标准/额定速率"),
        ("Speed", "速度"), ("MemoryChannels", "内存通道"), ("MemoryCapacity", "内存/显存容量")
    };

    public static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? "" :
        new string(value.Trim().Where(c => !char.IsControl(c)).Take(MaximumValueLength).ToArray());

    public static PerformanceHardwareDetailOptions Options(AppConfig cfg, string kind)
        => cfg.PerformanceOverlayHardwareDetails?.TryGetValue(kind, out var value) == true && value != null
            ? value : new PerformanceHardwareDetailOptions();

    public static Dictionary<string, PerformanceHardwareDetailOptions> Clone(AppConfig cfg)
        => Kinds.ToDictionary(kind => kind.Key, kind => Copy(Options(cfg, kind.Key)));

    /// <summary>
    /// Stage a hardware dialog's accepted edit against the latest settings-page draft, never persisted text.
    /// The caller persists HardwareDetails explicitly and returns Template to the page's normal save flow.
    /// A rejected dialog transaction leaves the dictionary untouched and produces no page edit.
    /// </summary>
    public static PerformanceHardwarePageEdit? StageSettingsPageEdit(AppConfig latestPageDraft, Action<AppConfig> edit)
    {
        var staged = new AppConfig
        {
            PerformanceOverlayHardwareDetails = Clone(latestPageDraft),
            PerformanceOverlayTextTemplate = latestPageDraft.PerformanceOverlayTextTemplate,
            PerformanceOverlayLayoutMode = latestPageDraft.PerformanceOverlayLayoutMode
        };
        var originalDetails = staged.PerformanceOverlayHardwareDetails;
        edit(staged);
        if (ReferenceEquals(originalDetails, staged.PerformanceOverlayHardwareDetails)) return null;
        return new(Clone(staged), staged.PerformanceOverlayTextTemplate ?? "");
    }

    public static PerformanceHardwareDetailOptions Copy(PerformanceHardwareDetailOptions value) => new()
    {
        Show = value.Show, ShowManufacturer = value.ShowManufacturer, ShowDefaultRate = value.ShowDefaultRate,
        ShowStandardRate = value.ShowStandardRate, ShowSpeed = value.ShowSpeed,
        ShowMemoryChannels = value.ShowMemoryChannels, ShowMemoryCapacity = value.ShowMemoryCapacity,
        Manufacturer = Clean(value.Manufacturer), DefaultRate = Clean(value.DefaultRate), StandardRate = Clean(value.StandardRate),
        Speed = Clean(value.Speed), MemoryChannels = Clean(value.MemoryChannels), MemoryCapacity = Clean(value.MemoryCapacity)
    };

    public static bool Selected(PerformanceHardwareDetailOptions value, string key) => key switch
    {
        "Manufacturer" => value.ShowManufacturer, "DefaultRate" => value.ShowDefaultRate,
        "StandardRate" => value.ShowStandardRate, "Speed" => value.ShowSpeed,
        "MemoryChannels" => value.ShowMemoryChannels, "MemoryCapacity" => value.ShowMemoryCapacity, _ => false
    };
    public static string ManualValue(PerformanceHardwareDetailOptions value, string key) => Clean(key switch
    {
        "Manufacturer" => value.Manufacturer, "DefaultRate" => value.DefaultRate, "StandardRate" => value.StandardRate,
        "Speed" => value.Speed, "MemoryChannels" => value.MemoryChannels, "MemoryCapacity" => value.MemoryCapacity, _ => ""
    });
    public static void Set(PerformanceHardwareDetailOptions value, string key, bool selected, string? text)
    {
        text = Clean(text);
        switch (key)
        {
            case "Manufacturer": value.ShowManufacturer = selected; value.Manufacturer = text; break;
            case "DefaultRate": value.ShowDefaultRate = selected; value.DefaultRate = text; break;
            case "StandardRate": value.ShowStandardRate = selected; value.StandardRate = text; break;
            case "Speed": value.ShowSpeed = selected; value.Speed = text; break;
            case "MemoryChannels": value.ShowMemoryChannels = selected; value.MemoryChannels = text; break;
            case "MemoryCapacity": value.ShowMemoryCapacity = selected; value.MemoryCapacity = text; break;
        }
    }
    public static string AutomaticValue(PerformanceHardwareSnapshot? snapshot, string kind, string key)
        => snapshot?.Metadata.TryGetValue(kind, out var fields) == true && fields.TryGetValue(key, out var value)
            ? Clean(value) : "";

    public static string FormatKind(PerformanceHardwareSnapshot? snapshot, AppConfig cfg, string kind)
    {
        var options = Options(cfg, kind);
        if (!options.Show) return "";
        var fields = new List<string>();
        foreach (var field in Fields)
        {
            if (!Selected(options, field.Key)) continue;
            var manual = ManualValue(options, field.Key);
            var automatic = AutomaticValue(snapshot, kind, field.Key);
            var value = manual.Length > 0 ? manual + "（手动）" : automatic.Length > 0 ? automatic : "暂不可用";
            var label = field.Key == "Speed" && kind == "Network" ? "链路速度" : field.Label;
            if (field.Key == "Speed" && kind == "Memory") label = "配置速度";
            if (field.Key == "MemoryCapacity" && kind == "Memory") label = "已安装容量";
            fields.Add(label + " " + value);
        }
        return fields.Count == 0 ? "" : Kinds.First(item => item.Key == kind).Label + " · " + string.Join(" · ", fields);
    }

    public static string Format(PerformanceHardwareSnapshot? snapshot, AppConfig cfg) =>
        string.Join("\n", Kinds.Select(kind => FormatKind(snapshot, cfg, kind.Key)).Where(line => line.Length > 0));

    /// <summary>Active template fields, using the formatter's escaped-brace and case-insensitive rules.</summary>
    private static HashSet<string> ActiveTemplateFields(string template, out bool unmatchedOpening)
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        unmatchedOpening = false;
        for (var i = 0; i < template.Length; i++)
        {
            var character = template[i];
            if (i + 1 < template.Length && ((character == '{' && template[i + 1] == '{') ||
                (character == '}' && template[i + 1] == '}'))) { i++; continue; }
            if (character != '{') continue;
            var close = template.IndexOf('}', i + 1);
            if (close < 0) { unmatchedOpening = true; break; }
            fields.Add(template[(i + 1)..close].Trim());
            i = close;
        }
        return fields;
    }

    public static IReadOnlyList<string> MissingTemplateFields(string? template, IEnumerable<string> selectedKinds)
    {
        var fields = ActiveTemplateFields(template ?? "", out _);
        if (fields.Contains("hardware_details")) return Array.Empty<string>();
        var selected = selectedKinds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Kinds.Where(kind => selected.Contains(kind.Key))
            .Select(kind => kind.Key.ToLowerInvariant() + "_details")
            .Where(field => !fields.Contains(field)).ToArray();
    }

    /// <summary>Append only explicitly requested missing fields; never rewrite or truncate the user's template.</summary>
    public static bool TryAppendTemplateFields(string? template, IEnumerable<string> selectedKinds, int maximumLength,
        out string result, out IReadOnlyList<string> addedFields, out string? warning)
    {
        result = template ?? "";
        addedFields = Array.Empty<string>();
        warning = null;
        if (result.Length > maximumLength)
        {
            warning = $"现有模板超过 {maximumLength} 字符上限，请先在模板编辑器精简；原文已保留。";
            return false;
        }
        addedFields = MissingTemplateFields(result, selectedKinds);
        if (addedFields.Count == 0) return true;
        ActiveTemplateFields(result, out var unmatchedOpening);
        if (unmatchedOpening)
        {
            warning = "模板中有未配对的左花括号，请先在模板编辑器修正，再补充硬件字段。原文已保留。";
            return false;
        }
        var lineBreak = result.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var separator = result.Length == 0 || result.EndsWith('\n') || result.EndsWith('\r') ? "" : lineBreak;
        var addition = separator + string.Join(lineBreak, addedFields.Select(field => "{" + field + "}"));
        if ((long)result.Length + addition.Length > maximumLength)
        {
            warning = $"加入字段将超过模板的 {maximumLength} 字符上限，请先精简模板；不会截断现有文字。";
            return false;
        }
        result += addition;
        return true;
    }
}
