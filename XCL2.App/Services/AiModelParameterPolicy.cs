using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>Allow-listed transformations, not arbitrary JSON patches or executable templates.</summary>
public static class AiModelParameterPolicy
{
    public static AiModelRequestProfile Profile(AiAssistantConfig config, string key) =>
        config.ModelRequestProfiles != null && config.ModelRequestProfiles.TryGetValue(key, out var value)
            && value != null ? value : new();

    public static string? Validate(AiModelRequestProfile profile)
    {
        if (profile == null) return "请求规则不能为空。";
        if (profile.Documentation == null || profile.Documentation.Length > 32000) return "请求文档最多 32000 字符。";
        if (profile.ThinkingProtocol is not ("inherit" or "thinking" or "enable_thinking")) return "未知思考协议。";
        if (profile.SupportedEfforts == null || profile.SupportedEfforts.Length > 6 ||
            profile.SupportedEfforts.Any(e => e is not ("minimal" or "low" or "medium" or "high" or "xhigh" or "max")))
            return "推理档位列表无效。";
        if (profile.Rules == null || profile.Rules.Count > 24) return "参数规则最多 24 条。";
        foreach (var rule in profile.Rules)
        {
            if (rule == null || rule.When is not ("any" or "thinking" or "off")) return "规则条件必须是 any、thinking 或 off。";
            if (rule.Action == "omit" && rule.Parameter is "temperature" or "reasoning_effort" or "stream_options") continue;
            if (rule.Action == "set" && rule.Parameter == "temperature" &&
                double.TryParse(rule.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && double.IsFinite(t) && t >= 0 && t <= 2) continue;
            if (rule.Action == "set" && rule.Parameter == "stream" && rule.Value == "false") continue;
            if (rule.Action == "rename" && rule.Parameter == "max_tokens" && rule.Value == "max_completion_tokens") continue;
            return "不支持该参数规则；不能修改 model、messages、认证信息或移除输出上限。";
        }
        if (profile.Rules.GroupBy(r => (r.Parameter, r.When)).Any(g => g.Count() > 1)) return "同一参数和条件存在重复规则。";
        return null;
    }

    public static AiReasoningLevel ResolveLevel(AiReasoningLevel requested, AiModelRequestProfile profile)
    {
        if (requested == AiReasoningLevel.Off || profile.SupportedEfforts.Length == 0) return requested;
        var levels = new[] { AiReasoningLevel.Fast, AiReasoningLevel.Low, AiReasoningLevel.Medium,
            AiReasoningLevel.High, AiReasoningLevel.VeryHigh, AiReasoningLevel.Max };
        var index = Array.IndexOf(levels, requested);
        var supported = levels.Where(l => profile.SupportedEfforts.Contains(AiReasoningPolicy.GetReasoningEffort(l))).ToArray();
        return supported.LastOrDefault(l => Array.IndexOf(levels, l) <= index,
            supported.FirstOrDefault(AiReasoningLevel.Off));
    }

    public static void Apply(Dictionary<string, JsonElement> payload, IEnumerable<AiParameterRule> rules, bool thinking)
    {
        foreach (var rule in rules.Where(r => r.When == "any" || r.When == (thinking ? "thinking" : "off"))
                     .OrderBy(r => r.When == "any" ? 0 : 1))
        {
            if (rule.Action == "omit") payload.Remove(rule.Parameter);
            else if (rule.Action == "rename")
            {
                if (payload.Remove(rule.Parameter, out var tokens)) payload[rule.Value] = tokens;
            }
            else if (rule.Parameter == "temperature")
                payload[rule.Parameter] = JsonSerializer.SerializeToElement(double.Parse(rule.Value, CultureInfo.InvariantCulture));
            else if (rule.Parameter == "stream") payload[rule.Parameter] = JsonSerializer.SerializeToElement(false);
        }
    }

    public static AiParameterRule? Learn(int status, string detail, IReadOnlyDictionary<string, JsonElement> sent)
    {
        if (status is not (400 or 422) || detail.Length > 131072) return null;
        // Inspect only error messages, not echoed request bodies or arbitrary metadata.
        var message = ErrorMessage(detail);
        if (Regex.IsMatch(message, @"api.?key|unauthorized|quota|billing|permission|model_not_found", RegexOptions.IgnoreCase)) return null;
        var match = Regex.Match(message,
            @"\btemperature\b\s*[:=]?\s*(?:must\s+be|must\s+equal|is\s+required\s+to\s+be)\s+(?<value>[0-9]+(?:\.[0-9]+)?)(?![\d.])\s+when\s+thinking\s+is\s+enabled\b",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        if (match.Success && !NamesForeignParameter(detail, "temperature") &&
            double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) &&
            t >= 0 && t <= 2 && (!sent.TryGetValue("temperature", out var old) || !old.TryGetDouble(out var previous) || previous != t))
            return new() { Parameter = "temperature", Action = "set", Value = t.ToString(CultureInfo.InvariantCulture) };
        var rejected = AiUnsupportedParameterPolicy.GetRejectedParameter(status, detail, sent.Keys);
        return rejected switch
        {
            "temperature" or "reasoning_effort" or "stream_options" => new() { Parameter = rejected },
            "stream" => sent["stream"].ValueKind == JsonValueKind.True ? new() { Parameter = "stream", Action = "set", Value = "false" } : null,
            "max_tokens" => new() { Parameter = "max_tokens", Action = "rename", Value = "max_completion_tokens" },
            // Dropping tools would silently remove the requested capability.
            _ => null
        };
    }

    private static bool NamesForeignParameter(string detail, string expected)
    {
        try
        {
            using var doc = JsonDocument.Parse(detail);
            return Check(doc.RootElement, 0);
        }
        catch (JsonException) { return false; }
        bool Check(JsonElement value, int depth)
        {
            if (depth > 8) return false;
            if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(v => Check(v, depth + 1));
            if (value.ValueKind != JsonValueKind.Object) return false;
            foreach (var key in new[] { "param", "parameter", "field" })
                if (value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String)
                {
                    var name = field.GetString()?.Trim();
                    if (name != expected && name != "body." + expected) return true;
                }
            if (value.TryGetProperty("loc", out var location) && location.ValueKind == JsonValueKind.Array)
            {
                var name = string.Join(".", location.EnumerateArray().Select(x => x.ToString()));
                if (name != expected && name != "body." + expected) return true;
            }
            foreach (var key in new[] { "error", "errors", "detail", "details" })
                if (value.TryGetProperty(key, out var nested) && Check(nested, depth + 1)) return true;
            return false;
        }
    }

    public static string ErrorMessage(string detail)
    {
        try
        {
            using var doc = JsonDocument.Parse(detail);
            return Read(doc.RootElement, 0);
        }
        catch (JsonException) { return detail.Length <= 2000 ? detail : detail[..2000]; }
        static string Read(JsonElement e, int depth)
        {
            if (depth > 8) return "";
            if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
            if (e.ValueKind == JsonValueKind.Array) return string.Join(" ", e.EnumerateArray().Take(16).Select(x => Read(x, depth + 1)));
            if (e.ValueKind != JsonValueKind.Object) return "";
            return string.Join(" ", new[] { "message", "msg", "error_description", "error", "errors", "detail", "details" }
                .Where(k => e.TryGetProperty(k, out _)).Select(k => Read(e.GetProperty(k), depth + 1)));
        }
    }
}
