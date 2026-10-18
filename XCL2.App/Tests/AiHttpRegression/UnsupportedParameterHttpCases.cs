using System.Net;
using System.Text;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class UnsupportedParameterHttpCases
{
    private const string Model = "claude-sonnet-5-5";

    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("Unsupported parameter HTTP: " + message);
        }

        var roots = new List<string>();
        string NewRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "xcl-parameter-http-" + Guid.NewGuid().ToString("N"));
            roots.Add(root);
            return root;
        }
        try
        {
            // The user's exact error is repaired on the same endpoint, key and model.
            // The provider preference applies to later models/keys and survives a new service.
            {
                var cfg = Configuration();
                var root = NewRoot();
                var handler = new RecordingHandler((_, number) => number == 1
                    ? Error(400, Model + " does not support non-default temperature") : Success());
                var service = Service(cfg, root, handler);
                var first = await service.TestModelAsync(Model);
                Check(first.Ok && handler.Requests.Count == 2, "temperature is removed and the rejected request is retried once");
                Check(handler.Requests[0].Has("temperature") && !handler.Requests[1].Has("temperature"), "retry omits the temperature JSON property");
                Check(handler.Requests.All(r => r.Url == "https://parameter.example/v1/chat/completions"
                    && r.Key == "parameter-key" && r.String("model") == Model), "parameter retry keeps endpoint, key and real model ID");
                await service.TestModelAsync(Model);
                await service.TestModelAsync("another-model");
                Check(handler.Requests.Count == 4 && handler.Requests.Skip(1).All(r => !r.Has("temperature")), "provider memory applies to every subsequent model");

                cfg.ApiKey = "replacement-key";
                var reloaded = new RecordingHandler((_, _) => Success());
                service = Service(cfg, root, reloaded);
                var later = await service.TestModelAsync(Model);
                Check(later.Ok && reloaded.Requests.Single().Key == "replacement-key"
                    && !reloaded.Requests.Single().Has("temperature"), "new service and changed key retain the persisted provider preference");

                await service.TestModelAsync(AiModelIds.BuildModelKey("separate-provider", Model));
                Check(reloaded.Requests.Count == 2 && reloaded.Requests[1].Has("temperature")
                    && reloaded.Requests[1].Url == reloaded.Requests[0].Url, "another provider ID sharing the URL keeps its own parameter support");
                cfg.BaseUrl = "https://another-parameter.example/v1";
                service.UpdateConfig(cfg);
                await service.TestModelAsync(Model);
                Check(reloaded.Requests.Count == 3 && reloaded.Requests[2].Has("temperature")
                    && reloaded.Requests[2].Url == "https://another-parameter.example/v1/chat/completions", "a different URL of the same provider is not poisoned");
            }

            // More than one unsupported option can be rejected in separate responses.
            // Each attempt removes the newly identified field and keeps earlier removals.
            {
                var cfg = Configuration(); cfg.StreamResponses = true; cfg.RequestUsageMetadata = true;
                var handler = new RecordingHandler((_, number) => number switch
                {
                    1 => Error(400, "Unsupported parameter: 'temperature'."),
                    2 => Error(400, "Unknown parameter: stream_options"),
                    3 => Error(400, "The stream parameter is not supported by this model"),
                    _ => Success()
                });
                var result = await Service(cfg, NewRoot(), handler).TestModelAsync(Model);
                Check(result.Ok && handler.Requests.Count == 4, "successive unsupported fields each get a bounded retry");
                Check(handler.Requests[0].Has("temperature") && handler.Requests[0].Bool("stream") == true
                    && handler.Requests[0].Has("stream_options"), "initial request contains all configured options");
                Check(!handler.Requests[1].Has("temperature") && handler.Requests[1].Has("stream_options"), "first fallback removes only temperature");
                Check(!handler.Requests[2].Has("temperature") && !handler.Requests[2].Has("stream_options")
                    && handler.Requests[2].Bool("stream") == true, "stream_options rejection preserves streaming");
                Check(!handler.Requests[3].Has("temperature") && !handler.Requests[3].Has("stream_options")
                    && !handler.Requests[3].Has("stream"), "stream rejection also removes its dependent stream_options");
                Check(handler.Requests.Select(r => r.Key).Distinct().Single() == "parameter-key", "multiple parameter retries never rotate credentials");
            }

            // A direct stream rejection must remove stream_options even if no separate
            // error has identified that dependent option yet, and persist that decision.
            {
                var cfg = Configuration(); cfg.StreamResponses = true; cfg.RequestUsageMetadata = true;
                var root = NewRoot();
                var handler = new RecordingHandler((_, number) => number == 1
                    ? Error(400, "Unsupported parameter: 'stream'.") : Success());
                var service = Service(cfg, root, handler);
                Check((await service.TestModelAsync(Model)).Ok && handler.Requests.Count == 2, "stream rejection gets one nonstreaming retry");
                Check(handler.Requests[0].Bool("stream") == true && handler.Requests[0].Has("stream_options")
                    && !handler.Requests[1].Has("stream") && !handler.Requests[1].Has("stream_options"), "stream fallback removes both related JSON properties");
                var reloaded = new RecordingHandler((_, _) => Success());
                await Service(cfg, root, reloaded).TestModelAsync(Model);
                Check(!reloaded.Requests.Single().Has("stream") && !reloaded.Requests.Single().Has("stream_options"), "stream dependency remains absent after reloading service");
            }

            // Invalid values and authentication/gateway errors are not capability evidence,
            // even when their text happens to mention an unsupported temperature.
            foreach (var (status, detail) in new[]
            {
                (400, "Invalid temperature: must be between 0 and 2"),
                (401, "temperature is unsupported; unauthorized"),
                (503, "upstream does not support non-default temperature")
            })
            {
                var cfg = Configuration();
                var root = NewRoot();
                var handler = new RecordingHandler((_, number) => number == 1 ? Error(status, detail) : Success());
                var service = Service(cfg, root, handler);
                var result = await service.TestModelAsync(Model);
                var expectedAttempts = status == 503 ? 2 : 1;
                Check(handler.Requests.Count == expectedAttempts && result.Ok == (status == 503), "non-capability error follows its normal retry policy: " + status);
                await service.TestModelAsync(Model);
                Check(handler.Requests.All(r => r.Has("temperature")), "non-capability error never removes temperature: " + status);
                var reloaded = new RecordingHandler((_, _) => Success());
                await Service(cfg, root, reloaded).TestModelAsync(Model);
                Check(reloaded.Requests.Single().Has("temperature"), "non-capability error cannot persist a disable: " + status);
            }

            // An SSE error frame before any output has the same safe fallback as HTTP 400.
            {
                var cfg = Configuration(); cfg.StreamResponses = true;
                var handler = new RecordingHandler((_, number) => number == 1
                    ? Sse("data: {\"error\":{\"code\":400,\"type\":\"unsupported_parameter\",\"message\":\"temperature is not supported\"}}\n\n") : Success());
                var result = await Service(cfg, NewRoot(), handler).TestModelAsync(Model);
                Check(result.Ok && handler.Requests.Count == 2 && !handler.Requests[1].Has("temperature"), "SSE pre-output parameter error retries without the rejected option");
            }

            // Once content, reasoning or even an invisible control fragment was emitted,
            // a parameter error cannot replay output or teach the provider a false rule.
            foreach (var delta in new[] { "\"content\":\"partial\"", "\"reasoning\":\"thinking\"", "\"content\":\"<<XCL2_\"" })
            {
                var cfg = Configuration(); cfg.StreamResponses = true;
                var root = NewRoot();
                var handler = new RecordingHandler((_, number) => number == 1
                    ? Sse("data: {\"choices\":[{\"delta\":{" + delta + "}}]}\n\ndata: {\"error\":{\"code\":400,\"message\":\"temperature is not supported\"}}\n\n") : Success());
                var service = Service(cfg, root, handler);
                var result = await service.TestModelAsync(Model);
                Check(!result.Ok && handler.Requests.Count == 1, "SSE error after any output is never replayed: " + delta);
                await service.TestModelAsync(Model);
                Check(handler.Requests.Count == 2 && handler.Requests[1].Has("temperature"), "post-output error does not disable future temperature: " + delta);
                var reloaded = new RecordingHandler((_, _) => Success());
                await Service(cfg, root, reloaded).TestModelAsync(Model);
                Check(reloaded.Requests.Single().Has("temperature"), "post-output error does not persist unsupported capability: " + delta);
            }

            // A provider may name an unsupported field that this request never sent.
            // It cannot cause an identical-body retry loop or an invented learned rule.
            {
                var cfg = Configuration(); cfg.SendTemperature = false;
                var root = NewRoot();
                var handler = new RecordingHandler((_, _) => Error(400, "Unsupported parameter: temperature"));
                var service = Service(cfg, root, handler);
                var result = await service.TestModelAsync(Model);
                Check(!result.Ok && handler.Requests.Count == 1 && !handler.Requests[0].Has("temperature"), "absent temperature cannot trigger a no-op fallback loop");
                cfg.SendTemperature = true;
                var reloaded = new RecordingHandler((_, _) => Success());
                await Service(cfg, root, reloaded).TestModelAsync(Model);
                Check(reloaded.Requests.Single().Has("temperature"), "an option absent from the rejected body is not learned as unsupported");
            }

            // Rejecting a reasoning value is not evidence that all models on this
            // provider reject the reasoning field. Preserve the older local fallback.
            {
                var cfg = Configuration();
                var root = NewRoot();
                var handler = new RecordingHandler((_, number) => number == 1
                    ? Error(400, "Invalid value 'max': reasoning_effort must be one of low, medium, high") : Success());
                var service = Service(cfg, root, handler);
                await service.SendMessageAsync(service.CreateNewSession(), "hello", reasoningLevel: AiReasoningLevel.Max);
                Check(handler.Requests.Count == 2 && handler.Requests[0].String("reasoning_effort") == "max"
                    && !handler.Requests[1].Has("reasoning_effort"), "unsupported reasoning value still falls back locally");
                await service.SendMessageAsync(service.CreateNewSession(), "another", forcedModel: "another-model", reasoningLevel: AiReasoningLevel.High);
                Check(handler.Requests.Count == 3 && handler.Requests[2].String("reasoning_effort") == "high",
                    "a value error cannot disable the reasoning field on another model");
                var reloaded = new RecordingHandler((_, _) => Success());
                var fresh = Service(cfg, root, reloaded);
                await fresh.SendMessageAsync(fresh.CreateNewSession(), "after restart", reasoningLevel: AiReasoningLevel.Max);
                Check(reloaded.Requests.Single().String("reasoning_effort") == "max",
                    "an enum value rejection cannot persist a provider-wide field restriction");
            }

            // The output cap must survive max_tokens compatibility negotiation.
            // Dropping both cap names would silently bypass configured local limits.
            {
                var cfg = Configuration();
                var root = NewRoot();
                var handler = new RecordingHandler((_, number) => number == 1
                    ? Error(400, "Unsupported parameter: 'max_tokens'. Use max_completion_tokens instead.") : Success());
                var service = Service(cfg, root, handler);
                var result = await service.TestModelAsync(Model);
                Check(result.Ok && handler.Requests.Count == 2 && handler.Requests[0].Number("max_tokens") == 64,
                    "unsupported max_tokens gets a compatible capped retry");
                Check(!handler.Requests[1].Has("max_tokens") && handler.Requests[1].Number("max_completion_tokens") == 64,
                    "max_completion_tokens preserves the requested output bound");
                await service.TestModelAsync(Model);
                Check(handler.Requests.Count == 3 && !handler.Requests[2].Has("max_tokens")
                    && handler.Requests[2].Number("max_completion_tokens") == 64, "later requests retain the compatible cap property");
            }
            {
                var cfg = Configuration();
                var handler = new RecordingHandler((_, number) => number == 1
                    ? Error(400, "Unsupported parameter: 'max_tokens'. Use max_completion_tokens instead.")
                    : Error(400, "Unsupported parameter: 'max_completion_tokens'."));
                var result = await Service(cfg, NewRoot(), handler).TestModelAsync(Model);
                Check(!result.Ok && handler.Requests.Count == 2, "rejecting both output-cap names terminates without an uncapped third attempt");
                Check(handler.Requests.All(r => r.Number("max_tokens") == 64 || r.Number("max_completion_tokens") == 64),
                    "every cap-negotiation attempt retains an output limit");
            }
        }
        finally
        {
            foreach (var root in roots)
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return checks;
    }

    private static AiAssistantConfig Configuration() => new()
    {
        UseCustomApiKey = true, BaseUrl = "https://parameter.example/v1", ApiKey = "parameter-key",
        EnableApiKeyFailover = false, EnableEndpointFailover = false,
        StreamResponses = false, RequestUsageMetadata = true, SendTemperature = true, Temperature = 0.6,
        MaxOutputTokens = 64, WorkMode = AiWorkMode.Chat, RoutingMode = AiRoutingMode.SpecificModel,
        SelectedModel = Model, NormalModelId = Model, ExpertModelId = Model,
        CustomModels = new()
        {
            new() { Id = Model, DisplayName = "Claude", ProviderId = "custom" },
            new() { Id = "another-model", DisplayName = "Another", ProviderId = "custom" },
            new() { Id = Model, DisplayName = "Separate", ProviderId = "separate-provider" }
        },
        ApiEndpoints = new()
        {
            new() { Id = "separate-provider", Name = "Separate", BaseUrl = "https://parameter.example/v1", ApiKeys = new() { "other-provider-key" } }
        }
    };

    private static AiAssistantService Service(AiAssistantConfig config, string root, HttpMessageHandler handler)
        => new(config, root, new HttpClient(handler));
    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json")
    };
    private static HttpResponseMessage Error(int status, string message) => new((HttpStatusCode)status)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { error = new { message } }), Encoding.UTF8, "application/json")
    };
    private static HttpResponseMessage Sse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/event-stream")
    };

    private sealed record Request(string Url, string? Key, JsonElement Json)
    {
        internal bool Has(string name) => Json.TryGetProperty(name, out _);
        internal string? String(string name) => Json.TryGetProperty(name, out var value) ? value.GetString() : null;
        internal bool? Bool(string name) => Json.TryGetProperty(name, out var value) ? value.GetBoolean() : null;
        internal int? Number(string name) => Json.TryGetProperty(name, out var value) ? value.GetInt32() : null;
    }

    private sealed class RecordingHandler(Func<Request, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<Request> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var captured = new Request(request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.Parameter, doc.RootElement.Clone());
            Requests.Add(captured);
            return respond(captured, Requests.Count);
        }
    }
}
