using System.Text.Json;
using System.Text.Json.Serialization;
using XCL2.App.Models;

namespace XCL2.App.Services;

public partial class AiAssistantService
{
    private Dictionary<string, JsonElement> BuildCompatiblePayload(ChatCompletionRequest request,
        AiRequestTarget target, int reservedOutput, AiModelRequestProfile profile, AiReasoningLevel level,
        IReadOnlyList<AiParameterRule> learned, bool alternateSearch = false)
    {
        var error = AiModelParameterPolicy.Validate(profile);
        if (error != null) throw new InvalidOperationException("模型独立请求规则无效：" + error);
        var root = JsonSerializer.SerializeToElement(request,
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        var payload = root.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        var thinking = level != AiReasoningLevel.Off;
        if (profile.ThinkingProtocol != "inherit")
        {
            payload.Remove("reasoning_effort");
            if (profile.ThinkingProtocol == "thinking") payload["thinking"] = JsonSerializer.SerializeToElement(new { type = thinking ? "enabled" : "disabled" });
            else payload["enable_thinking"] = JsonSerializer.SerializeToElement(thinking);
        }
        AiModelParameterPolicy.Apply(payload, learned, thinking);
        // Explicit per-model settings override learned defaults; conflicts are surfaced, not overwritten.
        AiModelParameterPolicy.Apply(payload, profile.Rules, thinking);
        if (!payload.TryGetValue("stream", out var stream) || stream.ValueKind != JsonValueKind.True)
            payload.Remove("stream_options");
        if (reservedOutput > 0 && !payload.ContainsKey("max_tokens") && !payload.ContainsKey("max_completion_tokens"))
            throw new InvalidOperationException("模型适配不能移除当前输出长度或配额上限。");
        if (target.Protocol == AiApiProtocol.Anthropic)
            ConvertToAnthropic(payload, request, level, alternateSearch);
        else if (alternateSearch && request.Tools != null)
        {
            payload.Remove("tools");
            payload["web_search_options"] = JsonSerializer.SerializeToElement(new { search_context_size = "medium" });
        }
        return payload;
    }

    // Translate the launcher's text/attachment context to Messages without changing its local
    // permission/tool-marker protocol. No extra client-side execution capabilities are granted.
    private static void ConvertToAnthropic(Dictionary<string, JsonElement> payload,
        ChatCompletionRequest request, AiReasoningLevel level, bool alternateSearch)
    {
        payload.Remove("stream_options");
        payload.Remove("reasoning_effort");
        payload.Remove("enable_thinking");
        if (payload.Remove("max_completion_tokens", out var renamed)) payload["max_tokens"] = renamed;
        var maxTokens = payload.TryGetValue("max_tokens", out var limit) && limit.TryGetInt32(out var amount) ? amount : 4096;
        payload["max_tokens"] = JsonSerializer.SerializeToElement(maxTokens);
        payload["system"] = JsonSerializer.SerializeToElement(string.Join("\n\n",
            request.Messages.Where(m => m.Role == "system").Select(m => m.Content as string ?? "")));
        var messages = request.Messages.Where(m => m.Role != "system").Select(m => new
        {
            role = m.Role == "assistant" ? "assistant" : "user",
            content = AnthropicContent(m.Content)
        }).ToList();
        payload["messages"] = JsonSerializer.SerializeToElement(messages);
        payload.Remove("thinking");
        if (level is not (AiReasoningLevel.Off or AiReasoningLevel.Fast))
        {
            if (maxTokens <= 1024)
                throw new InvalidOperationException("Anthropic 深度思考至少需要 1024 个思考 Token。请提高输出上限或关闭深度思考。");
            var desired = level switch { AiReasoningLevel.Low => 1024, AiReasoningLevel.Medium => 2048, _ => 4096 };
            payload["thinking"] = JsonSerializer.SerializeToElement(new { type = "enabled", budget_tokens = Math.Min(Math.Min(desired, Math.Max(1024, maxTokens / 2)), maxTokens - 1) });
            // Extended thinking accepts only the provider's temperature=1 default.
            payload.Remove("temperature");
        }
        else if (payload.TryGetValue("temperature", out var temperature) && temperature.TryGetDouble(out var value))
            payload["temperature"] = JsonSerializer.SerializeToElement(Math.Clamp(value, 0, 1));
        if (request.Tools != null)
            payload["tools"] = alternateSearch
                ? JsonSerializer.SerializeToElement(new[] { new { type = "web_search_20260318", name = "web_search", max_uses = 5, allowed_callers = new[] { "direct" } } })
                : JsonSerializer.SerializeToElement(new[] { new { type = "web_search_20250305", name = "web_search", max_uses = 5 } });
    }

    private static object AnthropicContent(object content)
    {
        if (content is string text) return text;
        if (content is not IEnumerable<ChatContentPart> parts)
            throw new InvalidOperationException("Anthropic 接口不支持此附件内容格式。");
        var blocks = new List<object>();
        foreach (var part in parts)
        {
            if (part.Type == "text") { if (!string.IsNullOrEmpty(part.Text)) blocks.Add(new { type = "text", text = part.Text }); continue; }
            if (part.Type != "image_url" || part.ImageUrl == null) continue;
            var url = part.ImageUrl.Url;
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var separator = url.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
                if (separator < 6) throw new InvalidOperationException("图片附件必须使用有效的 base64 数据格式。");
                blocks.Add(new { type = "image", source = new { type = "base64", media_type = url[5..separator], data = url[(separator + 8)..] } });
            }
            else blocks.Add(new { type = "image", source = new { type = "url", url } });
        }
        return blocks;
    }

    public void ClearModelCompatibility(string modelKey)
    {
        var target = AiEndpointRouting.GetCandidates(_config, modelKey).FirstOrDefault();
        if (target != null)
        {
            _modelParameterMemory.Clear(AiModelParameterMemory.ModelScope(target));
            if (_modelParameterMemory.PersistenceWarning is { } warning) throw new System.IO.IOException(warning);
        }
    }
}
