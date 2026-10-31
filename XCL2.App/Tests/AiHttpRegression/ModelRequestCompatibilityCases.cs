using System.Net;
using System.Text;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class ModelRequestCompatibilityCases
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("Model request: " + name); count++; }
        AiAssistantConfig Config() => new() { UseCustomApiKey = true, BaseUrl = "https://models.example/v1", ApiKey = "private-test-key",
            StreamResponses = false, SendTemperature = true, Temperature = 0.6, ReasoningLevel = AiReasoningLevel.High,
            CustomModels = new() { new() { Id = "alpha", ProviderId = "custom" }, new() { Id = "beta", ProviderId = "custom" } } };
        HttpResponseMessage Response(int status, string value) => new((HttpStatusCode)status) {
            Content = new StringContent(status == 200 ? "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}" :
                JsonSerializer.Serialize(new { error = new { message = value } }), Encoding.UTF8, "application/json") };
        var directory = Path.Combine(Path.GetTempPath(), "xcl-model-rules-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = Config(); var sent = new List<JsonElement>();
            var handler = new Handler(async (r, n, ct) =>
            {
                using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct)); var body = json.RootElement.Clone(); sent.Add(body);
                return body.GetProperty("model").GetString() == "alpha" && body.GetProperty("temperature").GetDouble() != 1.0
                    ? Response(400, "invalid temperature: must be 1.0 when thinking is enabled") : Response(200, "");
            });
            var service = new AiAssistantService(cfg, directory, new HttpClient(handler));
            Check((await service.TestModelAsync("alpha")).Ok && sent.Count == 2, "screenshot error repairs same model");
            Check(sent[0].GetProperty("temperature").GetDouble() == 0.6 && sent[1].GetProperty("temperature").GetDouble() == 1,
                "fixed value, not blind omission");
            Check(cfg.Temperature == 0.6 && sent[1].GetProperty("reasoning_effort").GetString() == "high", "user preference and thinking retained");
            Check((await service.TestModelAsync("alpha")).Ok && sent.Count == 3, "verified mode reuses rule");
            Check((await service.TestModelAsync("beta")).Ok && sent[^1].GetProperty("temperature").GetDouble() == 0.6, "same endpoint other model isolated");
            cfg.ReasoningLevel = AiReasoningLevel.Off;
            await service.TestModelAsync("alpha");
            Check(sent[^2].GetProperty("temperature").GetDouble() == 0.6, "different requested mode revalidates default-thinking constraint");
            var disk = File.ReadAllText(Path.Combine(directory, "ai", "model-parameter-memory-v2.json"));
            Check(!disk.Contains("private-test-key") && !disk.Contains("models.example") && !disk.Contains("alpha"), "cache identities hashed");
            var preview = service.PrepareModelDiagnostic("alpha", "api_key=UNKNOWN_TOKEN Authorization: Bearer private-test-key",
                new() { Documentation = "{\"api_key\":\"DOC_SECRET\"} https://tenant.example/path?token=QUERY_SECRET" });
            Check(!preview.Contains("UNKNOWN_TOKEN") && !preview.Contains("DOC_SECRET") && !preview.Contains("QUERY_SECRET") && !preview.Contains("private-test-key"), "diagnostic redaction includes pasted secrets");
            service.ClearModelCompatibility(AiModelIds.BuildModelKey("custom", "alpha"));
            var before = sent.Count; await service.TestModelAsync("alpha");
            Check(sent.Count == before + 2, "single model reset forces revalidation");
            // Explicit settings are never silently overwritten.
            var key = AiModelIds.BuildModelKey("custom", "alpha");
            cfg.ModelRequestProfiles[key] = new() { Rules = new() { new() { Parameter = "temperature", Action = "set", Value = "0.4" } } };
            before = sent.Count;
            Check(!(await service.TestModelAsync(key)).Ok && sent.Count == before + 1, "conflicting explicit rule stops with actionable failure");
            cfg.ModelRequestProfiles[key] = new() { ThinkingProtocol = "thinking", Rules = new() { new() { Parameter = "temperature", Action = "set", Value = "1" } } };
            Check((await service.TestModelAsync(key)).Ok && sent[^1].GetProperty("thinking").GetProperty("type").GetString() == "disabled"
                && !sent[^1].TryGetProperty("reasoning_effort", out _), "explicit off protocol is distinct from omission");
            Check(AiModelParameterPolicy.Validate(new() { Rules = new() { new() { Parameter = "messages" } } }) != null, "rules cannot remove essential content");
            Check(AiModelParameterPolicy.Learn(401, "temperature must be 1.0 when thinking is enabled", new Dictionary<string, JsonElement>()) == null,
                "auth failures never become parameter rules");
            // A failed repair is not persisted, even though a retry was attempted.
            var badDir = Path.Combine(directory, "unsuccessful");
            var badHandler = new Handler((r,n,ct) => Task.FromResult(n % 2 == 1
                ? Response(400, "invalid temperature: must be 1.0 when thinking is enabled") : Response(400, "invalid messages")));
            var bad = new AiAssistantService(Config(), badDir, new HttpClient(badHandler));
            Check(!(await bad.TestModelAsync("alpha")).Ok && !File.Exists(Path.Combine(badDir, "ai", "model-parameter-memory-v2.json")), "failed repair not remembered");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        return count;
    }
}
