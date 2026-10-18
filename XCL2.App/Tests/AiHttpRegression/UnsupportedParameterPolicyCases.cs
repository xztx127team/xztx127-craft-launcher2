using System.Text.Json;
using XCL2.App.Services;

internal static class UnsupportedParameterPolicyCases
{
    internal static int Run()
    {
        var assertions = 0;
        var sent = new[] { "temperature", "reasoning_effort", "stream_options", "stream", "tools", "max_tokens", "max_completion_tokens" };
        void Check(int status, string? body, string? expected, string[]? actualSent = null)
        {
            assertions++;
            var actual = AiUnsupportedParameterPolicy.GetRejectedParameter(status, body, actualSent ?? sent);
            if (actual != expected) throw new InvalidOperationException(
                $"Unsupported parameter policy: expected {expected ?? "none"}, got {actual ?? "none"}, status {status}: {body}");
        }
        string Error(string message, string? param = null, string? code = null) =>
            JsonSerializer.Serialize(new { error = new { message, param, code } });

        Check(400, "claude-sonnet-5-5 does not support non-default temperature", "temperature");
        Check(400, Error("Unsupported parameter: 'temperature' is not supported with this model.", "temperature"), "temperature");
        Check(400, Error("Unsupported value: 'temperature' does not support 0.7 with this model. Only the default (1) value is supported.", "temperature", "unsupported_value"), "temperature");
        Check(422, Error("Only default(1) value supported", "temperature"), "temperature");
        Check(400, Error("Unknown parameter: stream_options"), "stream_options");
        Check(400, Error("Unrecognized request argument supplied: stream_options"), "stream_options");
        Check(400, Error("Got an unexpected keyword argument 'stream_options'"), "stream_options");
        Check(400, Error("'stream_options' is an unknown parameter"), "stream_options");
        Check(400, Error("reasoning_effort is unsupported"), "reasoning_effort");
        Check(400, Error("This parameter is not supported by this model", "reasoning_effort"), "reasoning_effort");
        Check(400, Error("Unexpected argument", "stream", "unsupported_parameter"), "stream");
        Check(422, "{\"detail\":[{\"loc\":[\"body\",\"stream_options\"],\"msg\":\"Extra inputs are not permitted\",\"type\":\"extra_forbidden\"}]}", "stream_options");
        Check(400, Error("This field is not supported", "stream_options.include_usage"), "stream_options");
        Check(400, Error("tools is not supported with this model"), "tools");
        Check(400, Error("Unsupported parameter: max_tokens; use max_completion_tokens instead."), "max_tokens");
        Check(400, Error("Unknown parameter: max_completion_tokens"), "max_completion_tokens");
        Check(400, "不支持非默认 temperature", "temperature");
        Check(400, Error("仅支持默认值", "temperature"), "temperature");
        Check(400, "{\"errors\":[{\"message\":\"temperature is out of range\",\"param\":\"temperature\"},{\"message\":\"stream_options is not supported\",\"param\":\"stream_options\"}]}", "stream_options");

        // Only the actual sent optional field can be retried. Never infer an auth,
        // required field, malformed tool schema or an unknown parameter from a 400.
        Check(400, Error("Unsupported parameter: temperature"), null, new[] { "stream" });
        Check(400, Error("Unsupported parameter: temperature"), null, Array.Empty<string>());
        Check(400, Error("Unsupported parameter: model", "model", "unsupported_parameter"), null);
        Check(400, Error("messages is not supported", "messages"), null);
        Check(400, Error("Unknown parameter: Authorization", "Authorization"), null);
        Check(400, Error("Unknown parameter: top_k", "top_k"), null);
        Check(400, Error("Unsupported parameter: temperature", "top_k"), null);
        Check(400, Error("Unknown parameter: stream_options", "temperature"), null);
        Check(400, Error("This field is not supported", "tools.0.function.parameters.temperature"), null);
        Check(400, Error("Unsupported parameter: mytemperature"), null);
        Check(400, Error("temperature is invalid"), null);
        Check(422, Error("temperature must be a number", "temperature", "invalid_type"), null);
        Check(400, Error("temperature must be between 0 and 2", "temperature", "invalid_value"), null);
        Check(400, Error("temperature is not supported outside the range [0, 2]", "temperature"), null);
        Check(400, Error("Unsupported value for temperature: 100; allowed values are between 0 and 2", "temperature", "unsupported_value"), null);
        Check(400, Error("temperature is out of range", "temperature", "unsupported_parameter"), null);
        Check(400, Error("reasoning_effort must be one of low, medium, high", "reasoning_effort", "literal_error"), null);
        Check(400, Error("Invalid API key: temperature is unsupported", "temperature", "unsupported_parameter"), null);
        Check(400, Error("Unauthorized: unsupported parameter temperature"), null);
        Check(400, Error("Billing quota exhausted; model does not support temperature"), null);
        Check(400, "{\"error\":{\"message\":\"Invalid request\"},\"request\":{\"temperature\":0.7,\"message\":\"Unsupported parameter: temperature\"}}", null);
        Check(400, Error("Unknown parameter: top_k. Request used temperature and stream_options."), null);
        Check(400, Error("Only default(1) value supported"), null);
        Check(400, Error("Only default(1) value supported", "max_tokens"), null);
        Check(400, Error("Request body malformed"), null);
        foreach (var status in new[] { 200, 401, 403, 404, 408, 429, 500, 503 })
            Check(status, Error("Unsupported parameter: temperature"), null);
        Check(400, null, null);
        Check(422, "", null);
        Check(400, "{\"error\":{\"message\":\"Unsupported parameter: stream_options\"}}", "stream_options", new[] { "stream_options", "model", "messages" });

        assertions++;
        if (AiUnsupportedParameterPolicy.IsOptionalParameter("model") || !AiUnsupportedParameterPolicy.IsOptionalParameter("max_completion_tokens"))
            throw new InvalidOperationException("Optional parameter allowlist excludes required/auth fields and includes output-token aliases");
        return assertions;
    }
}
