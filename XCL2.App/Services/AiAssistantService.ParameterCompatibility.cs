using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XCL2.App.Services;

public partial class AiAssistantService
{
    private string? _lastParameterPersistenceWarning;

    private Dictionary<string, JsonElement> BuildCompatiblePayload(ChatCompletionRequest request,
        AiRequestTarget target, int reservedOutput)
    {
        var omitted = new HashSet<string>(_parameterMemory.GetOmittedParameters(target.ProviderId, target.BaseUrl),
            StringComparer.Ordinal);
        // Keep the configured output/quota bound when a newer API rejects the legacy name.
        if (reservedOutput > 0 && omitted.Contains("max_tokens"))
        {
            if (omitted.Contains("max_completion_tokens"))
                throw new InvalidOperationException("该接口不支持可用的输出长度参数，无法保持当前 Token 上限或配额；请调整模型或接口设置后重试。");
            request.MaxTokens = null;
            request.MaxCompletionTokens = reservedOutput;
        }
        var root = JsonSerializer.SerializeToElement(request,
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        var payload = root.EnumerateObject().Where(property => !omitted.Contains(property.Name))
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        // stream_options has meaning only when an explicit streaming request is sent.
        if (!payload.TryGetValue("stream", out var stream) || stream.ValueKind != JsonValueKind.True)
            payload.Remove("stream_options");
        return payload;
    }

    private void RememberRejectedParameter(AiRequestTarget target, string parameter)
    {
        _parameterMemory.Remember(target.ProviderId, target.BaseUrl, parameter);
        var change = parameter switch
        {
            "reasoning_effort" => "已省略推理参数（off）",
            "max_tokens" => "已改用兼容的输出长度参数",
            _ => $"已移除 {parameter} 参数"
        };
        ToastService.ShowWarning($"{target.ProviderName} 不支持该参数，{change}并重试；此接口后续请求会记住此设置。");
        var warning = _parameterMemory.PersistenceWarning;
        if (warning != null && warning != _lastParameterPersistenceWarning)
        {
            _lastParameterPersistenceWarning = warning;
            ToastService.ShowWarning("接口兼容设置暂时无法保存，本次运行仍会记住；重启后可能需要重新适配。");
        }
    }
}
