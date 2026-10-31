using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>单一接口的请求目标；密钥只用于 HTTP 鉴权，禁止作为日志或模型身份。</summary>
public sealed class AiRequestTarget
{
    public string ModelKey { get; init; } = "";
    public AiApiProtocol Protocol { get; init; } = AiApiProtocol.OpenAI;
    public string ModelId { get; init; } = "";
    public string ProviderId { get; init; } = "";
    public string ProviderName { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public IReadOnlyList<string> ApiKeys { get; init; } = Array.Empty<string>();
    public AiModelTier Tier { get; init; }
    public bool SupportsFastMode { get; init; }
}

public static class AiEndpointRouting
{
    // A redirect is not approval to disclose a conversation, attachment or provider key
    // to another endpoint. 307/308 can resend the POST body and custom auth headers.
    internal static System.Net.Http.HttpClient CreateHttpClient(TimeSpan timeout) =>
        new(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false }) { Timeout = timeout };

    /// <summary>只在配置读写时迁移；不会获取凭据，也不会开启跨接口切换。</summary>
    public static void NormalizeConfig(AiAssistantConfig config)
    {
        // Missing legacy values keep Build; invalid imported values never grant execution.
        if (!Enum.IsDefined(typeof(AiWorkMode), config.WorkMode)) config.WorkMode = AiWorkMode.Plan;
        if (!Enum.IsDefined(typeof(AiApiProtocol), config.ApiProtocol)) config.ApiProtocol = AiApiProtocol.OpenAI;
        config.ApiKeys = CleanKeys(config.ApiKeys).ToList();
        config.ApiEndpoints ??= new();
        config.ApiEndpoints.RemoveAll(e => e == null);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in config.ApiEndpoints)
        {
            endpoint.Id = endpoint.Id?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(endpoint.Id) || (string.Equals(endpoint.Id, "builtin", StringComparison.OrdinalIgnoreCase) || string.Equals(endpoint.Id, "custom", StringComparison.OrdinalIgnoreCase)) || !ids.Add(endpoint.Id))
            {
                endpoint.Id = Guid.NewGuid().ToString("N");
                ids.Add(endpoint.Id);
            }
            endpoint.Name = endpoint.Name?.Trim() ?? "自定义接口";
            endpoint.BaseUrl = endpoint.BaseUrl?.Trim() ?? "";
            if (!Enum.IsDefined(typeof(AiApiProtocol), endpoint.Protocol)) endpoint.Protocol = AiApiProtocol.OpenAI;
            endpoint.ApiKeys = CleanKeys(endpoint.ApiKeys).ToList();
        }
        config.CustomModels ??= new();
        config.CustomModels.RemoveAll(m => m == null || string.IsNullOrWhiteSpace(m.Id));
        foreach (var model in config.CustomModels)
        {
            model.Id = model.Id.Trim();
            model.ProviderId = AiModelIds.GetProviderId(model);
        }
        config.ModelAccounting ??= new();
        config.ReasoningLevel ??= config.EnableDeepThinking ? AiReasoningLevel.High : AiReasoningLevel.Off;
        if (!Enum.IsDefined(typeof(AiReasoningLevel), config.ReasoningLevel.Value)) config.ReasoningLevel = AiReasoningLevel.Off;
        config.EnableDeepThinking = config.ReasoningLevel is not (AiReasoningLevel.Off or AiReasoningLevel.Fast);
        string Selection(string value) => AiModelIds.FindInCatalog(config, value)?.ModelKey ?? value;
        config.SelectedModel = Selection(config.SelectedModel);
        config.NormalModelId = Selection(config.NormalModelId);
        config.ExpertModelId = Selection(config.ExpertModelId);
        // 旧计费条目只属于原来的默认接口；不能复制到每个同名模型后串账。
        foreach (var entry in config.ModelAccounting.ToList())
        {
            if (entry.Value == null) { config.ModelAccounting.Remove(entry.Key); continue; }
            if (AiModelIds.TrySplitModelKey(entry.Key, out _, out _)) continue;
            var model = AiModelIds.FindInCatalog(config, entry.Key);
            if (model != null)
            {
                if (!config.ModelAccounting.Keys.Any(k => string.Equals(k, model.ModelKey, StringComparison.OrdinalIgnoreCase)))
                    config.ModelAccounting[model.ModelKey] = entry.Value.Clone();
                config.ModelAccounting.Remove(entry.Key); // 防止后续切换模式把旧计费重放到同名接口。
            }
        }
    }

    public static IReadOnlyList<string> CleanKeys(IEnumerable<string>? keys) =>
        (keys ?? Array.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim()).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>统一解析接口地址；完整的聊天/模型列表地址也可以作为输入，查询参数始终留在路径之后。</summary>
    public static string NormalizeBaseUrl(string? baseUrl) => BuildEndpointUrl(baseUrl, "");

    public static string BuildChatCompletionsUrl(string? baseUrl) => BuildEndpointUrl(baseUrl, "/chat/completions");

    public static string BuildModelsUrl(string? baseUrl) => BuildEndpointUrl(baseUrl, "/models");

    public static string BuildMessagesUrl(string? baseUrl) => BuildEndpointUrl(baseUrl, "/messages", anthropic: true);
    public static string BuildModelsUrl(string? baseUrl, AiApiProtocol protocol) =>
        BuildEndpointUrl(baseUrl, "/models", protocol == AiApiProtocol.Anthropic);
    public static string BuildCompletionUrl(AiRequestTarget target) => target.Protocol == AiApiProtocol.Anthropic
        ? BuildMessagesUrl(target.BaseUrl) : BuildChatCompletionsUrl(target.BaseUrl);

    public static void ApplyAuthentication(System.Net.Http.HttpRequestMessage request, string apiKey, AiApiProtocol protocol)
    {
        if (protocol == AiApiProtocol.Anthropic)
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Add("x-api-key", apiKey.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    private static string BuildEndpointUrl(string? baseUrl, string suffix, bool anthropic = false)
    {
        var value = (baseUrl ?? "").Trim();
        if (value.IndexOfAny(new[] { '\r', '\n' }) >= 0
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("AI 接口地址无效，请填写 http/https 地址，并将 API 密钥填写在密钥栏。", nameof(baseUrl));

        var path = uri.AbsolutePath.TrimEnd('/');
        foreach (var knownSuffix in new[] { "/chat/completions", "/models", "/messages" })
            if (path.EndsWith(knownSuffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^knownSuffix.Length].TrimEnd('/');
                break;
            }
        if (anthropic && path.Length == 0) path = "/v1";
        // Preserve custom API prefixes, escaped path segments and required query parameters.
        // A fragment is local browser navigation and is never part of an HTTP API request.
        return uri.GetLeftPart(UriPartial.Authority) + path + suffix + uri.Query;
    }

    /// <summary>只针对密钥/额度错误切换同一接口的备用密钥；权限或参数错误不会遍历密钥。</summary>
    public static bool ShouldRetryWithNextKey(int statusCode, string? responseBody) =>
        AiRequestExecutionPolicy.Classify(statusCode, responseBody ?? "") == AiRequestExecutionPolicy.FailureKind.Credential;

    public static IReadOnlyList<AiRequestTarget> GetCandidates(AiAssistantConfig config, string modelKeyOrId)
    {
        var primary = AiModelIds.FindInCatalog(config, modelKeyOrId);
        if (primary == null) return Array.Empty<AiRequestTarget>();
        var models = new List<AiModelDefinition> { primary };
        if (config.EnableEndpointFailover)
        {
            var alternatives = AiModelIds.GetCatalog(config)
                .Where(m => !string.Equals(m.ProviderId, primary.ProviderId, StringComparison.OrdinalIgnoreCase))
                .Where(m => string.Equals(m.Id, primary.Id, StringComparison.OrdinalIgnoreCase) || m.Tier == primary.Tier)
                .OrderBy(m => !string.Equals(m.Id, primary.Id, StringComparison.OrdinalIgnoreCase));
            models.AddRange(alternatives);
        }
        return models.Select(m => CreateTarget(config, m)).Where(t => t != null)
            .Cast<AiRequestTarget>().ToList();
    }

    private static AiRequestTarget? CreateTarget(AiAssistantConfig config, AiModelDefinition model)
    {
        string url;
        var protocol = AiApiProtocol.OpenAI;
        IReadOnlyList<string> keys;
        if (model.ProviderId == AiModelIds.BuiltInProviderId)
        {
            url = BuiltInAiDefaults.BaseUrl;
            keys = Array.Empty<string>(); // 候选创建绝不发起内置密钥网络请求。
        }
        else if (model.ProviderId == AiModelIds.CustomProviderId)
        {
            url = config.BaseUrl;
            protocol = config.ApiProtocol;
            keys = CleanKeys(new[] { config.ApiKey }.Concat(config.ApiKeys ?? new()));
        }
        else
        {
            var endpoint = config.ApiEndpoints?.FirstOrDefault(e => e != null
                && string.Equals(e.Id, model.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (endpoint != null)
            {
                if (!endpoint.Enabled) return null;
                url = endpoint.BaseUrl;
                protocol = endpoint.Protocol;
                keys = CleanKeys(endpoint.ApiKeys);
            }
            else if (model.ProviderId.StartsWith("legacy-", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(model.ProviderBaseUrl))
            {
                url = model.ProviderBaseUrl;
                keys = CleanKeys(new[] { model.ProviderApiKey ?? "" });
            }
            else return null;
        }
        if (string.IsNullOrWhiteSpace(url)) return null;
        return new AiRequestTarget
        {
            ModelId = model.Id, ModelKey = model.ModelKey, ProviderId = model.ProviderId, Protocol = protocol,
            ProviderName = model.ProviderName, BaseUrl = url.Trim(), Tier = model.Tier,
            ApiKeys = config.EnableApiKeyFailover ? keys : keys.Take(1).ToArray(),
            SupportsFastMode = model.SupportsFastMode
        };
    }

    public static IReadOnlyList<string> ResolveApiKeys(AiRequestTarget target) =>
        target.ProviderId == AiModelIds.BuiltInProviderId
            ? CleanKeys(new[] { BuiltInAiDefaults.GetApiKey() }) : CustomApiKeys(target);

    public static async Task<IReadOnlyList<string>> ResolveApiKeysAsync(AiRequestTarget target, CancellationToken ct = default)
    {
        if (target.ProviderId != AiModelIds.BuiltInProviderId) return CustomApiKeys(target);
        return CleanKeys(new[] { await BuiltInAiDefaults.GetApiKeyAsync(ct).ConfigureAwait(false) });
    }

    // Local OpenAI-compatible servers may explicitly use no authentication. Never borrow a
    // different endpoint's key to fill this gap; an empty key means omit Authorization.
    private static IReadOnlyList<string> CustomApiKeys(AiRequestTarget target) =>
        target.ApiKeys.Count > 0 ? target.ApiKeys : new[] { "" };
}
