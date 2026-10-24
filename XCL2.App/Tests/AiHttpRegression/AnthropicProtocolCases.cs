using System.Net;
using System.Text;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class AnthropicProtocolCases
{
    internal static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool condition, string message)
        { count++; if (!condition) throw new InvalidOperationException("Anthropic: " + message); }
        var directories = new List<string>();
        AiAssistantConfig Config() => new()
        {
            UseCustomApiKey = true, ApiProtocol = AiApiProtocol.Anthropic,
            BaseUrl = "https://claude.example", ApiKey = "own-secret", EnableApiKeyFailover = false,
            WorkMode = AiWorkMode.Chat, RoutingMode = AiRoutingMode.SpecificModel, SelectedModel = "claude-test",
            CustomModels = new() { new() { Id = "claude-test", ProviderId = "custom" } },
            StreamResponses = false, ReasoningLevel = AiReasoningLevel.Off, TokenSaverMode = false
        };
        AiAssistantService Service(AiAssistantConfig config, RecordingHandler handler)
        {
            var directory = Path.Combine(Path.GetTempPath(), "xcl-anthropic-" + Guid.NewGuid().ToString("N"));
            directories.Add(directory);
            return new(config, directory, new HttpClient(handler));
        }
        try
        {
            Check(new AiAssistantConfig().ApiProtocol == AiApiProtocol.OpenAI, "old settings retain OpenAI");
            Check(AiEndpointRouting.BuildMessagesUrl("https://claude.example/v1/messages?tenant=one#ignored")
                == "https://claude.example/v1/messages?tenant=one", "full Messages URL normalized once");
            Check(AiEndpointRouting.BuildModelsUrl("https://claude.example", AiApiProtocol.Anthropic)
                == "https://claude.example/v1/models", "bare Anthropic host uses /v1");
            var handler = new RecordingHandler(_ => Success());
            using (var service = Service(Config(), handler))
            {
                var result = await service.TestModelAsync("claude-test");
                Check(result.Ok && handler.Requests.Count == 1, "native nonstream answer accepted");
                var request = handler.Requests[0];
                Check(request.Url == "https://claude.example/v1/messages" && request.ApiKey == "own-secret"
                    && request.Version == "2023-06-01" && request.Bearer == null, "native URL and scoped auth headers");
                Check(request.Body.GetProperty("system").GetString()!.Contains("连通性测试")
                    && request.Body.GetProperty("messages").EnumerateArray().All(m => m.GetProperty("role").GetString() != "system"),
                    "system context is moved to top-level field");
                Check(request.Body.GetProperty("max_tokens").GetInt32() == 4096
                    && !request.Body.TryGetProperty("stream_options", out _) && !request.Body.TryGetProperty("reasoning_effort", out _),
                    "required output cap and no OpenAI-only fields");
            }
            var streamConfig = Config(); streamConfig.StreamResponses = true;
            handler = new RecordingHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Stream, Encoding.UTF8, "text/event-stream") });
            using (var service = Service(streamConfig, handler))
            {
                var session = service.CreateNewSession();
                var reply = await service.SendMessageAsync(session, "hello");
                Check(reply.Content == "hello" && !reply.Content.Contains("private"), "native text and reasoning stay separate");
                Check(session.UsageRequests.Single().InputTokens == 15 && session.UsageRequests.Single().OutputTokens == 2,
                    "message_start plus message_delta usage includes cache tokens");
            }
            handler = new RecordingHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(
                Stream.Replace("\"end_turn\"", "\"max_tokens\""), Encoding.UTF8, "text/event-stream") });
            using (var service = Service(streamConfig, handler))
            {
                var session = service.CreateNewSession();
                bool interrupted = false;
                try { await service.SendMessageAsync(session, "hello"); }
                catch (IOException) { interrupted = true; }
                Check(interrupted && handler.Requests.Count == 1 && session.Messages.Last().Content.Contains("hello"),
                    "truncated native stream preserves output and never replays the request");
            }
            var namedConfig = Config(); namedConfig.ApiProtocol = AiApiProtocol.OpenAI;
            namedConfig.ApiEndpoints.Add(new() { Id = "named", Name = "Claude", Protocol = AiApiProtocol.Anthropic,
                BaseUrl = "https://named.example/v1", ApiKeys = new() { "named-secret" } });
            namedConfig.CustomModels.Add(new() { Id = "claude-test", ProviderId = "named" });
            handler = new RecordingHandler(_ => Success());
            using (var service = Service(namedConfig, handler))
            {
                var result = await service.TestModelAsync(AiModelIds.BuildModelKey("named", "claude-test"));
                Check(result.Ok && handler.Requests.Single().Url == "https://named.example/v1/messages"
                    && handler.Requests.Single().ApiKey == "named-secret", "named provider owns protocol and credentials");
            }
            foreach (var protocol in new[] { AiApiProtocol.OpenAI, AiApiProtocol.Anthropic })
            {
                var config = Config(); config.ApiProtocol = protocol;
                handler = new RecordingHandler(n => n == 1 ? Error(400, "web_search tool is not supported")
                    : protocol == AiApiProtocol.Anthropic ? Success() : OpenAiSuccess());
                using var service = Service(config, handler);
                var reply = await service.SendMessageAsync(service.CreateNewSession(), "search weather", webSearch: true);
                Check(reply.Content == "ok" && handler.Requests.Count == 2, "search retries one alternate format");
                Check(handler.Requests[0].Url == handler.Requests[1].Url
                    && handler.Requests[0].Body.GetProperty("model").GetString() == handler.Requests[1].Body.GetProperty("model").GetString(),
                    "search retry retains endpoint/model");
                Check(protocol == AiApiProtocol.OpenAI
                    ? handler.Requests[1].Body.TryGetProperty("web_search_options", out _) && !handler.Requests[1].Body.TryGetProperty("tools", out _)
                    : handler.Requests[1].Body.GetProperty("tools")[0].GetProperty("type").GetString() == "web_search_20260318",
                    "alternate search request changes actual wire format");
            }
            handler = new RecordingHandler(_ => Error(400, "web_search is not supported"));
            using (var service = Service(Config(), handler))
            {
                string failure = "";
                try { await service.SendMessageAsync(service.CreateNewSession(), "search weather", webSearch: true); }
                catch (InvalidOperationException ex) { failure = ex.Message; }
                Check(handler.Requests.Count == 2 && failure.Contains("关闭联网搜索"), "two rejected formats yield actionable suggestion");
            }
            Check(!AiRequestExecutionPolicy.IsWebSearchCompatibilityFailure(401, "web_search invalid api key")
                && !AiRequestExecutionPolicy.IsWebSearchCompatibilityFailure(503, "web_search unavailable")
                && !AiRequestExecutionPolicy.IsWebSearchCompatibilityFailure(400, "temperature is unsupported"),
                "unrelated auth/transport/parameter errors do not become search errors");
            var thinking = Config(); thinking.ReasoningLevel = AiReasoningLevel.High; thinking.MaxOutputTokens = 64;
            handler = new RecordingHandler(_ => Success());
            using (var service = Service(thinking, handler))
            {
                var result = await service.TestModelAsync("claude-test");
                Check(!result.Ok && result.Message.Contains("提高输出上限") && handler.Requests.Count == 0,
                    "thinking cannot silently increase explicit output budget");
            }
            return count;
        }
        finally { foreach (var directory in directories) if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK) { Content = new StringContent(
        "{\"type\":\"message\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":7,\"output_tokens\":2}}", Encoding.UTF8, "application/json") };
    private static HttpResponseMessage OpenAiSuccess() => new(HttpStatusCode.OK) { Content = new StringContent(
        "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Error(int status, string text) => new((HttpStatusCode)status)
    { Content = new StringContent(JsonSerializer.Serialize(new { error = new { message = text } }), Encoding.UTF8, "application/json") };
    private sealed record Request(string Url, string? ApiKey, string? Version, string? Bearer, JsonElement Body);
    private sealed class RecordingHandler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal List<Request> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(new(request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null,
                request.Headers.TryGetValues("anthropic-version", out var versions) ? versions.Single() : null,
                request.Headers.Authorization?.Parameter, document.RootElement.Clone()));
            return response(Requests.Count);
        }
    }
    private const string Stream = """
        event: message_start
        data: {"type":"message_start","message":{"usage":{"input_tokens":10,"cache_read_input_tokens":5,"output_tokens":0}}}

        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"private public thought"}}

        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"hello"}}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}

        event: message_stop
        data: {"type":"message_stop"}

        """;
}
