using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>
/// Identifies an explicitly unsupported optional request field. A validation error about a
/// value, credentials or another field is not evidence that an optional field can be omitted.
/// </summary>
public static class AiUnsupportedParameterPolicy
{
    private static readonly string[] OptionalParameters =
    {
        "temperature", "reasoning_effort", "stream_options", "stream", "tools", "max_tokens", "max_completion_tokens"
    };

    /// <summary>Only these optional body fields may participate in compatibility fallback.</summary>
    public static bool IsOptionalParameter(string? parameter) => parameter != null
        && OptionalParameters.Contains(parameter.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a canonical top-level field actually present in this request, or null. The
    /// caller decides whether omitting that field is compatible with its local output limits.
    /// Never returns model, messages, authentication, or an arbitrary provider-supplied name.
    /// </summary>
    public static string? GetRejectedParameter(int status, string? detail, IEnumerable<string> sentParameters)
    {
        if (status is not (400 or 422) || string.IsNullOrWhiteSpace(detail) || detail.Length > 131072) return null;
        var sent = new HashSet<string>((sentParameters ?? Array.Empty<string>())
            .Where(IsOptionalParameter).Select(name => name.Trim()), StringComparer.OrdinalIgnoreCase);
        if (sent.Count == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(detail);
            return ReadError(document.RootElement, sent, 0);
        }
        catch (JsonException)
        {
            return RejectedInText(detail, sent);
        }
    }

    private static string? ReadError(JsonElement value, ISet<string> sent, int depth)
    {
        if (depth > 8) return null;
        if (value.ValueKind == JsonValueKind.String) return RejectedInText(value.GetString() ?? "", sent);
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var rejected = ReadError(item, sent, depth + 1);
                if (rejected != null) return rejected;
            }
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object) return null;

        var message = ReadString(value, "message") ?? ReadString(value, "msg")
            ?? ReadString(value, "error_description") ?? "";
        var code = (ReadString(value, "code") ?? "") + " " + (ReadString(value, "type") ?? "");
        if (IsUnrelatedFailure(message + " " + code)) return null;
        var parameter = ReadString(value, "param") ?? ReadString(value, "parameter") ?? ReadString(value, "field");
        if (parameter == null && value.TryGetProperty("loc", out var location) && location.ValueKind == JsonValueKind.Array)
            parameter = string.Join(".", location.EnumerateArray()
                .Where(part => part.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                .Select(part => part.ToString()));

        if (!string.IsNullOrWhiteSpace(parameter))
        {
            // An explicitly named foreign or unsent field must not be inferred from a
            // request echo or from a different parameter mentioned in the same message.
            var canonical = CanonicalErrorParameter(parameter);
            if (canonical == null || !sent.Contains(canonical)) return null;
            if (IsDefaultOnlyTemperature(canonical, message)) return canonical;
            if (IsValueValidation(message)) return null;
            var named = RejectedInText(message, new HashSet<string>(OptionalParameters, StringComparer.OrdinalIgnoreCase));
            if (named != null) return named == canonical ? canonical : null;
            if (IsUnsupportedParameterCode(code) || IsGenericParameterRejection(message)) return canonical;
        }
        else
        {
            var rejected = RejectedInText(message, sent);
            if (rejected != null) return rejected;
        }

        // Only traverse error containers, never an echoed body/request/metadata object.
        foreach (var name in new[] { "error", "errors", "detail", "details" })
            if (value.TryGetProperty(name, out var nested))
            {
                var rejected = ReadError(nested, sent, depth + 1);
                if (rejected != null) return rejected;
            }
        return null;
    }

    private static string? RejectedInText(string text, ISet<string> sent)
    {
        if (IsUnrelatedFailure(text)) return null;
        foreach (var parameter in OptionalParameters)
        {
            if (!sent.Contains(parameter)) continue;
            if (IsDefaultOnlyTemperature(parameter, text) && NamesParameter(text, parameter)) return parameter;
            if (IsValueValidation(text)) continue;
            var field = @"['""`]?" + Regex.Escape(parameter) + @"['""`]?";
            var boundaryField = @"(?<![a-z0-9_])" + field + @"(?![a-z0-9_])";
            // Rejection and the field name must be connected, not merely occur
            // somewhere in an error that also echoes the original request.
            if (Matches(text, @"\bunsupported\s+(?:(?:request\s+)?(?:parameter|argument|field|option)\s*)?[:=]?\s*" + boundaryField)
                || Matches(text, @"\b(?:unknown|unrecognized|unrecognised|unexpected)\s+(?:(?:request|keyword)\s+)?(?:parameter|argument|field|option)(?:\s+supplied)?\s*[:=]?\s*" + boundaryField)
                || Matches(text, boundaryField + @"\s*(?:(?:parameter|argument|field|option)\s*)?(?:(?:is|are)\s+)?(?:unsupported\b|not\s+(?:supported|allowed|permitted|accepted)\b)")
                || Matches(text, boundaryField + @"\s+is\s+(?:an?\s+)?(?:unknown|unrecognized|unrecognised|unexpected)\s+(?:parameter|argument|field|option)\b")
                || Matches(text, @"\b(?:does\s+not|doesn't|cannot|can't)\s+support\s+(?:(?:the|a|an|non-default|non\s+default)\s+)*(?:(?:parameter|argument|field|option)\s+)?" + boundaryField)
                || Matches(text, @"(?:不支持|未识别|未知)(?:的)?\s*(?:非默认)?\s*(?:参数|字段)?\s*[:：]?\s*" + boundaryField)
                || Matches(text, boundaryField + @"\s*(?:参数|字段)?\s*(?:不受支持|不支持|不被允许)"))
                return parameter;
        }
        return null;
    }

