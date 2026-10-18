using System;
using System.Linq;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>Failure categories deliberately separate credential exhaustion from model availability.</summary>
internal static class AiRequestExecutionPolicy
{
    internal enum FailureKind { Request, Credential, Model, Transient }

    internal static FailureKind Classify(int status, string detail)
    {
        // A failing gateway can echo quota/auth terminology from an upstream response.
        // Its 5xx status is still transient, and must not burn through every API key.
        if (status is 408 or 425 || status >= 500) return FailureKind.Transient;
        if (status is 401 or 402 or 429) return FailureKind.Credential;
        if ((status is 0 or 400 or 403 or 422) && Contains(detail,
            "invalid_api_key", "api_key_invalid", "invalid api key", "incorrect api key", "invalid authentication",
            "authentication_error", "token_expired", "expired token", "invalid token", "密钥无效", "无效的密钥",
            "insufficient_quota", "quota_exceeded", "quota_exhausted", "billing_hard_limit_reached", "credit_limit_exceeded",
            "insufficient_balance", "quota exceeded", "insufficient balance", "balance is insufficient",
            "insufficient credits", "credit balance", "credits exhausted", "rate_limit_exceeded", "rate limit exceeded",
            "额度不足", "配额耗尽", "余额不足", "余额已耗尽", "配额已用完")) return FailureKind.Credential;
        // A 400 mentioning 'model' is not enough: unsupported parameters often name the model too.
        if ((status is 400 or 404 or 422 || status == 0) && Contains(detail,
            "model_not_found", "invalid_model", "unknown model", "model does not exist",
            "model not found", "no available model", "no endpoints found", "model is not available",
            "model is unavailable", "model is currently unavailable", "model has been disabled", "model has been deprecated",
            "model is disabled", "model is not supported", "not a valid model id", "invalid model id",
            "模型不存在", "模型不可用", "不支持的模型"))
            return FailureKind.Model;
        if ((status is 400 or 404 or 422 || status == 0) && Contains(detail, "unsupported model")
            && !Contains(detail, "parameter", "argument", "temperature", "top_p", "max_tokens", "max_completion_tokens", "reasoning_effort", "stream_options"))
            return FailureKind.Model;
        if (status == 404) return FailureKind.Model;
        return FailureKind.Request;
    }

    internal static int StreamErrorStatus(JsonElement root)
    {
        var error = root.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.Object ? value : root;
        foreach (var name in new[] { "status", "status_code", "code" })
        {
            if (!error.TryGetProperty(name, out var status)) continue;
            if (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var number)) return number;
            if (status.ValueKind == JsonValueKind.String && int.TryParse(status.GetString(), out var parsed)) return parsed;
        }
        var text = error.ToString();
        if (Contains(text, "invalid_api_key", "authentication_error")) return 401;
        if (Contains(text, "rate_limit", "insufficient_quota")) return 429;
        if (Contains(text, "invalid_request_error", "unsupported_parameter", "invalid_parameter")) return 400;
        if (Contains(text, "server_error", "internal_error", "overloaded_error", "service_unavailable")) return 503;
        return 0;
    }

    private static bool Contains(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}