    private static bool IsDefaultOnlyTemperature(string parameter, string text) => parameter == "temperature"
        && (Matches(text, @"\bonly\s+(?:the\s+)?default\s*(?:\([^)]{0,24}\)\s*)?(?:value\s+)?(?:is\s+)?supported\b")
            || Matches(text, @"\bonly\s+(?:the\s+)?default\s+value\s*(?:\([^)]{0,24}\)\s*)?(?:is\s+)?supported\b")
            || Matches(text, @"\b(?:does\s+not|doesn't)\s+support\s+non[-\s]default\s+['""`]?temperature\b")
            || Matches(text, @"(?:仅|只)支持默认(?:值)?"));

    private static bool NamesParameter(string text, string parameter) =>
        Matches(text, @"(?<![a-z0-9_])" + Regex.Escape(parameter) + @"(?![a-z0-9_])");

    private static bool IsUnsupportedParameterCode(string code) => code.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Replace('-', '_').ToLowerInvariant()).Any(part => part is
            "unsupported_parameter" or "unsupported_param" or "unsupported_argument" or "parameter_not_supported"
            or "unknown_parameter" or "unknown_argument" or "unrecognized_parameter" or "unrecognised_parameter"
            or "unrecognized_request_argument" or "unrecognised_request_argument" or "extra_forbidden");

    private static bool IsGenericParameterRejection(string text) =>
        Matches(text, @"\b(?:this\s+)?(?:parameter|argument|field|option)\s+(?:is\s+)?(?:unsupported|not\s+(?:supported|allowed|permitted|accepted))\b")
        || Matches(text, @"\b(?:does\s+not|doesn't)\s+support\s+(?:this|the\s+specified)\s+(?:parameter|argument|field|option)\b")
        || Matches(text, @"\b(?:unknown|unrecognized|unrecognised|unsupported)\s+(?:request\s+)?(?:parameter|argument|field|option)\s*[.!]?\s*$")
        || Matches(text, @"(?:该|此)(?:参数|字段)(?:不支持|不受支持|不被允许)");

    private static bool IsValueValidation(string text) => Matches(text,
        @"\b(?:invalid\s+(?:type|value)|unsupported\s+value|not\s+supported\s+value|value\s+(?:is\s+)?not\s+supported|out\s+of\s+range|outside\s+(?:the\s+)?(?:range|\[)|must\s+be\s+(?:a|an|of\s+type)|expected\s+(?:a|an|type)|greater\s+than|less\s+than|between\s+[-+]?\d|allowed\s+values)\b")
        || Matches(text, @"(?:类型错误|值无效|超出范围|取值范围|必须为数字|必须为字符串)");

    private static bool IsUnrelatedFailure(string text) => Matches(text,
        @"\b(?:unauthorized|unauthenticated|authentication|authorization|invalid_api_key|api[_\s-]?key|insufficient_quota|quota|billing|insufficient\s+balance|permission|access\s+denied|model_not_found)\b")
        || Matches(text, @"(?:鉴权失败|认证失败|密钥无效|余额不足|额度不足|无权访问|模型不存在)");

    private static string? CanonicalErrorParameter(string parameter)
    {
        var name = parameter.Trim().Trim('\'', '"', '`').ToLowerInvariant();
        foreach (var prefix in new[] { "body.", "request.", "payload.", "params." })
            if (name.StartsWith(prefix, StringComparison.Ordinal)) { name = name[prefix.Length..]; break; }
        // include_usage is part of the optional stream_options container. A malformed
        // nested tool/function schema, by contrast, does not justify dropping all tools.
        if (name.StartsWith("stream_options.", StringComparison.Ordinal)
            || name.StartsWith("stream_options[", StringComparison.Ordinal)) return "stream_options";
        return OptionalParameters.FirstOrDefault(candidate => candidate == name);
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static bool Matches(string text, string pattern)
    {
        try { return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
