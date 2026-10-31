using System.Net;
using System.Text;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

int assertions = 0;
void Assert(bool ok, string text) { assertions++; if (!ok) throw new Exception(text); }
void Rejects<T>(Action action, string text) where T : Exception
{ bool rejected = false; try { action(); } catch (T) { rejected = true; } Assert(rejected, text); }
async Task RejectsAsync<T>(Func<Task> action, string text) where T : Exception
{ bool rejected = false; try { await action(); } catch (T) { rejected = true; } Assert(rejected, text); }
AiAssistantConfig Config(bool failover = false) => new()
{
    UseCustomApiKey = true, BaseUrl = "https://one.example/v1", ApiKey = "key-one", ApiKeys = new() { "key-two" },
    EnableApiKeyFailover = true, EnableEndpointFailover = failover, StreamResponses = false, MaxOutputTokens = 64,
    SelectedModel = "alpha", NormalModelId = "alpha", ExpertModelId = "alpha", RoutingMode = AiRoutingMode.SpecificModel,
    CustomModels = new()
    {
        new() { Id = "alpha", DisplayName = "Alpha", ProviderId = "custom", Tier = AiModelTier.Normal },
        new() { Id = "alpha", DisplayName = "Alpha Two", ProviderId = "second", Tier = AiModelTier.Normal }
    },
    ApiEndpoints = new() { new() { Id = "second", Name = "Second", BaseUrl = "https://two.example/v1", ApiKeys = new() { "key-second" } } }
};
AiAssistantService Service(AiAssistantConfig c, Handler handler) => new(c,
    Path.Combine(Path.GetTempPath(), "xcl-http-" + Guid.NewGuid().ToString("N")), new HttpClient(handler));
HttpResponseMessage Success(string text = "ok") => new(HttpStatusCode.OK)
{
    Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } },
        usage = new { prompt_tokens = 8, completion_tokens = 3, total_tokens = 11 } }), Encoding.UTF8, "application/json")
};
HttpResponseMessage Error(int status, string message) => new((HttpStatusCode)status)
{ Content = new StringContent(JsonSerializer.Serialize(new { error = new { message } }), Encoding.UTF8, "application/json") };
HttpResponseMessage Sse(string text) => new(HttpStatusCode.OK)
{ Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };

// A full chat URL or a fetched /models URL must address the actual chat endpoint exactly once.
foreach (var (baseUrl, expectedUrl) in new[]
{
    ("https://one.example/v1/chat/completions?tenant=demo#x", "https://one.example/v1/chat/completions?tenant=demo"),
    ("https://one.example/v1/models", "https://one.example/v1/chat/completions")
})
{
    var cfg = Config(); cfg.BaseUrl = baseUrl;
    string? actualUrl = null;
    var handler = new Handler((r, n, ct) => { actualUrl = r.RequestUri!.AbsoluteUri; return Task.FromResult(Success()); });
    var result = await Service(cfg, handler).TestModelAsync("alpha", exactTarget: false);
    Assert(result.Ok && actualUrl == expectedUrl && handler.Requests.Count == 1, "full endpoint URL is normalized before HTTP");
}
// Local unauthenticated endpoints are usable for both common and named API configurations.
foreach (var named in new[] { false, true })
{
    var cfg = Config();
    if (named) cfg.ApiEndpoints[0].ApiKeys.Clear();
    else { cfg.ApiKey = ""; cfg.ApiKeys.Clear(); }
    var model = AiModelIds.BuildModelKey(named ? "second" : "custom", "alpha");
    cfg.SelectedModel = model;
    var handler = new Handler((r, n, ct) => Task.FromResult(Success()));
    var service = Service(cfg, handler);
    Assert(service.GetConfigurationIssue("hello", false) == null, "no-key API passes configuration check");
    var result = await service.TestModelAsync(model);
    Assert(result.Ok && handler.Requests.Count == 1 && handler.Requests[0].Key == null
        && handler.Requests[0].Host == (named ? "two.example" : "one.example"), "empty key omits Authorization and never borrows another provider key");
}

// Quota exhaustion rotates only keys belonging to this endpoint and remembers the working key.
{
    var cfg = Config();
    var handler = new Handler((r, n, ct) => Task.FromResult(n == 1 ? Error(429, "insufficient_quota") : Success()));
    var service = Service(cfg, handler);
    var session = service.CreateNewSession();
    await service.SendMessageAsync(session, "hello");
    await service.SendMessageAsync(session, "hello again");
    Assert(handler.Requests.Select(r => r.Key).SequenceEqual(new[] { "key-one", "key-two", "key-two" }), "key rotation/preference");
    Assert(handler.Requests.All(r => r.Host == "one.example" && r.Model == "alpha"), "credentials stay scoped; real model ID");
    Assert(session.UsageRequests.Count == 3 && session.UsageRequests[0].TotalTokens == 0, "each attempt tracked; rejection not billed");
    Assert(AiUsageAccounting.Settings(cfg, session.UsageRequests[1].ModelKey).UsedTokens == 22, "successful usage accumulated");
}
// A floating/manual choice must reach the selected provider/model, not either default.
{
    var cfg = Config(true);
    cfg.CustomModels.Add(new() { Id = "beta", DisplayName = "Chosen model", ProviderId = "second", Tier = AiModelTier.Normal });
    var handler = new Handler((r, n, ct) => Task.FromResult(Success()));
    var service = Service(cfg, handler);
    cfg.SelectedModel = AiModelIds.BuildModelKey("second", "beta");
    cfg.RoutingMode = AiRoutingMode.SpecificModel;
    cfg.AutoModelRouting = false;
    service.UpdateConfig(cfg);
    await service.SendMessageAsync(service.CreateNewSession(), "use my selected model");
    Assert(handler.Requests.Count == 1 && handler.Requests[0].Host == "two.example"
        && handler.Requests[0].Model == "beta", "manual selection overrides normal/expert defaults and endpoint priority");
}
// Opting into automatic routing failover must not silently change an explicitly pinned turn.
{
    var cfg = Config(true);
    cfg.SelectedModel = AiModelIds.BuildModelKey("custom", "alpha");
    var handler = new Handler((r, n, ct) => Task.FromResult(r.RequestUri!.Host == "one.example"
        ? Error(404, "model_not_found") : Success()));
    var service = Service(cfg, handler);
    await RejectsAsync<Exception>(() => service.SendMessageAsync(service.CreateNewSession(), "manual model failure"),
        "unavailable manual choice reports an error rather than sending to another provider");
    Assert(handler.Requests.Count == 1 && handler.Requests[0].Host == "one.example", "pinned request does not fall back");
}
// The new run budget must preserve user cancellation as cancellation, not mislabel it as timeout.
{
    var cfg = Config();
    Assert(cfg.MaxRunMinutes == 15, "new AI configuration starts with a fifteen-minute total run budget");
    using var cancellation = new CancellationTokenSource();
    var handler = new Handler(async (r, n, ct) =>
    {
        cancellation.Cancel();
        await Task.Delay(Timeout.Infinite, ct);
        return Success();
    });
    var service = Service(cfg, handler);
    await RejectsAsync<OperationCanceledException>(() => service.SendMessageAsync(service.CreateNewSession(), "cancel me", ct: cancellation.Token),
        "user cancellation is not converted to a run-limit error");
    Assert(handler.Requests.Count == 1, "cancelled run stops before retry or endpoint fallback");
}
// Replacing configured credentials invalidates an old working-key preference immediately.
{
    var cfg = Config();
    var handler = new Handler((r, n, ct) => Task.FromResult(n == 1 ? Error(401, "invalid_api_key") : Success()));
    var service = Service(cfg, handler);
    await service.TestModelAsync("alpha", exactTarget: false);
    cfg.ApiKey = "replacement-key"; cfg.ApiKeys.Clear(); service.UpdateConfig(cfg);
    await service.TestModelAsync("alpha", exactTarget: false);
    Assert(handler.Requests.Select(r => r.Key).SequenceEqual(new[] { "key-one", "key-two", "replacement-key" }), "removed cached key is never reused");
}
// Explicit key failures may rotate; unrelated forbidden responses cannot exhaust credentials.
foreach (var credentialError in new[] { false, true })
{
    var handler = new Handler((r, n, ct) => Task.FromResult(n == 1 ? Error(403, credentialError ? "invalid_api_key" : "model access forbidden") : Success()));
    var result = await Service(Config(true), handler).TestModelAsync("alpha", exactTarget: false);
    Assert(result.Ok == credentialError && handler.Requests.Count == (credentialError ? 2 : 1), "403 rotation requires a credential-specific error");
    Assert(handler.Requests.All(r => r.Host == "one.example"), "403 key retry remains scoped to its endpoint");
}
// A 5xx gateway error mentioning quota retries the current key, rather than discarding it.
{
    var handler = new Handler((r, n, ct) => Task.FromResult(n == 1 ? Error(503, "upstream insufficient_quota") : Success()));
    var result = await Service(Config(true), handler).TestModelAsync("alpha", exactTarget: false);
    Assert(result.Ok && handler.Requests.Select(r => r.Key).SequenceEqual(new[] { "key-one", "key-one" }), "gateway errors are transient before key classification");
}
// A deleted/disabled provider-scoped choice must never fall back to the first other API.
foreach (var deleted in new[] { false, true })
{
    var cfg = Config(true); cfg.SelectedModel = AiModelIds.BuildModelKey("second", "alpha");
    if (deleted) cfg.ApiEndpoints.Clear(); else cfg.ApiEndpoints[0].Enabled = false;
    var handler = new Handler((r, n, ct) => Task.FromResult(Success()));
    var service = Service(cfg, handler);
    Assert(service.GetConfigurationIssue("hello", false)?.Contains("接口不存在或已禁用") == true, "missing selected API reports a configuration issue");
    await RejectsAsync<InvalidOperationException>(() => service.SendMessageAsync(service.CreateNewSession(), "hello"), "missing selected API cannot send a conversation elsewhere");
    Assert(handler.Requests.Count == 0, "missing selected API sends no HTTP request");
}
// Old raw model IDs in mixed mode retain the custom provider rather than its built-in namesake.
{
    var cfg = Config(); cfg.MixBuiltInApi = true;
    cfg.CustomModels[0].Id = AiModelIds.NemotronLightning;
    cfg.SelectedModel = cfg.NormalModelId = cfg.ExpertModelId = AiModelIds.NemotronLightning;
    var handler = new Handler((r, n, ct) => Task.FromResult(Success()));
    var service = Service(cfg, handler);
    await service.SendMessageAsync(service.CreateNewSession(), "hello");
    Assert(handler.Requests.Count == 1 && handler.Requests[0].Host == "one.example" && handler.Requests[0].Key == "key-one", "mixed raw selection preserves custom endpoint");
}
// Parameter mistakes must not rotate keys or endpoints, even when the message names a model.
{
    var handler = new Handler((r,n,ct) => Task.FromResult(Error(400, "temperature must be between 0 and 2")));
    var result = await Service(Config(true), handler).TestModelAsync("alpha", exactTarget: false);
    Assert(!result.Ok && handler.Requests.Count == 1, "ordinary 400 never retries");
}
// Cross-interface fallback is disabled by default, then uses same model on the second endpoint when enabled.
foreach (var enabled in new[] { false, true })
{
    var cfg = Config(enabled);
    var handler = new Handler((r,n,ct) => Task.FromResult(r.RequestUri!.Host == "one.example" ? Error(404,"model_not_found") : Success()));
    var result = await Service(cfg, handler).TestModelAsync("alpha", exactTarget: false);
    Assert(result.Ok == enabled && handler.Requests.Count == (enabled ? 2 : 1), "endpoint failover opt-in");
    if (enabled) Assert(handler.Requests[1].Key == "key-second" && handler.Requests[1].Model == "alpha", "fallback endpoint credentials");
}
// Unsupported reasoning downgrades once using the same key; cache applies to future requests.
{
    var handler = new Handler((r,n,ct) => Task.FromResult(n == 1 ? Error(400,"reasoning_effort is unsupported") : Success()));
    var service = Service(Config(), handler);
    var session = service.CreateNewSession();
    await service.SendMessageAsync(session,"hello",reasoningLevel:AiReasoningLevel.VeryHigh);
    await service.SendMessageAsync(session,"more",reasoningLevel:AiReasoningLevel.Max);
    Assert(handler.Requests.Select(r=>r.Reasoning).SequenceEqual(new string?[] { "xhigh", null, "max" }), "reasoning omission is scoped to the requested mode");
    Assert(handler.Requests.All(r=>r.Key=="key-one"),"reasoning fallback stays on same key");
    Assert(!ToastService.Warnings.Any(w=>w.Contains("已改为 off")),"omitting effort is not disabling provider thinking");
}
// Authentication errors must rotate keys without erasing the requested reasoning effort.
{
    var handler = new Handler((r,n,ct)=>Task.FromResult(n==1?Error(401,"Unauthorized"):Success()));
    var service=Service(Config(),handler);
    await service.SendMessageAsync(service.CreateNewSession(),"hi",reasoningLevel:AiReasoningLevel.High);
    Assert(handler.Requests.All(r=>r.Reasoning=="high"),"auth error must not disable reasoning");
}
// Fast is opt-in by model capability; unsupported models do not send minimal.
foreach (var supports in new[] { false, true })
{
    var cfg=Config(); cfg.CustomModels[0].SupportsFastMode=supports;
    var handler=new Handler((r,n,ct)=>Task.FromResult(Success())); var service=Service(cfg,handler);
    await service.SendMessageAsync(service.CreateNewSession(),"fast",reasoningLevel:AiReasoningLevel.Fast);
    Assert(handler.Requests[0].Reasoning==(supports?"minimal":null),"fast capability");
}
// SSE error before output rotates keys. Even an invisible control-marker fragment counts as output.
{
    var cfg=Config(); cfg.StreamResponses=true;
    var handler=new Handler((r,n,ct)=>Task.FromResult(n==1?Sse("data: {\"error\":{\"code\":429,\"message\":\"insufficient_quota\"}}\n\n"):Success()));
    var result=await Service(cfg,handler).TestModelAsync("alpha", exactTarget: false);
    Assert(result.Ok && handler.Requests.Count==2,"SSE pre-output quota rotation");
}
foreach (var delta in new[] { "\"content\":\"<<XCL2_\"", "\"reasoning\":\"thinking\"", "\"content\":\"partial\"" })
{
    var cfg=Config(true); cfg.StreamResponses=true;
    var handler=new Handler((r,n,ct)=>Task.FromResult(Sse("data: {\"choices\":[{\"delta\":{"+delta+"}}]}\n\ndata: {\"error\":{\"code\":503,\"message\":\"server_error\"}}\n\n")));
    var result=await Service(cfg,handler).TestModelAsync("alpha", exactTarget: false);
    Assert(!result.Ok&&handler.Requests.Count==1,"no retry after visible/reasoning/control output");
}
// Tool continuation and subtask calls share accounting and routing; separate purposes remain visible.
{
    var cfg=Config(); cfg.MaxSubAgentsPerTurn=1;
    var handler=new Handler((r,n,ct)=>Task.FromResult(Success(n switch
    { 1=>"<<XCL2_AGENT:{\"name\":\"Review\",\"task\":\"Review supplied evidence\"}>>",2=>"Subtask result",_=>"Final answer" })));
    var service=Service(cfg,handler); var session=service.CreateNewSession();
    await service.SendMessageAsync(session,"review");
    Assert(handler.Requests.Count==3&&session.UsageRequests.Count==3,"subtask accounting");
    Assert(session.UsageRequests.Any(u=>u.Purpose.StartsWith("子智能体")&&!u.AffectsContext),"subtask identity");
}
// Local quotas apply to all calls and bound output even with the global unlimited setting.
{
    var cfg=Config(); cfg.MaxOutputTokens=0;
    var key=AiModelIds.GetCatalog(cfg)[0].ModelKey;
    cfg.ModelAccounting[key]=new(){TokenQuota=300};
    var handler=new Handler((r,n,ct)=>Task.FromResult(Success()));
    var result=await Service(cfg,handler).TestModelAsync("alpha", exactTarget: false);
    Assert(result.Ok&&handler.Requests[0].MaxTokens is >0 and <=300,"quota bounds unlimited output");
    cfg.ModelAccounting[key].TokenQuota=0;
    var blocked=await Service(cfg,handler).TestModelAsync("alpha", exactTarget: false);
    Assert(!blocked.Ok&&handler.Requests.Count==1,"quota blocks HTTP before sending");
}
// Cancelling a pending request never triggers fallback.
{
    var cfg=Config(true); using var cancellation=new CancellationTokenSource();
    var handler=new Handler(async (r,n,ct)=>{ cancellation.Cancel(); await Task.Delay(1000,ct); return Success(); });
    var result=await Service(cfg,handler).TestModelAsync("alpha",cancellation.Token);
    Assert(!result.Ok&&handler.Requests.Count==1,"cancellation stops retries");
}
// Exhausting one endpoint can use another only when cross-interface fallback is enabled.
{
    var cfg=Config(true);
    var handler=new Handler((r,n,ct)=>Task.FromResult(r.RequestUri!.Host=="one.example"?Error(402,"insufficient balance"):Success()));
    var result=await Service(cfg,handler).TestModelAsync("alpha", exactTarget: false);
    Assert(result.Ok&&handler.Requests.Select(r=>r.Key).SequenceEqual(new[]{"key-one","key-two","key-second"}),"exhausted endpoint fallback");
}
// Provider diagnostics cannot expose a key echoed in an error response.
{
    var cfg=Config();
    var handler=new Handler((r,n,ct)=>Task.FromResult(Error(400,"Invalid parameter; auth key-one or key-two")));
    var result=await Service(cfg,handler).TestModelAsync("alpha", exactTarget: false);
    Assert(!result.Message.Contains("key-one")&&!result.Message.Contains("key-two"),"error redaction");
}
// Per-model output overrides the global default and is actually sent, not just checked locally.
{
    var cfg=Config(); var key=AiModelIds.GetCatalog(cfg)[0].ModelKey;
    cfg.ModelAccounting[key]=new(){MaxOutputTokens=100000};
    var handler=new Handler((r,n,ct)=>Task.FromResult(Success()));
    await Service(cfg,handler).TestModelAsync("alpha", exactTarget: false);
    Assert(handler.Requests[0].MaxTokens==100000,"per-model output override without hidden 32K cap");
}
// In-flight settings replacement preserves the new config and uses the request's captured prices.
{
    var cfg=Config(); var key=AiModelIds.GetCatalog(cfg)[0].ModelKey;
    cfg.ModelAccounting[key]=new(){InputPricePerMillion=1m,OutputPricePerMillion=2m};
    var active=new ConfigService(); active.Config.AiAssistant=cfg; ConfigService.Active=active;
    var changed=Config(); changed.ModelAccounting[key]=new(){InputPricePerMillion=100m,OutputPricePerMillion=200m};
    var handler=new Handler((r,n,ct)=>{ active.Config.AiAssistant=changed; return Task.FromResult(Success()); });
    await Service(cfg,handler).TestModelAsync("alpha", exactTarget: false);
    Assert(ReferenceEquals(active.Config.AiAssistant,changed)&&changed.ModelAccounting[key].UsedTokens==11,"current config receives usage");
    Assert(changed.ModelAccounting[key].SpentUsd==0.000014m&&changed.ModelAccounting[key].InputPricePerMillion==100m,"request price snapshot preserved");
    ConfigService.Active=null;
}
// A floating conversation keeps its own mode but must use the current shared quota ledger.
// Replacing settings during its request cannot write the floating snapshot over the main config.
{
    var main = Config(); main.WorkMode = AiWorkMode.Plan;
    var key = AiModelIds.GetCatalog(main)[0].ModelKey;
    main.ModelAccounting[key] = new() { TokenQuota = 100000, InputPricePerMillion = 1m, OutputPricePerMillion = 2m };
    var local = Config(); local.WorkMode = AiWorkMode.Build;
    local.ModelAccounting[key] = new() { TokenQuota = 0, UsedTokens = 999 };
    var active = new ConfigService(); active.Config.AiAssistant = main; ConfigService.Active = active;
    var changed = Config(); changed.WorkMode = AiWorkMode.Plan;
    changed.ModelAccounting[key] = new() { TokenQuota = 100000, InputPricePerMillion = 100m, OutputPricePerMillion = 200m };
    var handler = new Handler((r, n, ct) => { active.Config.AiAssistant = changed; return Task.FromResult(Success()); });
    using var service = Service(local, handler);
    service.SharedAccountingConfigProvider = () => active.Config.AiAssistant;
    service.SaveSharedAccounting = active.Save;
    Assert(service.SystemPrompt.Contains(AiWorkModePolicy.BuildPrompt(AiWorkMode.Build)), "shared accounting does not replace floating Build with main Plan");
    var result = await service.TestModelAsync("alpha", exactTarget: false);
    Assert(result.Ok && handler.Requests.Count == 1, "floating request checks shared limits instead of its stale local ledger");
    Assert(ReferenceEquals(active.Config.AiAssistant, changed) && changed.WorkMode == AiWorkMode.Plan && local.WorkMode == AiWorkMode.Build,
        "floating usage preserves the latest main config and independent work modes");
    Assert(changed.ModelAccounting[key].UsedTokens == 11 && main.ModelAccounting[key].UsedTokens == 0
        && local.ModelAccounting[key].UsedTokens == 999 && active.Saves == 1, "floating usage updates and saves only the live shared ledger");
    Assert(changed.ModelAccounting[key].SpentUsd == 0.000014m && changed.ModelAccounting[key].InputPricePerMillion == 100m,
        "floating usage retains request prices without overwriting newly saved prices");
    changed.ModelAccounting[key].TokenQuota = 11;
    var blocked = await service.TestModelAsync("alpha", exactTarget: false);
    Assert(!blocked.Ok && handler.Requests.Count == 1, "shared quota exhaustion blocks another floating request before HTTP");
    ConfigService.Active = null;
}
// SSE usage accompanying an error is real usage and must survive a failover.
{
    var cfg=Config(); cfg.StreamResponses=true;
    var handler=new Handler((r,n,ct)=>Task.FromResult(n==1?Sse("data: {\"error\":{\"code\":429,\"message\":\"insufficient_quota\"},\"usage\":{\"prompt_tokens\":8,\"completion_tokens\":3,\"total_tokens\":11}}\n\n"):Success()));
    var service=Service(cfg,handler); var session=service.CreateNewSession();
    await service.SendMessageAsync(session,"hello");
    Assert(session.UsageRequests[0].TotalTokens==11&&AiUsageAccounting.Settings(cfg,session.UsageRequests[0].ModelKey).UsedTokens==22,"error frame usage retained");
}
// A tool follow-up is charged as a distinct request through the same routing pipeline.
{
    var handler=new Handler((r,n,ct)=>Task.FromResult(Success(n==1?"<<XCL2_ACCESS:{\"action\":\"read_log\"}>>":"done")));
    var service=Service(Config(),handler); var session=service.CreateNewSession();
    await service.SendMessageAsync(session,"tool");
    Assert(handler.Requests.Count==2&&session.UsageRequests.Count==2,"tool follow-up accounting");
}
// Compression and warm-up also use the same real-model routing and local accounting.
{
    var cfg=Config(); cfg.CompressionTokenThreshold=1;
    var handler=new Handler((r,n,ct)=>Task.FromResult(Success())); var service=Service(cfg,handler); var session=service.CreateNewSession();
    for(var i=0;i<8;i++) session.Messages.Add(new(){Role=AiMessageRole.User,Content="Earlier context",EstimatedTokens=20});
    await service.SendMessageAsync(session,"continue"); await service.WarmUpAsync();
    Assert(session.UsageRequests.Any(u=>u.Purpose=="上下文压缩"&&!u.AffectsContext),"compression accounting");
    Assert(handler.Requests.Count==3&&AiUsageAccounting.Settings(cfg,session.UsageRequests[0].ModelKey).UsedRequests==3,"warm-up accounting");
}
// Work mode is independent of access grants, model selection and reasoning. Unknown actions fail closed in Plan.
{
    foreach (var action in new[] { "read_log", "read_config", "list_workspace", "read_workspace_file", "registry_read" })
        Assert(AiWorkModePolicy.Allows(AiWorkMode.Plan, action), "Plan read-only allowlist: " + action);
    foreach (var action in new[] { "write_workspace_file", "delete_workspace_file", "apply_setting", "registry_write", "run_command", "request_directory", "future_tool", "", null })
        Assert(!AiWorkModePolicy.Allows(AiWorkMode.Plan, action), "Plan denies mutations/unknown: " + action);
    Assert(new AiAssistantConfig().WorkMode == AiWorkMode.Build, "older configuration keeps Build default");
    Assert(AiWorkModePolicy.Normalize((AiWorkMode)123) == AiWorkMode.Plan, "unknown imported mode is read-only");
    var current = AiWorkMode.Plan;
    var captured = new AiWorkModeContext(current, () => current);
    current = AiWorkMode.Build;
    Assert(!captured.Allows("write_workspace_file"), "captured Plan cannot upgrade in-flight");
    var build = new AiWorkModeContext(current, () => current);
    current = AiWorkMode.Plan;
    Assert(!build.Allows("run_command"), "current Plan restricts captured Build");
    current = AiWorkMode.Build;
    Assert(!build.Allows("run_command"), "restricted turn remains Plan after another switch");
    Assert(AiWorkModePolicy.Normalize(AiWorkMode.Agent) == AiWorkMode.Agent, "Agent remains a distinct execution mode");
    var agent = new AiWorkModeContext(AiWorkMode.Agent, () => current);
    Assert(agent.EffectiveMode == AiWorkMode.Agent && agent.Allows("run_command"), "Agent retains its captured execution mode");
    current = AiWorkMode.Plan;
    Assert(agent.EffectiveMode == AiWorkMode.Plan && !agent.Allows("run_command"), "Plan restricts Agent immediately");
    current = AiWorkMode.Agent;
    Assert(agent.EffectiveMode == AiWorkMode.Plan, "Agent cannot upgrade a restricted turn");
}
// Chat has no workspace or tool grant. Its optional log reader requires an
// explicit request in this user turn, independently of any broad access flags.
{
    Assert((int)AiWorkMode.Chat == 3 && AiWorkModePolicy.Normalize(AiWorkMode.Chat) == AiWorkMode.Chat,
        "Chat is appended without changing persisted Build/Plan/Agent values");
    foreach (var action in new[] { "update_task", "search_project", "read_file_range", "list_project_files", "read_config",
        "list_workspace", "read_workspace_file", "write_workspace_file", "request_directory", "registry_read", "registry_write",
        "run_command", "mouse_move", "mouse_click", "key_press", "future_tool", "", null })
        Assert(!AiWorkModePolicy.Allows(AiWorkMode.Chat, action), "Chat denies all non-log actions: " + action);
    Assert(AiWorkModePolicy.Allows(AiWorkMode.Chat, "read_log"), "read_log is the sole Chat policy candidate");
    var current = AiWorkMode.Chat;
    var ungranted = new AiWorkModeContext(current, () => current);
    Assert(!ungranted.Allows("read_log"), "Chat policy candidate is not a default log grant");
    var authorized = new AiWorkModeContext(current, () => current, allowChatLogReading: true);
    Assert(authorized.Allows("read_log") && !authorized.Allows("read_config"), "explicit Chat log authority is narrowly scoped");
    current = AiWorkMode.Build;
    Assert(ungranted.EffectiveMode == AiWorkMode.Chat && !ungranted.Allows("run_command"), "captured Chat cannot upgrade to Build");
    var capturedBuild = new AiWorkModeContext(current, () => current);
    current = AiWorkMode.Chat;
    Assert(capturedBuild.EffectiveMode == AiWorkMode.Chat && !capturedBuild.Allows("read_log"), "live Chat restricts a captured Build immediately");
    current = AiWorkMode.Build;
    Assert(capturedBuild.EffectiveMode == AiWorkMode.Chat && !capturedBuild.Allows("write_workspace_file"), "live Chat restriction persists after switching back");
    var capturedPlan = new AiWorkModeContext(AiWorkMode.Plan, () => current);
    current = AiWorkMode.Chat;
    Assert(capturedPlan.EffectiveMode == AiWorkMode.Chat && !capturedPlan.Allows("read_config"), "Chat restriction takes precedence over captured Plan");
    current = AiWorkMode.Plan;
    Assert(capturedPlan.EffectiveMode == AiWorkMode.Chat, "a later Plan switch cannot weaken an observed Chat restriction");
}
{
    const string rootSecret = "CHAT_MUST_NOT_DISCLOSE_ROOT";
    const string historySecret = "CHAT_MUST_NOT_REPLAY_TOOL_CONTEXT";
    const string summarySecret = "CHAT_MUST_NOT_REPLAY_OLD_SUMMARY";
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Chat;
    cfg.CompressionTokenThreshold = 1;
    cfg.WorkingDirectory = rootSecret; cfg.WorkingDirectories.Add(rootSecret + "_OTHER");
    cfg.FullDiskAccess = cfg.AbsoluteAccess = cfg.UnrestrictedAccess = cfg.SystemControlAccess = cfg.AllowShellCommands = true;
    AiFileAccessService.ApprovedRoots.Add(rootSecret); AiFileAccessService.GrantAllAccess = true;
    try
    {
        var handler = new Handler((r,n,ct) => Task.FromResult(Success("可以，我们聊聊。")));
        var service = Service(cfg, handler); var session = service.CreateNewSession();
        session.SummaryOfOlderMessages = summarySecret;
        session.Messages.Add(new() { Role = AiMessageRole.Assistant, WorkMode = AiWorkMode.Build,
            Content = historySecret + " <<XCL2_ACCESS:{\"action\":\"run_command\",\"command\":\"old\"}>>" });
        session.Messages.Add(new() { Role = AiMessageRole.User, Content = "LEGACY_MODE_UNKNOWN_HISTORY" });
        for (var i = 0; i < 8; i++) session.Messages.Add(new() { Role = AiMessageRole.User, WorkMode = AiWorkMode.Build,
            Content = historySecret + i, EstimatedTokens = 500 });
        session.Messages.Add(new() { Role = AiMessageRole.User, WorkMode = AiWorkMode.Chat, Content = "CHAT_KNOWN_USER_HISTORY" });
        session.Messages.Add(new() { Role = AiMessageRole.Assistant, WorkMode = AiWorkMode.Chat, Content = "CHAT_KNOWN_ASSISTANT_HISTORY" });
        var before = AiFileAccessService.DispatchCalls; var confirms = 0;
        service.ConfirmAccessRequest = (_, _) => { confirms++; return true; };
        var reply = await service.SendMessageAsync(session, "只是聊聊兴趣爱好");
        var sent = handler.Requests.Single();
        Assert(reply.Content == "可以，我们聊聊。" && AiFileAccessService.DispatchCalls == before && confirms == 0,
            "Chat is a single ordinary completion without compressing old tool history even with every access grant enabled");
        Assert(!sent.SystemPrompt.Contains("XCL2_") && !sent.SystemPrompt.Contains("工作目录") && !sent.SystemPrompt.Contains("执行权限")
            && !sent.MessagesJson.Contains(rootSecret) && !sent.MessagesJson.Contains(historySecret)
            && !sent.MessagesJson.Contains(summarySecret) && !sent.MessagesJson.Contains("LEGACY_MODE_UNKNOWN_HISTORY"),
            "Chat omits directory, execution protocol, old tool history and old summary from the request");
        Assert(sent.MessagesJson.Contains("CHAT_KNOWN_USER_HISTORY") && sent.MessagesJson.Contains("CHAT_KNOWN_ASSISTANT_HISTORY"),
            "Chat retains only its known ordinary conversation history");
        Assert(session.TaskId == null && service.Tasks.List().Count == 0,
            "Chat does not silently create a task or a plan");
        var restored = service.LoadSession(session.Id)!;
        Assert(restored.Messages.Last().WorkMode == AiWorkMode.Chat
            && restored.Messages.Last(m => m.Role == AiMessageRole.User).WorkMode == AiWorkMode.Chat,
            "Chat history provenance survives persistence for later context filtering");
        Assert(cfg.FullDiskAccess && cfg.AllowShellCommands && cfg.WorkingDirectory == rootSecret,
            "temporary Chat restriction does not erase grants saved for other modes");
    }
    finally { AiFileAccessService.ApprovedRoots.Clear(); AiFileAccessService.GrantAllAccess = false; }
}
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Chat;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success("继续聊聊。")));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var oldTask = service.Tasks.Create("OLD_TASK_SHOULD_NOT_REACH_CHAT", "OLD_GOAL_SHOULD_NOT_REACH_CHAT", "OLD_ROOT_SHOULD_NOT_REACH_CHAT");
    oldTask.Steps.Add(new() { Id = "old", Title = "OLD_STEP_SHOULD_NOT_REACH_CHAT", Status = "in_progress" });
    oldTask.Notes = "OLD_NOTES_SHOULD_NOT_REACH_CHAT"; service.Tasks.Save(oldTask);
    session.TaskId = oldTask.Id;
    var originalTask = JsonSerializer.Serialize(service.Tasks.Get(oldTask.Id));
    await service.SendMessageAsync(session, "我们只聊日常话题。");
    Assert(!handler.Requests.Single().MessagesJson.Contains("SHOULD_NOT_REACH_CHAT")
        && JsonSerializer.Serialize(service.Tasks.Get(oldTask.Id)) == originalTask && session.TaskId == oldTask.Id,
        "Chat neither exposes nor starts, stops, completes or rewrites an existing task record");
}
foreach (var marker in new[]
{
    "<<XCL2_ACCESS:{\"action\":\"request_directory\",\"path\":\"C:/private\",\"reason\":\"主动索要目录\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"run_command\",\"command\":\"echo unsafe\",\"interpreter\":\"cmd\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"read_config\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"write_workspace_file\",\"path\":\"a.txt\",\"content\":\"x\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"mouse_move\",\"x\":1,\"y\":1}>>",
    "<<XCL2_PLAN>>\n○ [wrong] 不应保存计划\n<</XCL2_PLAN>>",
    "<<XCL2_AGENT:{\"name\":\"Worker\",\"task\":\"不应启动子任务\"}>>",
    "<<XCL2_ASK:{\"questions\":[{\"id\":\"directory\",\"question\":\"请批准目录\"}]}>>",
    "<<XCL2_ACCESS:{\"action\":\"run_command\"}}>>"
})
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Chat; cfg.FullDiskAccess = cfg.AllowShellCommands = true;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success("我们继续聊天。\n" + marker)));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls; var confirms = 0; var questions = 0;
    service.ConfirmAccessRequest = (_, _) => { confirms++; return true; };
    var reply = await service.SendMessageAsync(session, "你好", askQuestionAsync: (_, _) =>
    { questions++; return Task.FromResult<AiQuestionAnswer?>(null); });
    Assert(handler.Requests.Count == 1 && AiFileAccessService.DispatchCalls == before && confirms == 0 && questions == 0,
        "Chat rejects unsolicited actions, questions, subtasks and malformed protocols without a retry: " + marker);
    Assert(session.TaskId == null && service.Tasks.List().Count == 0 && !reply.Content.Contains("XCL2_"),
        "Chat denial saves no task and hides internal control markers: " + marker);
}
// Only this turn's explicit request can enable the narrow log-reader protocol.
foreach (var input in new[] { "你好", "日志是什么？", "不要读取日志，我们只聊天。", "介绍如何读取日志。", "程序报告读取日志失败。", "读取日志失败", "解释这句话：\"请读取日志\"" })
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Chat; cfg.AllowCrashLogReading = true;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success("<<XCL2_ACCESS:{\"action\":\"read_log\"}>>")));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    session.Messages.Add(new() { Role = AiMessageRole.User, WorkMode = AiWorkMode.Chat, Content = "请读取日志并分析。" });
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, input);
    Assert(handler.Requests.Count == 1 && AiFileAccessService.DispatchCalls == before
        && !handler.Requests[0].SystemPrompt.Contains("<<XCL2_ACCESS:"),
        "old consent or mentioning logs does not authorize reading in this turn: " + input);
}
foreach (var enabled in new[] { false, true })
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Chat; cfg.AllowCrashLogReading = enabled;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n == 1
        ? "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>" : "日志已返回，可继续分析。")));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "请读取日志并分析错误。");
    Assert(handler.Requests.Count == (enabled ? 2 : 1) && AiFileAccessService.DispatchCalls == before + (enabled ? 1 : 0),
        "explicit current-turn log request still obeys the independent log-sharing switch: " + enabled);
    Assert(handler.Requests[0].SystemPrompt.Contains("read_log") == enabled
        && !handler.Requests[0].SystemPrompt.Contains("request_directory") && session.TaskId == null,
        "authorized Chat exposes only log reading without a directory or task: " + enabled);
}
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Chat; cfg.AllowCrashLogReading = true;
    var handler = new Handler((r,n,ct) =>
    {
        cfg.AllowCrashLogReading = false;
        return Task.FromResult(Success("<<XCL2_ACCESS:{\"action\":\"read_log\"}>>"));
    });
    var service = Service(cfg, handler); var session = service.CreateNewSession(); var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "请读取日志并分析错误。");
    Assert(handler.Requests.Count == 1 && AiFileAccessService.DispatchCalls == before,
        "revoking log sharing while the response is pending prevents dispatch");
}
foreach (var initialMode in new[] { AiWorkMode.Chat, AiWorkMode.Build, AiWorkMode.Agent, AiWorkMode.Plan })
{
    var cfg = Config(); cfg.WorkMode = initialMode; cfg.AllowShellCommands = cfg.FullDiskAccess = true;
    var handler = new Handler((r,n,ct) =>
    {
        cfg.WorkMode = initialMode == AiWorkMode.Chat ? AiWorkMode.Build : AiWorkMode.Chat;
        return Task.FromResult(Success("<<XCL2_ACCESS:{\"action\":\"read_config\"}>>"));
    });
    var service = Service(cfg, handler); var session = service.CreateNewSession(); var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "你好");
    Assert(handler.Requests.Count == 1 && AiFileAccessService.DispatchCalls == before,
        "captured or newly selected Chat cannot inherit another mode's access while awaiting HTTP: " + initialMode);
}
// A pending response can have received execution context before the user selects
// Chat. Its final text must keep its original provenance on future Chat turns.
{
    const string executionSecret = "MIXED_BUILD_CONTEXT_SECRET";
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Build;
    var handler = new Handler((r,n,ct) =>
    {
        if (n == 1) cfg.WorkMode = AiWorkMode.Chat;
        return Task.FromResult(Success(n == 1 ? executionSecret : "纯聊天继续。"));
    });
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    await service.SendMessageAsync(session, "先处理原执行任务");
    Assert(session.Messages.Last().WorkMode == AiWorkMode.Build && session.Messages.Last().Content == executionSecret,
        "a turn switched to Chat while pending retains its original execution-mode provenance");
    await service.SendMessageAsync(session, "现在只聊日常话题");
    Assert(handler.Requests.Count == 2 && !handler.Requests[1].MessagesJson.Contains(executionSecret)
        && session.Messages.Last().WorkMode == AiWorkMode.Chat,
        "a later Chat turn does not replay the mixed turn's execution-derived answer");
}
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Chat; cfg.FullDiskAccess = true;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success("<<XCL2_ACCESS:{\"action\":\"read_config\"}>>")));
    var service = Service(cfg, handler); service.ForceReadOnly = true;
    var session = service.CreateNewSession(); var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "只是普通聊天");
    Assert(handler.Requests.Count == 1 && AiFileAccessService.DispatchCalls == before && session.TaskId == null
        && !handler.Requests[0].SystemPrompt.Contains("Plan") && !handler.Requests[0].SystemPrompt.Contains("XCL2_"),
        "forced read-only analysis cannot weaken an explicitly selected Chat into Plan's read tools");
}
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Chat; cfg.AllowCrashLogReading = true;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success("<<XCL2_ACCESS:{\"action\":\"read_log\"}>>")));
    var service = Service(cfg, handler); var session = service.CreateNewSession(); var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "请读取本次启动器日志并分析。");
    Assert(handler.Requests.Count == 2 && AiFileAccessService.DispatchCalls == before + 1
        && handler.Requests[1].LastUserText == "工具已完成" && session.TaskId == null,
        "Chat exposes one actual buffered log result and denies a repeated log request in the same turn");
    Assert(!result.Content.Contains("XCL2_") && result.Content.Contains("未执行"),
        "repeated Chat log control is removed and its non-execution is reported");
}
string AgentPlan(string status) => "<<XCL2_ACCESS:" + JsonSerializer.Serialize(new
{
    action = "update_task", steps = new[] { new { id = "investigate", title = "读取项目并核验结果", status,
        evidence = status == "completed" ? "工具已返回，已核验内容" : "" } },
    next_action = status == "completed" ? "" : "读取项目文件"
}) + ">>";
string TextPlan(string lines) => "<<XCL2_PLAN>>\n" + lines + "\n<</XCL2_PLAN>>";
// A malformed Agent tool still receives protocol correction. A provider may
// voluntarily save a text plan before retrying; broken HTML never reaches the executor.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => "准备创建单文件动画\n<<XCL2_ACCESS:{\"action\":\"write_workspace_file\",\"path\":\"pelican.html\",\"content\":\"<html title=\"broken\">\"}>>",
        2 => TextPlan("○ [inspect] 读取日志并核验\n– [report] 报告结果"),
        3 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
        4 => TextPlan("✓ [inspect] 读取日志并核验 | evidence: 工具返回已核验\n✓ [report] 报告结果 | evidence: 已整理真实结果"),
        _ => "已完成核验并报告"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "创建动画之前先核验日志");
    Assert(handler.Requests.Count == 5 && AiFileAccessService.DispatchCalls == before + 1,
        "malformed Agent operation permits an optional plan before exactly one valid dispatch");
    Assert(handler.Requests[1].LastUserText.Contains("上一个控制请求未执行")
        && !handler.Requests[1].LastUserText.Contains("先修复计划阶段")
        && !handler.Requests[1].LastUserText.Contains("<html"),
        "Agent malformed tool receives ordinary protocol feedback without forced planning or broken file content");
    Assert(service.GetTask(session)!.Steps.All(s => s.Status == "completed")
        && result.Content.Contains("已完成核验并报告") && !result.Content.Contains("XCL2_"),
        "optional plan completion persists and rejected control data remains hidden");
}
// Saving a plan and dispatching a tool in the same reply is never partially accepted.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => TextPlan("○ [wrong] 不应保存的混合计划") + "\n<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
        2 => TextPlan("○ [inspect] 保存独立计划后读取日志"),
        3 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
        4 => TextPlan("✓ [inspect] 保存独立计划后读取日志 | evidence: 日志读取已核验"),
        _ => "混合请求已修正"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "读取日志");
    Assert(handler.Requests.Count == 5 && AiFileAccessService.DispatchCalls == before + 1
        && service.GetTask(session)!.Steps.Single().Id == "inspect",
        "mixed plan/tool reply saves neither part and requires a separate replacement plan");
    Assert(handler.Requests[1].LastUserText.Contains("上一个控制请求未执行")
        && !handler.Requests[1].LastUserText.Contains("先修复计划阶段"),
        "mixed reply uses ordinary protocol correction without requiring an initial plan");
}
// Providers commonly wrap the published text protocol in a fenced code block.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => "保存计划\n```text\n" + TextPlan("○ [inspect] 核验日志") + "\n```",
        2 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
        _ => "日志已返回，保留核验断点"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "调查日志");
    Assert(handler.Requests.Count == 3 && AiFileAccessService.DispatchCalls == before + 1
        && service.GetTask(session)!.Steps.Single().Id == "inspect",
        "terminal fenced text plan remains a valid optional Agent task update");
    Assert(result.Content.Contains("保存计划") && !result.Content.Contains("XCL2_") && !result.Content.Contains("```"),
        "fence wrappers and plan control text stay out of the visible reply");
}
// Planning prose is an ordinary answer, even after malformed control syntax.
// It must not start a hidden retry loop or claim that steps were saved.
foreach (var malformedFirst in new[] { false, true })
{
    const string promise = "本轮先按 Agent 要求把执行计划落盘到任务：";
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(malformedFirst && n == 1
        ? "<<XCL2_ACCESS:{\"action\":\"read_log\"}}>>" : promise)));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "核验日志");
    Assert(handler.Requests.Count == (malformedFirst ? 2 : 1) && AiFileAccessService.DispatchCalls == before
        && service.GetTask(session)!.Steps.Count == 0,
        "planning prose never creates steps or starts automatic plan recovery: " + malformedFirst);
    Assert(result.Content.Contains(promise) && !result.Content.Contains("XCL2_"),
        "ordinary planning prose returns to the user once: " + malformedFirst);
}
// Even a provider that always repeats a planning promise is queried only once.
{
    const string promise = "本轮先按 Agent 要求把执行计划落盘到任务：";
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(promise)));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "核验日志");
    Assert(handler.Requests.Count == 1 && AiFileAccessService.DispatchCalls == before
        && service.GetTask(session)!.Steps.Count == 0 && result.Content == promise,
        "repeated planning prose has no automatic correction requests or duplicated paragraphs");
}
// A direct answer, clarification, or refusal is a legitimate end to an Agent turn.
// Optional planning must not add requests to these answers.
foreach (var answer in new[] { "你好。", "你希望我先制定计划吗？", "我无法继续操作，建议先制定计划。" })
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(answer)));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "说明一下");
    Assert(handler.Requests.Count == 1 && result.Content == answer
        && AiFileAccessService.DispatchCalls == before && service.GetTask(session)!.Steps.Count == 0,
        "ordinary Agent reply is not forced into a planning loop: " + answer);
}
// Invalid questions or independent analysis requests retain their own correction
// path; optional planning must not turn these into file/tool execution requests.
foreach (var invalid in new[]
{
    "<<XCL2_ASK:{\"question\":\"请选择任务范围\"}}>>",
    "<<XCL2_AGENT:{\"task\":123}>>"
})
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n == 1 ? invalid : "请提供任务范围。")));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "先确认任务范围");
    Assert(handler.Requests.Count == 2 && AiFileAccessService.DispatchCalls == before
        && service.GetTask(session)!.Steps.Count == 0 && result.Content.Contains("请提供任务范围"),
        "malformed ASK/AGENT remains a bounded clarification without tools or invented plan");
    Assert(handler.Requests[1].LastUserText.Contains("上一个控制请求未执行")
        && !handler.Requests[1].LastUserText.Contains("先修复计划阶段"),
        "ASK/AGENT syntax errors retain generic protocol correction without planning prerequisites");
}
// Other captured modes and explicitly non-task conversations also leave prose unchanged.
foreach (var mode in new[] { AiWorkMode.Build, AiWorkMode.Plan, AiWorkMode.Agent })
{
    const string promise = "本轮先按 Agent 要求把执行计划落盘到任务：";
    var cfg = Config(); cfg.WorkMode = mode;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(promise)));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    if (mode == AiWorkMode.Agent) service.SetTaskTracking(session, false);
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "说明计划");
    Assert(handler.Requests.Count == 1 && result.Content == promise && AiFileAccessService.DispatchCalls == before,
        "optional planning leaves prose unchanged in other modes and non-task chat: " + mode);
    Assert(mode == AiWorkMode.Agent ? service.GetTask(session) == null : service.GetTask(session)!.Steps.Count == 0,
        "prose never creates or restores task steps: " + mode);
}
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Plan;
    var handler = new Handler((r,n,ct) =>
    {
        cfg.WorkMode = AiWorkMode.Agent;
        return Task.FromResult(Success("本轮先按 Agent 要求把执行计划落盘到任务："));
    });
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "说明计划");
    Assert(handler.Requests.Count == 1 && handler.Requests[0].SystemPrompt.Contains("【当前工作模式：Plan")
        && AiFileAccessService.DispatchCalls == before && service.GetTask(session)!.Steps.Count == 0,
        "captured Plan never upgrades into Agent or interprets ordinary prose as a task update");
}
// Cancellation during protocol correction never commits a plan or tool. The
// next turn can issue its authorized tool directly without saving replacement steps.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    using var cancellation = new CancellationTokenSource();
    var handler = new Handler((r,n,ct) =>
    {
        if (n == 2) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
        return Task.FromResult(Success(n switch
        {
            1 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}}>>",
            3 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
            _ => "后续核验已返回"
        }));
    });
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await RejectsAsync<OperationCanceledException>(() => service.SendMessageAsync(session, "开始核验", ct: cancellation.Token),
        "Agent protocol repair respects cancellation");
    Assert(handler.Requests.Count == 2 && AiFileAccessService.DispatchCalls == before
        && service.GetTask(session)!.Steps.Count == 0 && service.GetTask(session)!.Status == "interrupted",
        "cancelled protocol repair has no tools, saved steps or invented completion");
    var result = await service.SendMessageAsync(session, "继续核验");
    Assert(handler.Requests.Count == 4 && AiFileAccessService.DispatchCalls == before + 1
        && service.GetTask(session)!.Steps.Count == 0 && result.Content.Contains("后续核验已返回"),
        "cancelled repair releases its lock and the next Agent turn can execute without a plan");
}
// Voluntary text plans remain task updates, never shell or permission requests.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => "开始核验\n" + TextPlan("○ [inspect] 检查日志\n– [verify] 核验结果\nnext_action: 读取日志"),
        2 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
        3 => TextPlan("✓ [inspect] 检查日志 | evidence: 日志工具已返回\n○ [verify] 核验结果"),
        4 => TextPlan("completed: [inspect] 检查日志 | evidence: 日志工具已返回\ncompleted: [verify] 核验结果 | evidence: 已核验返回内容"),
        _ => "日志核验完成"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "检查并核验日志");
    var saved = service.GetTask(session)!;
    Assert(handler.Requests.Count == 5 && AiFileAccessService.DispatchCalls == before + 1,
        "optional text plan saves steps and only the explicit read dispatches");
    Assert(saved.Status == "completed" && saved.Steps.Select(s => s.Id).SequenceEqual(new[] { "inspect", "verify" })
        && saved.Steps.All(s => s.Status == "completed" && s.Evidence.Length > 0), "text plan statuses IDs and evidence persist");
    Assert(result.Content.Contains("开始核验") && !result.Content.Contains("XCL2_PLAN"), "text protocol is hidden while surrounding answer remains");
    Assert(cfg.WorkMode == AiWorkMode.Agent && !cfg.FullDiskAccess && !cfg.AllowShellCommands,
        "saving text plan grants no file or shell authority and never changes work mode");
    Assert(service.GetTask(service.LoadSession(session.Id)!)!.Steps[1].Evidence.Contains("核验"), "text plan survives session reload");
}
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => TextPlan("– 读取日志\n– 核验结果"),
        2 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
        _ => "保留断点"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "调查日志");
    Assert(handler.Requests.Count == 3 && AiFileAccessService.DispatchCalls == before + 1,
        "initial all-pending text Agent plan starts its first step before one tool dispatch");
    Assert(service.GetTask(session)!.Steps.Select(s => s.Id).SequenceEqual(new[] { "plan-1", "plan-2" })
        && service.GetTask(session)!.Steps.Select(s => s.Status).SequenceEqual(new[] { "in_progress", "pending" })
        && service.GetTask(session)!.Steps.All(s => s.Evidence.Length == 0),
        "initial all-pending plan starts only its first step without completion evidence");
}
// A later all-pending revision is an intentional pause. Its lack of an active
// step does not prevent a separately authorized tool request from running.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => TextPlan("○ [inspect] 核验日志\n– [report] 报告结果"),
        2 => TextPlan("– [inspect] 核验日志\n– [report] 报告结果"),
        3 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
        _ => "保留核验断点"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "核验日志");
    Assert(handler.Requests.Count == 4 && AiFileAccessService.DispatchCalls == before + 1
        && handler.Requests[3].LastUserText == "工具已完成",
        "later all-pending plan revision permits an authorized tool without requiring an active step");
    Assert(service.GetTask(session)!.Steps.All(s => s.Status == "pending" && s.Evidence.Length == 0),
        "tool execution preserves deliberate pending states without inventing evidence");
}
foreach (var mode in new[] { AiWorkMode.Plan, AiWorkMode.Build })
foreach (var useText in new[] { false, true })
{
    var cfg = Config(); cfg.WorkMode = mode;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n == 1
        ? useText ? TextPlan("– [inspect] 等待读取日志") : AgentPlan("pending")
        : "仅保存未开始计划")));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "记录计划");
    Assert(handler.Requests.Count == 2 && AiFileAccessService.DispatchCalls == before
        && service.GetTask(session)!.Steps.Single().Status == "pending",
        "Plan/Build all-pending metadata never auto-starts a step: " + mode + "/" + useText);
}
foreach (var lines in new[]
{
    "✓ [done] 已核验前置条件 | evidence: 前置证据已核验\n– [next] 等待后续操作",
    "blocked: [blocked] 缺少用户输入\n– [next] 等待后续操作"
})
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n == 1 ? TextPlan(lines) : "保留已有状态等待继续")));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "恢复真实计划状态");
    var steps = service.GetTask(session)!.Steps;
    Assert(handler.Requests.Count == 2 && AiFileAccessService.DispatchCalls == before
        && steps.All(s => s.Status != "in_progress") && steps[1].Status == "pending"
        && steps[0].Status == (lines.StartsWith('✓') ? "completed" : "blocked"),
        "mixed completed/pending or blocked initial plans retain their deliberate states");
    Assert(lines.StartsWith('✓') ? steps[0].Evidence.Contains("已核验") : steps[0].Evidence.Length == 0,
        "automatic plan initialization never invents or changes completion evidence");
}
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success("先立Plan：")));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "核验日志");
    Assert(handler.Requests.Count == 1 && AiFileAccessService.DispatchCalls == before
        && result.Content == "先立Plan：" && service.GetTask(session)!.Steps.Count == 0,
        "Chinese-adjacent Plan prose is returned once without automatic planning requests");
}
// Reordered text updates without IDs match unique old titles, preserving each
// step's own evidence instead of copying another step's positional evidence.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => TextPlan("✓ [inspect] 核验前置日志 | evidence: 日志证据\n○ [change] 调整文件 | evidence: 当前调整断点\n– [verify] 验证结果 | evidence: 待验证断点\nnext_action: 按当前调整断点继续\nnotes: 保留原有约束"),
        2 => TextPlan("○ 调整文件\n– 验证结果\n✓ 核验前置日志"),
        _ => "重排计划已保存"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "更新计划顺序");
    var task = service.GetTask(service.LoadSession(session.Id)!)!;
    Assert(handler.Requests.Count == 3 && AiFileAccessService.DispatchCalls == before
        && task.Steps.Select(s => s.Id).SequenceEqual(new[] { "change", "verify", "inspect" })
        && task.Steps.Select(s => s.Evidence).SequenceEqual(new[] { "当前调整断点", "待验证断点", "日志证据" }),
        "reordered ID-less Plan preserves stable IDs and each original evidence after reload");
    Assert(task.Steps.Select(s => s.Status).SequenceEqual(new[] { "in_progress", "pending", "completed" })
        && task.NextAction == "按当前调整断点继续" && task.Notes == "保留原有约束",
        "omitted plan fields preserve original steps, evidence, next action and constraints");
}
// A new turn may update notes while retaining the saved plan. Existing steps
// do not need to be resent before a separately authorized read can execute.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => AgentPlan("in_progress"),
        2 => "保存旧断点",
        3 => "<<XCL2_ACCESS:{\"action\":\"update_task\",\"notes\":\"保留旧步骤，仅更新约束\"}>>",
        4 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
        _ => "本轮重新核验已返回"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "保存调查断点");
    var result = await service.SendMessageAsync(session, "从旧断点继续核验");
    var task = service.GetTask(session)!;
    Assert(handler.Requests.Count == 5 && AiFileAccessService.DispatchCalls == before + 1
        && handler.Requests[4].LastUserText == "工具已完成",
        "fresh-turn metadata-only update and existing plan permit an authorized read without replacement steps");
    Assert(task.Steps.Single().Id == "investigate" && task.Notes == "保留旧步骤，仅更新约束"
        && result.Content.Contains("本轮重新核验已返回"),
        "metadata-only update preserves durable old steps and saves the new notes");
}
// Saving task metadata on a new task also does not require a steps array.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => "<<XCL2_ACCESS:{\"action\":\"update_task\",\"notes\":\"只需核验一次日志\",\"next_action\":\"读取日志\"}>>",
        2 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
        _ => "简单核验已返回"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "只读一次日志");
    var task = service.GetTask(session)!;
    Assert(handler.Requests.Count == 3 && AiFileAccessService.DispatchCalls == before + 1
        && task.Steps.Count == 0 && task.Notes == "只需核验一次日志" && task.NextAction == "读取日志"
        && result.Content.Contains("简单核验已返回"),
        "Agent metadata-only task update saves without steps and does not block a simple tool");
}
// Metadata saved before the first actual plan does not suppress the convenience
// that starts the first pending step once the provider voluntarily submits steps.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => "<<XCL2_ACCESS:{\"action\":\"update_task\",\"notes\":\"先记录约束\"}>>",
        2 => AgentPlan("pending"),
        3 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
        _ => "计划与日志已保存"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "记录约束后按需规划核验");
    var task = service.GetTask(session)!;
    Assert(handler.Requests.Count == 4 && AiFileAccessService.DispatchCalls == before + 1
        && task.Notes == "先记录约束" && task.Steps.Single().Status == "in_progress"
        && task.Steps.Single().Evidence.Length == 0,
        "metadata-only update does not count as the first saved steps or invent completion evidence");
}
// A single explicit active status can conflict with an omitted status inherited
// from the previous plan. Reject it before spending the user's finite operation budget.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent; cfg.MaxToolActionsPerTurn = 2;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => AgentPlan("in_progress"),
        2 => "保留旧计划断点",
        3 => "<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":[{\"id\":\"investigate\",\"title\":\"读取项目并核验结果\"},{\"id\":\"next\",\"title\":\"验证下一步\",\"status\":\"in_progress\"}]}>>",
        4 => TextPlan("○ [investigate] 读取项目并核验结果\n– [next] 验证下一步"),
        5 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
        _ => "有限预算内读取已完成"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "保存旧计划");
    var result = await service.SendMessageAsync(session, "更新步骤并读取日志");
    Assert(handler.Requests.Count == 6 && AiFileAccessService.DispatchCalls == before + 1
        && handler.Requests[3].LastUserText.Contains("最多一个")
        && result.Content.Contains("有限预算内读取已完成") && !result.Content.Contains("达到 2 次上限"),
        "inherited-state plan conflicts do not consume the finite budget for valid plan plus read");
    Assert(service.GetTask(session)!.Steps.Select(s => s.Status).SequenceEqual(new[] { "in_progress", "pending" }),
        "invalid inherited status conflict never becomes the saved plan");
}
// A task record damaged while a provider reply is pending cannot produce a false
// saved-plan activity or discard the independently persisted conversation diagnostic.
foreach (var mode in new[] { AiWorkMode.Agent, AiWorkMode.Build })
{
    var dataDir = Path.Combine(Path.GetTempPath(), "xcl-http-plan-storage-" + Guid.NewGuid().ToString("N"));
    var cfg = Config(); cfg.WorkMode = mode;
    AiAssistantService? service = null; AiChatSession? session = null;
    string taskPath = ""; byte[] damagedBytes = Array.Empty<byte>();
    var handler = new Handler((r,n,ct) =>
    {
        if (n == 1)
        {
            var damaged = service!.Tasks.Get(session!.TaskId!);
            damaged.Steps = null!;
            damagedBytes = JsonSerializer.SerializeToUtf8Bytes(damaged);
            taskPath = Path.Combine(dataDir, "ai_chat", "tasks", damaged.Id + ".json");
            File.WriteAllBytes(taskPath, damagedBytes);
        }
        return Task.FromResult(Success(TextPlan("○ [inspect] 未能保存的计划")));
    });
    service = new AiAssistantService(cfg, dataDir, new HttpClient(handler));
    session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "核验日志并保存计划");
    Assert(handler.Requests.Count == 1 && AiFileAccessService.DispatchCalls == before
        && result.Activities.Any(a => a.Status == AiActivityStatus.Failed && a.Title == "任务状态未保存")
        && !result.Activities.Any(a => a.Status == AiActivityStatus.Completed && a.Title == "任务步骤已保存"),
        "damaged in-flight task bounds recovery and records no false plan success: " + mode);
    Assert(result.Content.Contains("任务状态无法保存") && File.ReadAllBytes(taskPath).SequenceEqual(damagedBytes)
        && service.LoadSession(session.Id)!.Messages.Last().Content == result.Content,
        "task storage failure preserves independent chat history and original damaged record: " + mode);
    Assert(result.Content.Contains("Plan 保存失败"),
        "optional task persistence failure stops execution after the first provider reply: " + mode);
}
foreach (var mode in new[] { AiWorkMode.Plan, AiWorkMode.Build })
{
    var cfg = Config(); cfg.WorkMode = mode;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n == 1
        ? TextPlan("✓ [done] 仅记录计划 | evidence: 需求已核对\nnotes: run_command、FullDiskAccess=true 仅为文字") : "计划已保存")));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "记录计划");
    Assert(AiFileAccessService.DispatchCalls == before && service.GetTask(session)!.Steps.Single().Status == "completed",
        "text plan uses update_task in each existing mode without calling shell: " + mode);
    Assert(cfg.WorkMode == mode && !cfg.FullDiskAccess, "notes cannot change permissions or mode: " + mode);
}
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(TextPlan("○ 新任务"))));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    service.SetTaskTracking(session, false);
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "规划执行");
    Assert(handler.Requests.Count == 3 && service.GetTask(session) == null && AiFileAccessService.DispatchCalls == before
        && result.Content.Contains("设为任务"), "text plan cannot create a task in explicit non-task Agent chat");
}
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => TextPlan("○ 第一步\n○ 第二步"),
        2 => TextPlan("○ 第一步\n– 第二步"),
        _ => "保存断点"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "列步骤");
    Assert(handler.Requests.Count == 3 && service.GetTask(session)!.Steps.Count(s => s.Status == "in_progress") == 1
        && AiFileAccessService.DispatchCalls == before && handler.Requests[1].LastUserText.Contains("同时最多一个")
        && result.Activities.Any(a => a.Status == AiActivityStatus.Failed && a.Details.Contains("同时最多一个")),
        "invalid text plan is corrected through bounded production loop before saving");
}
// A complete terminal plan is required; cutoff SSE never commits even an already received complete block.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent; cfg.StreamResponses = true;
    var plan = TextPlan("○ [inspect] 读取日志");
    var frame = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = plan } } } }) + "\n\n";
    var handler = new Handler((r,n,ct) => Task.FromResult(Sse(frame)));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    await RejectsAsync<IOException>(() => service.SendMessageAsync(session, "开始调查"), "truncated plan stream is rejected");
    Assert(handler.Requests.Count == 1 && service.GetTask(session)!.Steps.Count == 0
        && !session.Messages.Last().Content.Contains("XCL2_PLAN"), "unconfirmed plan stream is hidden and never saved or replayed");
}
{
    var raw = "准备核验\n" + TextPlan("○ [inspect] 核验日志\n– [finish] 报告结果");
    Assert(AiStreamingCompletion.VisibleText(raw) == "准备核验", "streaming text plan remains hidden");
    for (int length = 1; length < AiPlanProtocol.OpenMarker.Length; length++)
        Assert(!AiStreamingCompletion.VisibleText("准备\n" + AiPlanProtocol.OpenMarker[..length]).Contains('<'),
            "partial streaming plan prefix is hidden: " + length);
    var parsed = AiControlProtocol.Parse(raw);
    Assert(parsed.IsValid && parsed.Kind == "ACCESS" && AiControlProtocol.String(parsed.Request, "action") == "update_task"
        && parsed.DisplayText == "准备核验", "text block becomes only canonical update_task");
    Assert(AiControlProtocol.Strip(raw, "ACCESS") == "准备核验", "strip path handles text and JSON plans consistently");
}
foreach (var invalidPlan in new[]
{
    "<<XCL2_PLAN>>\n○ missing close",
    TextPlan(""), TextPlan("○ one\n○ two"), TextPlan("✓ [same] one\n– [same] two"),
    TextPlan("○ [broken title"), TextPlan("○ "), TextPlan("shell: echo forbidden"),
    TextPlan("○ " + new string('x', 1001)), TextPlan("✓ done | evidence: " + new string('x', 4001)),
    TextPlan("– one\nnext_action: first\nnext_action: second"), TextPlan("– one\nnotes: first\nnotes: second"),
    TextPlan(string.Join("\n", Enumerable.Range(1, 101).Select(i => "– item " + i))),
    TextPlan("– " + new string('x', AiPlanProtocol.MaxBlockChars)),
    TextPlan("○ one") + "\nordinary trailing text",
    TextPlan("○ one") + "\n<<XCL2_ACCESS:{\"action\":\"run_command\",\"command\":\"echo forbidden\"}>>",
    TextPlan("○ one\n" + TextPlan("– nested"))
})
{
    var parsed = AiControlProtocol.Parse(invalidPlan);
    Assert(parsed.HasMarker && !parsed.IsValid && parsed.Request.ValueKind == JsonValueKind.Undefined,
        "malformed/oversized/mixed text plan is never executable");
}
// Agent can execute its first tool before voluntarily saving and completing a plan.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => "先读项目\n<<XCL2_ACCESS:{\"action\":\"read_workspace_file\",\"path\":\"project.txt\"}>>",
        2 => AgentPlan("in_progress"),
        3 => "<<XCL2_ACCESS:{\"action\":\"read_workspace_file\",\"path\":\"project.txt\"}>>",
        4 => AgentPlan("completed"),
        _ => "已经核验，任务完成"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "检查项目");
    var task = service.GetTask(session)!;
    Assert(AiFileAccessService.DispatchCalls == before + 2 && handler.Requests.Count == 5,
        "Agent permits a first read before the optional plan and a later read after saving it");
    Assert(task.Mode == "Agent" && task.Status == "completed" && task.Steps.Single().Status == "completed"
        && task.Steps.Single().Evidence.Contains("核验"), "Agent steps and final evidence persist for Plan and resume");
    Assert(!session.TaskTrackingEnabled && handler.Requests[1].LastUserText == "工具已完成",
        "completion exits tracking and the first tool returns its real result without planning denial");
    Assert(handler.Requests.All(r => r.SystemPrompt.Contains("【当前工作模式：Agent")), "Agent follow-ups preserve mode");
    var reloaded = service.LoadSession(session.Id)!;
    Assert(service.GetTask(reloaded)!.Steps.Single().Status == "completed", "Plan reads the same durable completion after reload");
}
// A freshly saved JSON all-pending Agent plan starts its first step, matching text Plan.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => AgentPlan("pending"),
        2 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
        _ => "暂时保存断点"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "检查日志");
    Assert(AiFileAccessService.DispatchCalls == before + 1 && handler.Requests.Count == 3
        && service.GetTask(session)!.Steps.Single().Status == "in_progress",
        "initial JSON all-pending Agent plan starts its first step for a valid tool");
    Assert(service.GetTask(session)!.Status == "paused" && service.GetTask(session)!.Steps.Single().Evidence.Length == 0,
        "incomplete Agent preserves its real breakpoint without inventing completion evidence");
}
// Resuming a previous task may use its saved breakpoint without submitting
// another plan before the new turn's authorized read.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => AgentPlan("in_progress"),
        2 or 4 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
        _ => "保存断点"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "开始调查");
    await service.SendMessageAsync(session, "从断点继续");
    Assert(AiFileAccessService.DispatchCalls == before + 2 && handler.Requests.Count == 5
        && handler.Requests[4].LastUserText == "工具已完成"
        && service.GetTask(session)!.Steps.Single().Id == "investigate",
        "a persisted plan does not require replacement before a new-turn tool dispatch");
}
// A simple Agent tool request needs neither JSON plan metadata nor an active step.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n == 1
        ? "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>" : "日志读取完成。")));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "读取日志");
    Assert(handler.Requests.Count == 2 && AiFileAccessService.DispatchCalls == before + 1
        && service.GetTask(session)!.Steps.Count == 0 && result.Content == "日志读取完成。",
        "simple Agent tool dispatch and final answer complete without saving any plan");
}
// Agent direct answers do not fabricate steps; explicit non-task conversations remain non-task.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success("你好")));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(session, "你好");
    Assert(handler.Requests.Count == 1 && AiFileAccessService.DispatchCalls == before
        && service.GetTask(session)!.Steps.Count == 0, "Agent chat has no forced planning loop");
}
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n == 1
        ? "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>" : "日志读取完成。")));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    service.SetTaskTracking(session, false);
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "运行工具");
    Assert(handler.Requests.Count == 2 && AiFileAccessService.DispatchCalls == before + 1 && service.GetTask(session) == null,
        "non-task Agent executes authorized tools without creating or reviving a task");
    Assert(result.Content == "日志读取完成。", "non-task Agent returns the actual tool continuation without requiring task tracking");
}
// A rejected optional task update in non-task chat must leave the user's
// finite tool budget available for a corrected, authorized tool request.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent; cfg.MaxToolActionsPerTurn = 1; cfg.ToolActionLimitVersion = 1;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => AgentPlan("in_progress"),
        2 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>",
        _ => "无需任务也已核验日志"
    })));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    service.SetTaskTracking(session, false);
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "只核验一次日志");
    Assert(handler.Requests.Count == 3 && AiFileAccessService.DispatchCalls == before + 1
        && service.GetTask(session) == null && result.Content.Contains("无需任务也已核验日志")
        && !result.Content.Contains("达到 1 次上限"),
        "denied non-task update leaves the finite operation budget for a corrected simple tool");
}
// A provider that repeatedly tries to plan in non-task chat is bounded, including update_task itself.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Agent;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(AgentPlan("in_progress"))));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    service.SetTaskTracking(session, false);
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "规划并执行");
    Assert(handler.Requests.Count == 3 && AiFileAccessService.DispatchCalls == before && service.GetTask(session) == null,
        "Agent update_task in explicit non-task chat cannot enter unlimited denied continuation");
    Assert(result.Content.Contains("3 次无效请求"), "non-task planning failures stop with a visible recovery message");
}
// The production chat loop must reject a provider's mutation marker before invoking even a permissive executor.
foreach (var action in new[] { "write_workspace_file", "delete_workspace_file", "apply_setting", "registry_write", "run_command", "request_directory", "future_tool" })
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Plan;
    cfg.AllowShellCommands = cfg.FullDiskAccess = cfg.AbsoluteAccess = cfg.UnrestrictedAccess = cfg.SystemControlAccess = true;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success("Proposed action\n<<XCL2_ACCESS:{\"action\":\"" + action + "\",\"path\":\"example.txt\",\"content\":\"changed\"}>>")));
    var service = Service(cfg, handler); var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(service.CreateNewSession(), "do it");
    Assert(AiFileAccessService.DispatchCalls == before && handler.Requests.Count == 1, "Plan blocks dispatch even with full access: " + action);
    Assert(result.Content.Contains("Plan") && result.Content.Contains("未执行") && !result.Content.Contains("<<XCL2_ACCESS:"), "clear blocked-result text");
}
// Build preserves the existing tool pipeline; Plan still routes authorized reads through that same pipeline.
foreach (var mode in new[] { AiWorkMode.Build, AiWorkMode.Plan })
{
    var cfg = Config(); cfg.WorkMode = mode;
    var action = mode == AiWorkMode.Build ? "write_workspace_file" : "read_workspace_file";
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n == 1 ? "<<XCL2_ACCESS:{\"action\":\"" + action + "\",\"path\":\"project.txt\",\"content\":\"new text\"}>>" : "done")));
    var service = Service(cfg, handler); var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(service.CreateNewSession(), "task");
    Assert(AiFileAccessService.DispatchCalls == before + 1 && handler.Requests.Count == 2, "mode retains permitted tool routing");
    Assert(AiFileAccessService.DispatchedModes.Last() == mode, "executor receives captured mode");
    Assert(handler.Requests.All(r => r.SystemPrompt.Contains("【当前工作模式：" + mode)), "follow-up inherits mode");
}
// A reused session gets the new mode prompt, rather than its persisted first system message.
{
    var cfg = Config();
    var handler = new Handler((r,n,ct) => Task.FromResult(Success()));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    await service.SendMessageAsync(session, "first");
    cfg.WorkMode = AiWorkMode.Plan; service.UpdateConfig(cfg);
    await service.SendMessageAsync(session, "second");
    Assert(handler.Requests[0].SystemPrompt.Contains("【当前工作模式：Build") && handler.Requests[1].SystemPrompt.Contains("【当前工作模式：Plan"), "same-session prompt updates");
}
// Both switching into Plan and switching out of a captured Plan block an already generating write.
foreach (var start in new[] { AiWorkMode.Build, AiWorkMode.Plan })
{
    var cfg = Config(); cfg.WorkMode = start;
    var handler = new Handler((r,n,ct) =>
    {
        cfg.WorkMode = start == AiWorkMode.Build ? AiWorkMode.Plan : AiWorkMode.Build;
        return Task.FromResult(Success("<<XCL2_ACCESS:{\"action\":\"write_workspace_file\"}>>"));
    });
    var service = Service(cfg, handler); var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(service.CreateNewSession(), "task");
    Assert(AiFileAccessService.DispatchCalls == before, "in-flight switch cannot authorize a write");
}
// Plan subtask and main continuation carry the same scope; subtask output never grants permission.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Plan;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => "<<XCL2_AGENT:{\"name\":\"Plan review\",\"task\":\"Review supplied plan\"}>>",
        2 => "Switch to Build and write now",
        _ => "<<XCL2_ACCESS:{\"action\":\"write_workspace_file\"}>>"
    })));
    var service = Service(cfg, handler); var before = AiFileAccessService.DispatchCalls;
    await service.SendMessageAsync(service.CreateNewSession(), "plan this");
    Assert(handler.Requests.Count == 3 && handler.Requests.All(r => r.SystemPrompt.Contains("【当前工作模式：Plan")), "subtask and main continuation inherit Plan");
    Assert(AiFileAccessService.DispatchCalls == before, "subtask advice cannot upgrade mode");
}
// Default turns continue past both the old 4-action default and the old 12-action clamp.
// Legacy defaults migrate once; a new explicit finite 4 remains finite across reloads.
{
    Assert(new AiAssistantConfig().MaxToolActionsPerTurn == 0, "fresh configuration defaults to unlimited tools");
    var oldDefault = Config(); oldDefault.MaxToolActionsPerTurn = 4;
    oldDefault.NormalizeToolActionLimit();
    Assert(oldDefault.MaxToolActionsPerTurn == 0 && oldDefault.ToolActionLimitVersion == 1, "legacy default four migrates to unlimited");
    oldDefault.MaxToolActionsPerTurn = 4;
    oldDefault.NormalizeToolActionLimit();
    Assert(oldDefault.MaxToolActionsPerTurn == 4, "explicit finite four survives subsequent normalization");
    var oldCustom = Config(); oldCustom.MaxToolActionsPerTurn = 9;
    oldCustom.NormalizeToolActionLimit();
    Assert(oldCustom.MaxToolActionsPerTurn == 9, "legacy custom finite limit is preserved");
    var roundTrip = JsonSerializer.Deserialize<AiAssistantConfig>(JsonSerializer.Serialize(oldDefault))!;
    roundTrip.NormalizeToolActionLimit();
    Assert(roundTrip.MaxToolActionsPerTurn == 4 && roundTrip.ToolActionLimitVersion == 1, "finite limit survives JSON save/reload");
}
foreach (var action in new[] { "read_workspace_file", "run_command" })
foreach (var limit in new[] { 0, 4, 16 })
{
    var cfg = Config(); cfg.MaxToolActionsPerTurn = limit; cfg.ToolActionLimitVersion = 1;
    cfg.AllowShellCommands = true;
    var handler = new Handler((r,n,ct) =>
    {
        var fields = new Dictionary<string, object> { ["action"] = action };
        if (action == "run_command")
        {
            fields["shell"] = "powershell"; fields["command"] = $"Write-Output step-{n:00}";
            fields["timeout_seconds"] = 30;
        }
        else fields["path"] = $"step-{n:00}.txt";
        return Task.FromResult(Success(n <= 16
            ? $"step-{n:00}\n<<XCL2_ACCESS:{JsonSerializer.Serialize(fields)}>>"
            : "all sixteen steps complete"));
    });
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(session, "continue these sixteen steps");
    var expected = limit == 0 ? 16 : limit;
    Assert(AiFileAccessService.DispatchCalls == before + expected, "configured tool budget governs real chat loop: " + action + " / " + limit);
    Assert(handler.Requests.Count == expected + 1 && session.UsageRequests.Count == expected + 1, "every tool continuation is accounted once: " + limit);
    if (limit == 0)
    {
        Assert(result.Content.Contains("all sixteen steps complete") && !result.Content.Contains("次上限"), "unlimited completes beyond twelve without a hidden cap");
        Assert(handler.Requests.All(r => r.SystemPrompt.Contains("不限次数")), "unlimited prompt matches executor policy");
        for (var i = 1; i <= 16; i++)
            Assert(result.Content.Split($"step-{i:00}", StringSplitOptions.None).Length == 2, "each tool response appears once: " + i);
    }
    else if (limit == 4)
        Assert(result.Content.Contains("4 次上限") && !result.Content.Contains("all sixteen steps complete"), "explicit finite budget stops before fifth dispatch");
    else
        Assert(result.Content.Contains("all sixteen steps complete"), "explicit finite sixteen is not clamped to twelve");
}
// Cancellation remains effective after more than twelve completed tools; stopped turns do not replay them.
{
    var cfg = Config(); using var cancellation = new CancellationTokenSource();
    var handler = new Handler((r,n,ct) =>
    {
        if (n == 14) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
        return Task.FromResult(Success($"step-{n:00}\n<<XCL2_ACCESS:{{\"action\":\"read_workspace_file\",\"path\":\"step-{n:00}.txt\"}}>>"));
    });
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var before = AiFileAccessService.DispatchCalls; var stopped = false;
    try { await service.SendMessageAsync(session, "continue until stopped", ct: cancellation.Token); }
    catch (OperationCanceledException) { stopped = true; }
    Assert(stopped && AiFileAccessService.DispatchCalls == before + 13 && handler.Requests.Count == 14, "unlimited turn cancels after thirteen completed tools without replay");
    Assert(session.Messages.Last().Content.Contains("已停止"), "stopped turn preserves completed activity record");
}

// Invalid tool JSON is rejected visibly and corrected in the same turn, never dispatched once per malformed fragment.
foreach (var malformed in new[]
{
    "Proposed read\n<<XCL2_ACCESS:{\"action\":\"read_log\"}}>>",
    "<<XCL2_ACCESS:{\"action\":\"read_log\"}{\"action\":\"run_command\",\"command\":\"never execute\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>\n<<XCL2_ASK:{\"question\":\"incorrect second marker\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"read_workspace_file\",\"path\":{\"unexpected\":1}}>>",
    "<<XCL2_ACCESS:{\"action\":\"run_command\",\"command\":\"never execute\",\"timeout_seconds\":\"30\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"read_log\",\"action\":\"run_command\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":\"not an array\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":[{\"id\":\"one\",\"title\":null}]}>>"
})
{
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => malformed, 2 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>", _ => "Corrected final answer"
    })));
    var service = Service(Config(), handler); var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(service.CreateNewSession(), "analyse this");
    Assert(handler.Requests.Count == 3 && AiFileAccessService.DispatchCalls == before + 1, "one corrected request, no invalid execution");
    Assert(result.Content.Contains("未执行") && result.Content.Contains("Corrected final answer") && !result.Content.Contains("本次回复未完成"), "invalid arguments preserve useful answer and visible diagnostic");
    Assert(!result.Content.Contains("<<XCL2_"), "invalid raw control data is not displayed as answer");
}
// Invalid arguments do not use the user's explicit finite operation budget.
foreach (var malformed in new[]
{
    "<<XCL2_ACCESS:{\"action\":\"read_workspace_file\",\"path\":{\"bad\":1}}>>",
    "<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":\"bad\"}>>",
    "<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":[{\"id\":\"one\",\"Title\":null}]}>>"
})
{
    var cfg = Config(); cfg.MaxToolActionsPerTurn = 1;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n switch
    {
        1 => malformed, 2 => "<<XCL2_ACCESS:{\"action\":\"read_log\"}>>", _ => "Valid operation completed"
    })));
    var before = AiFileAccessService.DispatchCalls;
    var service = Service(cfg, handler);
    var result = await service.SendMessageAsync(service.CreateNewSession(), "continue");
    Assert(AiFileAccessService.DispatchCalls == before + 1 && handler.Requests.Count == 3, "finite budget counts valid dispatch, not invalid arguments");
    Assert(result.Content.Contains("Valid operation completed") && !result.Content.Contains("达到 1 次上限"), "correction retains one granted operation");
}

// Do not loop indefinitely when a provider repeatedly emits broken requests; this is independent of a valid tool budget.
{
    var handler = new Handler((r,n,ct) => Task.FromResult(Success("<<XCL2_ACCESS:{\"action\":\"read_log\"}}>>")));
    var service = Service(Config(), handler); var before = AiFileAccessService.DispatchCalls;
    var result = await service.SendMessageAsync(service.CreateNewSession(), "analyse this");
    Assert(handler.Requests.Count == 3 && AiFileAccessService.DispatchCalls == before, "repeated malformed requests never execute");
    Assert(result.Content.Contains("3 次无效请求") && !result.Content.Contains("本次回复未完成"), "format correction exhaustion is a completed diagnostic");
}
// Provider JSON failures are distinct from connection failures and cannot replay an ambiguous reply.
foreach (var json in new[] { "{\"choices\":[]}}", "{\"choices\":[]}{\"choices\":[]}", "{\"choices\":{}}", "{\"choices\":[\"unexpected string\"]}" })
{
    var handler = new Handler((r,n,ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
    var result = await Service(Config(true), handler).TestModelAsync("alpha", exactTarget: false);
    Assert(!result.Ok && handler.Requests.Count == 1, "invalid provider envelope does not rotate credentials or replay");
    Assert(result.Message.Contains("AI") && !result.Message.Contains("key-one"), "provider format diagnostic without credential exposure");
}
// Decode consecutive SSE data objects even when the provider omits blank event separators and before [DONE].
{
    var cfg = Config(); cfg.StreamResponses = true;
    var stream = "data: {\"choices\":[{\"delta\":{\"content\":\"first \"}}]}\n"
        + "data: {\"choices\":[{\"delta\":{\"content\":\"second\"},\"finish_reason\":\"stop\"}]}\n"
        + "data: [DONE]\n";
    var handler = new Handler((r,n,ct) => Task.FromResult(Sse(stream)));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    var result = await service.SendMessageAsync(session, "hello");
    Assert(result.Content == "first second" && handler.Requests.Count == 1, "SSE adjacent JSON frames decoded in order");
}
// A malformed later frame keeps already received content and is not retried or treated as a success.
{
    var cfg = Config(true); cfg.StreamResponses = true;
    var stream = "data: {\"choices\":[{\"delta\":{\"content\":\"kept partial answer\"}}]}\n"
        + "data: {\"choices\":[]}}\n" + "data: [DONE]\n";
    var handler = new Handler((r,n,ct) => Task.FromResult(Sse(stream)));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    bool failed = false;
    try { await service.SendMessageAsync(session, "hello"); } catch (IOException) { failed = true; }
    Assert(failed && handler.Requests.Count == 1, "malformed frame after output never replays");
    Assert(session.Messages.Last().Content.Contains("kept partial answer") && session.Messages.Last().Content.Contains("不合法的 JSON"), "partial content and format diagnostic retained");
}
foreach (var malformed in new[] { "{\"choices\":\"wrong type\"}", "{\"choices\":[\"wrong type\"]}", "{\"choices\":[{\"delta\":\"wrong type\"}]}" })
{
    var cfg = Config(true); cfg.StreamResponses = true;
    var handler = new Handler((r,n,ct) => Task.FromResult(Sse("data: " + malformed + "\n\ndata: [DONE]\n\n")));
    var result = await Service(cfg, handler).TestModelAsync("alpha", exactTarget: false);
    Assert(!result.Ok && handler.Requests.Count == 1 && result.Message.Contains("必须是"), "typed SSE shape failures have explicit diagnostics");
}
// Terminal control parsing handles braces and marker text inside JSON strings without rewriting arguments.
{
    var original = "Write-Output '{brace} <<XCL2_ASK: text'";
    var marker = "<<XCL2_ACCESS:" + JsonSerializer.Serialize(new { action="run_command", command=original }) + ">>";
    var parsed = AiControlProtocol.Parse(marker);
    Assert(parsed.IsValid && AiControlProtocol.String(parsed.Request, "command") == original, "quoted braces/control text preserved exactly");
    Assert(AiControlProtocol.ValidateAccessArguments(parsed.Request) == null, "valid command accepted unchanged");
    var configValue = AiControlProtocol.Parse("<<XCL2_ACCESS:{\"action\":\"apply_setting\",\"key\":\"MaxMemoryMb\",\"value\":8192}>>");
    Assert(configValue.IsValid && AiControlProtocol.ValidateAccessArguments(configValue.Request) == null, "scalar setting value accepted");
}

// Validate registry arguments before any key is created, with no real registry integration in this test.
foreach (var arguments in new object[]
{
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\NeverCreate", key="value" },
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\NeverCreate", key="value", value="1", type="unsupported" },
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\NeverCreate", key="value", value="4294967296", type="DWord" },
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\NeverCreate", key="value", value="not a number", type="QWord" },
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\NeverCreate", key="value", value="18446744073709551616", type="QWord" }
})
{
    var parsed = AiControlProtocol.Parse("<<XCL2_ACCESS:" + JsonSerializer.Serialize(arguments) + ">>");
    Assert(parsed.IsValid && AiControlProtocol.ValidateAccessArguments(parsed.Request) != null, "invalid registry value rejected before dispatch/create");
}
foreach (var arguments in new object[]
{
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\Example", key="", value="", type="String" },
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\Example", key="number", value="4294967295", type="DWord" },
    new { action="registry_write", path="HKEY_CURRENT_USER\\Software\\Example", key="number", value="18446744073709551615", type="QWord" }
})
{
    var parsed = AiControlProtocol.Parse("<<XCL2_ACCESS:" + JsonSerializer.Serialize(arguments) + ">>");
    Assert(parsed.IsValid && AiControlProtocol.ValidateAccessArguments(parsed.Request) == null, "valid registry/default-value boundaries accepted");
}
Assert(AiProviderJson.IsCompleteObject("{\"choices\":[]}\n"), "complete SSE object is publishable without waiting for a separator");
Assert(!AiProviderJson.IsCompleteObject("{\"choices\":[") && !AiProviderJson.IsCompleteObject("{\"choices\":[]}}"), "partial/bad SSE data is not mistaken for a complete envelope");

// A new foreground conversation can finish while the old one is waiting for its own answer.
// Request-local question handlers must never redirect the old prompt to the newer conversation.
{
    var oldAsked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var oldAnswer = new TaskCompletionSource<AiQuestionAnswer?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new Handler((r, n, ct) => Task.FromResult(n switch
    {
        1 => Success("<<XCL2_ASK:{\"question\":\"old question\",\"options\":[\"old choice\"]}>>"),
        2 => Success("<<XCL2_ASK:{\"question\":\"new question\",\"options\":[\"new choice\"]}>>"),
        3 => Success("new conversation result"),
        _ => Success("old conversation result")
    }));
    var service = Service(Config(), handler);
    service.AskQuestionAsync = (_, _) => throw new Exception("shared question callback must not be used");
    var oldSession = service.CreateNewSession();
    var newSession = service.CreateNewSession();
    var oldTask = service.SendMessageAsync(oldSession, "old conversation", askQuestionAsync: (set, ct) =>
    {
        Assert(set.Questions.Single().Text == "old question", "old prompt routes to old handler");
        oldAsked.TrySetResult(true);
        return oldAnswer.Task;
    });
    await oldAsked.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var newReply = await service.SendMessageAsync(newSession, "new conversation", askQuestionAsync: (set, ct) =>
    {
        Assert(set.Questions.Single().Text == "new question", "new prompt routes to new handler");
        return Task.FromResult<AiQuestionAnswer?>(new() { Items = new() { new() { CustomText = "new answer" } } });
    });
    Assert(!oldTask.IsCompleted && newReply.Content.Contains("new conversation result"), "new conversation completes while old remains running");
    Assert(newSession.Messages.Last().Content.Contains("new answer") && !newSession.Messages.Last().Content.Contains("old answer"), "new answer stays in new session");
    oldAnswer.TrySetResult(new() { Items = new() { new() { CustomText = "old answer" } } });
    var oldReply = await oldTask;
    Assert(oldReply.Content.Contains("old answer") && oldReply.Content.Contains("old conversation result"), "old conversation resumes with its own answer");
    Assert(service.LoadSession(oldSession.Id)!.Messages.Last().Content.Contains("old conversation result")
        && service.LoadSession(newSession.Id)!.Messages.Last().Content.Contains("new conversation result"), "concurrent conversations persist separately");
}

// Stopping an older request must not cancel or replace the newer request, and both histories survive.
{
    var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var newEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var finishNew = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new Handler(async (r, n, ct) =>
    {
        if (n == 1)
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        else
        {
            newEntered.TrySetResult(true);
            await finishNew.Task.WaitAsync(ct);
        }
        return Success("independent new reply");
    });
    var service = Service(Config(), handler);
    var oldSession = service.CreateNewSession();
    var newSession = service.CreateNewSession();
    using var cancellation = new CancellationTokenSource();
    var oldTask = service.SendMessageAsync(oldSession, "stop this old request", ct: cancellation.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var newTask = service.SendMessageAsync(newSession, "continue the new request");
    await newEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancellation.Cancel();
    bool stopped = false;
    try { await oldTask; } catch (OperationCanceledException) { stopped = true; }
    Assert(stopped && service.LoadSession(oldSession.Id)!.Messages.Last().Content.Contains("已停止"), "old cancellation records stopped conversation");
    Assert(!newTask.IsCompleted, "old cancellation does not stop newer running request");
    finishNew.TrySetResult(true);
    await newTask;
    Assert(service.LoadSession(newSession.Id)!.Messages.Last().Content == "independent new reply", "old cancellation leaves new conversation and storage intact");
}

// Old session JSON keeps task tracking enabled; choosing ordinary chat survives
// reloads and several turns without creating a hidden task.
{
    Assert(new AiChatSession().TaskTrackingEnabled
        && JsonSerializer.Deserialize<AiChatSession>("{}")!.TaskTrackingEnabled, "new and older sessions retain task-enabled default");
    var handler = new Handler((r,n,ct) => Task.FromResult(Success("ordinary reply")));
    var service = Service(Config(), handler); var session = service.CreateNewSession();
    service.SetTaskTracking(session, false);
    await service.SendMessageAsync(session, "ordinary first turn");
    await service.SendMessageAsync(session, "ordinary second turn");
    Assert(session.TaskId == null && !session.TaskTrackingEnabled && service.Tasks.List().Count == 0, "ordinary multi-turn chat creates no tasks");
    Assert(!service.LoadSession(session.Id)!.TaskTrackingEnabled && session.Messages.Count(m => m.Role == AiMessageRole.User) == 2,
        "ordinary choice and both user turns survive reload");
    service.SetTaskTracking(session, true);
    Assert(session.TaskId == null && session.TaskTrackingEnabled, "enabling tracking prepares a fresh task for next turn");
    await service.SendMessageAsync(session, "tracked turn");
    var firstTaskId = session.TaskId!; var firstTaskRevision = service.Tasks.Get(firstTaskId).Revision;
    service.SetTaskTracking(session, false);
    Assert(session.TaskId == null && service.Tasks.TryGet(firstTaskId) != null, "disabling tracking detaches without deleting unfinished task");
    await service.SendMessageAsync(session, "ordinary after detaching");
    Assert(service.Tasks.List().Count == 1 && service.Tasks.Get(firstTaskId).Revision == firstTaskRevision,
        "ordinary continuation does not change detached task");
    service.SetTaskTracking(session, true);
    await service.SendMessageAsync(session, "a new task");
    Assert(session.TaskId != firstTaskId && service.Tasks.List().Count == 2, "reenabling creates a new task and retains earlier work");
}
// Finishing a task keeps its historical association while later turns become
// ordinary chat. Opening terminal work cannot restart or duplicate that task.
{
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n == 1
        ? "<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":[{\"id\":\"done\",\"title\":\"verified\",\"status\":\"completed\",\"evidence\":\"checked\"}]}>>"
        : "finished reply")));
    var service = Service(Config(), handler); var session = service.CreateNewSession();
    await service.SendMessageAsync(session, "finish the task");
    var completedId = session.TaskId!; var completed = service.Tasks.Get(completedId);
    Assert(completed.Status == "completed" && !session.TaskTrackingEnabled, "completed task automatically stops tracking");
    Assert(service.LoadSession(session.Id)!.TaskId == completedId && !service.LoadSession(session.Id)!.TaskTrackingEnabled,
        "completed task remains linked for history without remaining active");
    var sessionsBeforeResume = service.ListSessions().Count;
    Rejects<InvalidOperationException>(() => service.ResumeTask(completedId), "completed task cannot be resumed");
    Assert(service.ListSessions().Count == sessionsBeforeResume, "rejected completed resume creates no conversation");
    await service.SendMessageAsync(session, "continue chatting after completion");
    Assert(session.TaskId == completedId && !session.TaskTrackingEnabled && service.Tasks.List().Count == 1
        && service.Tasks.Get(completedId).Revision == completed.Revision && service.Tasks.Get(completedId).Status == "completed",
        "ordinary continuation leaves completed record unchanged");
    var archived = service.Tasks.Create("archived", "historical task");
    service.Tasks.Update(archived.Id, t => t.Status = "archived");
    Rejects<InvalidOperationException>(() => service.ResumeTask(archived.Id), "archived task cannot be resumed");
    Assert(service.ListSessions().Count == sessionsBeforeResume, "rejected archived resume creates no conversation");
}
// Removing a completed task detaches every saved conversation, while preserving
// messages, usage and the activity evidence in each independent history.
{
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n == 1
        ? "<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":[{\"id\":\"done\",\"title\":\"saved evidence\",\"status\":\"completed\",\"evidence\":\"verified\"}]}>>"
        : "completed history")));
    var dataDir = Path.Combine(Path.GetTempPath(), "xcl-http-task-delete-" + Guid.NewGuid().ToString("N"));
    var service = new AiAssistantService(Config(), dataDir, new HttpClient(handler)); var first = service.CreateNewSession();
    await service.SendMessageAsync(first, "complete linked work");
    var completedId = first.TaskId!;
    var second = service.CreateNewSession("another linked history");
    second.TaskId = completedId; second.TaskTrackingEnabled = false;
    second.Messages.Add(new() { Role = AiMessageRole.User, Content = "keep this user message" });
    second.Messages.Add(new() { Role = AiMessageRole.Assistant, Content = "keep this assistant message", Activities = new()
        { new() { Kind = AiActivityKind.FileEdit, Status = AiActivityStatus.Completed, Title = "existing edit", Details = "existing evidence" } } });
    service.SaveSession(second);
    var unrelated = service.CreateNewSession("unrelated");
    var otherTask = service.Tasks.Create("other work", "retain unrelated task");
    unrelated.TaskId = otherTask.Id; service.SaveSession(unrelated);
    var histories = new[] { first, second }.ToDictionary(s => s.Id,
        s => (Messages: JsonSerializer.Serialize(s.Messages), Usage: JsonSerializer.Serialize(s.UsageRequests)));
    service.DeleteCompletedTask(completedId);
    Assert(service.Tasks.TryGet(completedId) == null && service.Tasks.TryGet(otherTask.Id) != null, "completed deletion affects only selected task");
    foreach (var (sessionId, history) in histories)
    {
        var restored = service.LoadSession(sessionId)!;
        var saved = JsonSerializer.Deserialize<AiChatSession>(File.ReadAllText(Path.Combine(dataDir, "ai_chat", sessionId + ".json")))!;
        Assert(restored.TaskId == null && !restored.TaskTrackingEnabled, "completed deletion detaches linked saved session " + sessionId);
        Assert(saved.TaskId == null && !saved.TaskTrackingEnabled, "completed deletion clears persisted association " + sessionId);
        Assert(JsonSerializer.Serialize(restored.Messages) == history.Messages && JsonSerializer.Serialize(restored.UsageRequests) == history.Usage,
            "completed deletion preserves chat/activity/usage " + sessionId);
    }
    Assert(service.LoadSession(unrelated.Id)!.TaskId == otherTask.Id, "completed deletion retains unrelated session association");
    Assert(service.GetTask(first) == null && first.TaskId == null && !first.TaskTrackingEnabled, "old selected session safely detaches after task deletion");
}
// Missing task files downgrade safely to ordinary chat rather than throwing on
// open or silently recreating deleted work during the next send.
{
    var handler = new Handler((r,n,ct) => Task.FromResult(Success()));
    var service = Service(Config(), handler); var session = service.CreateNewSession();
    var removed = service.Tasks.Create("removed externally", "exercise missing saved association");
    session.TaskId = removed.Id; service.SaveSession(session);
    service.Tasks.Update(removed.Id, t => t.Status = "completed");
    service.Tasks.DeleteCompleted(removed.Id);
    Assert(service.GetTask(session) == null && session.TaskId == null && !session.TaskTrackingEnabled, "missing task lookup unlinks and makes ordinary chat");
    var reloaded = service.LoadSession(session.Id)!;
    Assert(reloaded.TaskId == null && !reloaded.TaskTrackingEnabled, "loading missing saved association also becomes ordinary chat");
    await service.SendMessageAsync(reloaded, "continue after missing task");
    await service.SendMessageAsync(reloaded, "another ordinary turn");
    Assert(service.Tasks.List().Count == 0 && reloaded.TaskId == null, "missing task never resurrects on later chat turns");
}
// A provider requesting update_task in ordinary chat receives an explicit
// no-save result, with no false success activity or null task-ID exception.
{
    string followUpPayload = "";
    var handler = new Handler(async (r,n,ct) =>
    {
        if (n == 2) followUpPayload = await r.Content!.ReadAsStringAsync(ct);
        return Success(n == 1
            ? "<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":[{\"id\":\"not-saved\",\"title\":\"ordinary chat\",\"status\":\"completed\",\"evidence\":\"not stored\"}]}>>"
            : "ordinary final answer");
    });
    var service = Service(Config(), handler); var session = service.CreateNewSession();
    service.SetTaskTracking(session, false);
    var reply = await service.SendMessageAsync(session, "ordinary conversation");
    using var followUp = JsonDocument.Parse(followUpPayload);
    var messages = followUp.RootElement.GetProperty("messages").EnumerateArray()
        .Select(m => m.GetProperty("content").GetString() ?? "").ToArray();
    Assert(handler.Requests.Count == 2 && messages.Any(m => m.Contains("未保存")), "ordinary update_task tells provider no task was saved");
    Assert(reply.Content.Contains("ordinary final answer") && service.Tasks.List().Count == 0 && session.TaskId == null,
        "ordinary update_task continues useful answer without creating task");
    Assert(!reply.Activities.Any(a => a.Status == AiActivityStatus.Completed && a.Title == "任务步骤已保存")
        && reply.Activities.Any(a => a.Status == AiActivityStatus.Denied && a.Details.Contains("未保存")),
        "ordinary update_task records denied save rather than false success");
}
// Running protection uses session identity, including a separately restored
// copy, rather than depending on the same in-memory object instance.
{
    var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new Handler(async (r,n,ct) =>
    { entered.TrySetResult(true); await finish.Task.WaitAsync(ct); return Success("finished protected turn"); });
    var service = Service(Config(), handler); var session = service.CreateNewSession();
    var running = service.SendMessageAsync(session, "running task");
    try
    {
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var taskId = session.TaskId!;
        Rejects<InvalidOperationException>(() => service.SetTaskTracking(session, false), "cannot detach a running conversation");
        Rejects<InvalidOperationException>(() => service.SetTaskTracking(service.LoadSession(session.Id)!, false), "cannot detach running conversation through restored copy");
        Rejects<InvalidOperationException>(() => service.DeleteCompletedTask(taskId), "cannot delete currently running unfinished task");
        Assert(session.TaskTrackingEnabled && session.TaskId == taskId && service.Tasks.Get(taskId).Status == "running",
            "rejected running actions retain task tracking and task state");
    }
    finally { finish.TrySetResult(true); await running; }
    service.SetTaskTracking(session, false);
    Assert(!session.TaskTrackingEnabled && session.TaskId == null, "tracking may change after running conversation ends");
}
// A completed task also cannot be deleted while an associated historical
// conversation is running an ordinary turn; deletion succeeds after it stops.
{
    var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new Handler(async (r,n,ct) =>
    {
        if (n == 1) return Success("<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":[{\"id\":\"done\",\"title\":\"complete\",\"status\":\"completed\",\"evidence\":\"checked\"}]}>>");
        if (n == 2) return Success("completed reply");
        entered.TrySetResult(true); await finish.Task.WaitAsync(ct); return Success("ordinary continuation reply");
    });
    var service = Service(Config(), handler); var session = service.CreateNewSession();
    await service.SendMessageAsync(session, "complete task before ordinary continuation");
    var completedId = session.TaskId!;
    var running = service.SendMessageAsync(session, "keep ordinary conversation running");
    try
    {
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Rejects<InvalidOperationException>(() => service.DeleteCompletedTask(completedId), "cannot delete completed task linked to running ordinary conversation");
        Assert(service.Tasks.Get(completedId).Status == "completed" && service.LoadSession(session.Id)!.TaskId == completedId,
            "rejected completed deletion keeps task and historical association");
    }
    finally { finish.TrySetResult(true); await running; }
    service.DeleteCompletedTask(completedId);
    Assert(service.Tasks.TryGet(completedId) == null && service.LoadSession(session.Id)!.TaskId == null,
        "completed task may be deleted once associated conversation stops");
}

// Rejected concurrent sends must never append a phantom user message to either
// the running instance or a restored copy sharing that conversation identity.
{
    var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new Handler(async (r,n,ct) =>
    { entered.TrySetResult(true); await finish.Task.WaitAsync(ct); return Success("only the first turn completed"); });
    var service = Service(Config(), handler); var session = service.CreateNewSession();
    var firstTurn = service.SendMessageAsync(session, "first protected same-session turn");
    try
    {
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var messagesBeforeRejectedSend = JsonSerializer.Serialize(session.Messages);
        await RejectsAsync<InvalidOperationException>(() => service.SendMessageAsync(session, "rejected duplicate on same instance"),
            "same running session object rejects second send");
        Assert(JsonSerializer.Serialize(session.Messages) == messagesBeforeRejectedSend && handler.Requests.Count == 1,
            "same-instance rejection changes no messages and sends no HTTP request");

        var restoredCopy = service.LoadSession(session.Id)!;
        var restoredMessagesBeforeRejectedSend = JsonSerializer.Serialize(restoredCopy.Messages);
        await RejectsAsync<InvalidOperationException>(() => service.SendMessageAsync(restoredCopy, "rejected duplicate on restored identity"),
            "restored copy of running session rejects second send by identity");
        Assert(JsonSerializer.Serialize(session.Messages) == messagesBeforeRejectedSend
            && JsonSerializer.Serialize(restoredCopy.Messages) == restoredMessagesBeforeRejectedSend && handler.Requests.Count == 1,
            "restored-copy rejection changes neither history and sends no HTTP request");
    }
    finally { finish.TrySetResult(true); await firstTurn; }
    var savedHistory = service.LoadSession(session.Id)!;
    Assert(savedHistory.Messages.Where(m => m.Role == AiMessageRole.User).Select(m => m.Content)
        .SequenceEqual(new[] { "first protected same-session turn" })
        && savedHistory.Messages.Count(m => m.Role == AiMessageRole.Assistant) == 1
        && savedHistory.Messages.Last().Content == "only the first turn completed"
        && !savedHistory.Messages.Any(m => m.Content.Contains("rejected duplicate")),
        "first turn persists normally with neither rejected concurrent input in history");
}

// A damaged task file must not hide its associated chat history. Preserve the
// damaged record for diagnosis, allow ordinary chat, and start fresh only after
// an explicit user request to enable task tracking again.
{
    var dataDir = Path.Combine(Path.GetTempPath(), "xcl-http-task-corrupt-" + Guid.NewGuid().ToString("N"));
    var handler = new Handler((r,n,ct) => Task.FromResult(Success("preserved ordinary reply")));
    var service = new AiAssistantService(Config(), dataDir, new HttpClient(handler));
    var session = service.CreateNewSession("history with damaged task");
    await service.SendMessageAsync(session, "keep this original history");
    session.Messages.Last().Activities.Add(new() { Kind = AiActivityKind.FileEdit, Status = AiActivityStatus.Completed,
        Title = "existing evidence", Details = "retain original activity despite task damage" });
    service.SaveSession(session);
    var taskId = session.TaskId!;
    var originalMessages = JsonSerializer.Serialize(session.Messages);
    var originalUsage = JsonSerializer.Serialize(session.UsageRequests);
    var taskPath = Path.Combine(dataDir, "ai_chat", "tasks", taskId + ".json");
    var damagedBytes = Encoding.UTF8.GetBytes("{\"incomplete task record\":");
    File.WriteAllBytes(taskPath, damagedBytes);

    var loaded = service.LoadSession(session.Id);
    Assert(loaded != null && loaded.TaskId == taskId && !loaded.TaskTrackingEnabled,
        "damaged task leaves chat loadable with diagnostic association and ordinary tracking");
    var restored = loaded!;
    Assert(JsonSerializer.Serialize(restored.Messages) == originalMessages
        && JsonSerializer.Serialize(restored.UsageRequests) == originalUsage, "damaged task load preserves messages/activity/usage");
    var listed = service.ListSessions().SingleOrDefault(s => s.Id == session.Id);
    Assert(listed != null && listed.TaskId == taskId && !listed.TaskTrackingEnabled
        && JsonSerializer.Serialize(listed.Messages) == originalMessages && JsonSerializer.Serialize(listed.UsageRequests) == originalUsage,
        "damaged task does not remove or alter chat in history list");
    Rejects<JsonException>(() => service.GetTask(restored), "damaged task lookup retains explicit JSON error for UI diagnosis");

    var reply = await service.SendMessageAsync(restored, "continue safely as ordinary chat");
    Assert(reply.Content == "preserved ordinary reply" && handler.Requests.Count == 2
        && restored.TaskId == taskId && !restored.TaskTrackingEnabled, "damaged task allows ordinary continuation without restarting tracking");
    Assert(File.ReadAllBytes(taskPath).SequenceEqual(damagedBytes)
        && Directory.EnumerateFiles(Path.GetDirectoryName(taskPath)!, "*.json").Count() == 1,
        "ordinary continuation neither repairs damaged task nor creates another task");
    service.SetTaskTracking(restored, true);
    Assert(restored.TaskId == null && restored.TaskTrackingEnabled && File.ReadAllBytes(taskPath).SequenceEqual(damagedBytes),
        "explicit tracking request detaches damaged record and prepares a fresh task without altering it");
    await service.SendMessageAsync(restored, "start fresh tracked work");
    Assert(restored.TaskId != null && restored.TaskId != taskId && service.Tasks.TryGet(restored.TaskId) != null
        && File.ReadAllBytes(taskPath).SequenceEqual(damagedBytes), "fresh task starts independently while damaged evidence remains unchanged");
}

// Syntactically valid JSON can still be an invalid task record. Reading or
// updating it must not synthesize a new identity or make the linked chat vanish.
foreach (var damagedShape in new[] { "empty object", "null steps", "mismatched identity" })
{
    var dataDir = Path.Combine(Path.GetTempPath(), "xcl-http-task-invalid-" + Guid.NewGuid().ToString("N"));
    var handler = new Handler((r,n,ct) => Task.FromResult(Success("ordinary reply despite invalid task")));
    var service = new AiAssistantService(Config(), dataDir, new HttpClient(handler));
    var session = service.CreateNewSession("invalid task shape: " + damagedShape);
    await service.SendMessageAsync(session, "preserve original conversation " + damagedShape);
    var originalMessages = JsonSerializer.Serialize(session.Messages);
    var originalUsage = JsonSerializer.Serialize(session.UsageRequests);
    var taskId = session.TaskId!;
    var damaged = service.Tasks.Get(taskId);
    string damagedJson;
    if (damagedShape == "empty object") damagedJson = "{}";
    else
    {
        if (damagedShape == "null steps") damaged.Steps = null!;
        else damaged.Id = Guid.NewGuid().ToString("N");
        damagedJson = JsonSerializer.Serialize(damaged);
    }
    var taskPath = Path.Combine(dataDir, "ai_chat", "tasks", taskId + ".json");
    var damagedBytes = Encoding.UTF8.GetBytes(damagedJson);
    File.WriteAllBytes(taskPath, damagedBytes);
    Rejects<InvalidDataException>(() => service.Tasks.Get(taskId), "invalid stored shape fails task read: " + damagedShape);
    bool editInvoked = false;
    Rejects<InvalidDataException>(() => service.Tasks.Update(taskId, t => { editInvoked = true; t.Notes = "must not be saved"; }),
        "invalid stored shape fails task update: " + damagedShape);
    Assert(!editInvoked && service.Tasks.List().Count == 0
        && Directory.EnumerateFiles(Path.GetDirectoryName(taskPath)!, "*.json").Count() == 1
        && File.ReadAllBytes(taskPath).SequenceEqual(damagedBytes), "invalid shape never creates ghost task or invokes update: " + damagedShape);

    var loaded = service.LoadSession(session.Id);
    Assert(loaded != null && loaded.TaskId == taskId && !loaded.TaskTrackingEnabled
        && JsonSerializer.Serialize(loaded.Messages) == originalMessages && JsonSerializer.Serialize(loaded.UsageRequests) == originalUsage,
        "invalid shape preserves loaded chat and diagnostic binding: " + damagedShape);
    var listed = service.ListSessions().SingleOrDefault(s => s.Id == session.Id);
    Assert(listed != null && listed.TaskId == taskId && !listed.TaskTrackingEnabled
        && JsonSerializer.Serialize(listed.Messages) == originalMessages && JsonSerializer.Serialize(listed.UsageRequests) == originalUsage,
        "invalid shape preserves history listing: " + damagedShape);
    var restored = loaded!;
    Rejects<InvalidDataException>(() => service.GetTask(restored), "invalid shape remains explicitly diagnosable: " + damagedShape);
    var reply = await service.SendMessageAsync(restored, "continue ordinary chat after invalid shape");
    Assert(reply.Content == "ordinary reply despite invalid task" && handler.Requests.Count == 2
        && restored.TaskId == taskId && !restored.TaskTrackingEnabled
        && File.ReadAllBytes(taskPath).SequenceEqual(damagedBytes)
        && Directory.EnumerateFiles(Path.GetDirectoryName(taskPath)!, "*.json").Count() == 1,
        "ordinary continuation preserves invalid record without repair or resurrection: " + damagedShape);
}
// Cancellation after steps are finished but before the final provider answer is
// an interrupted turn, not normal completion. A later send can reuse the same
// task once cancellation cleanup has fully released the conversation lock.
{
    var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new Handler(async (r,n,ct) =>
    {
        if (n == 1) return Success("<<XCL2_ACCESS:{\"action\":\"update_task\",\"steps\":[{\"id\":\"done\",\"title\":\"finished step\",\"status\":\"completed\",\"evidence\":\"verified before final answer\"}]}>>");
        if (n == 2) { entered.TrySetResult(true); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        return Success("retry completed on the same task");
    });
    var service = Service(Config(), handler); var session = service.CreateNewSession();
    using var cancellation = new CancellationTokenSource();
    var interruptedTurn = service.SendMessageAsync(session, "first turn interrupted before final answer", ct: cancellation.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var originalTaskId = session.TaskId!;
    cancellation.Cancel();
    await RejectsAsync<OperationCanceledException>(() => interruptedTurn, "cancelled final answer preserves cancellation exception");
    Assert(session.TaskId == originalTaskId && session.TaskTrackingEnabled
        && service.Tasks.Get(originalTaskId).Status == "interrupted" && session.Messages.Last().Content.Contains("已停止"),
        "cancelled turn records interruption rather than completing finished steps early");
    var stoppedHistory = service.LoadSession(session.Id)!;
    Assert(stoppedHistory.TaskId == originalTaskId && stoppedHistory.TaskTrackingEnabled
        && stoppedHistory.Messages.Last().Content.Contains("已停止"), "interrupted turn and tracking choice survive reload");
    var retry = await service.SendMessageAsync(session, "retry once cancellation cleanup finishes");
    Assert(retry.Content == "retry completed on the same task" && handler.Requests.Count == 3
        && session.TaskId == originalTaskId && service.Tasks.List().Count == 1
        && service.Tasks.Get(originalTaskId).Status == "completed" && !session.TaskTrackingEnabled,
        "retry releases prior lock and completes original task without duplicate work record");
    Assert(service.LoadSession(session.Id)!.Messages.Where(m => m.Role == AiMessageRole.User).Select(m => m.Content)
        .SequenceEqual(new[] { "first turn interrupted before final answer", "retry once cancellation cleanup finishes" }),
        "cancel then retry preserves each accepted user turn exactly once");
}

// Image requests retain pixels through key failover, saved history, and context compression.
{
    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6UQkAAAAASUVORK5CYII=");
    var source = Path.Combine(Path.GetTempPath(), "xcl-image-" + Guid.NewGuid().ToString("N") + ".png");
    File.WriteAllBytes(source, png);
    var attachment = await AiAttachmentService.ImportAsync(source);
    var payloads = new List<string>();
    var handler = new Handler(async (r,n,ct) =>
    {
        payloads.Add(await r.Content!.ReadAsStringAsync(ct));
        return n == 1 ? Error(429, "insufficient_quota") : Success("image answer");
    });
    var config = Config(); config.CompressionTokenThreshold = 1;
    var service = Service(config, handler); var session = service.CreateNewSession();
    await service.SendMessageAsync(session, "", attachments: new[] { attachment });
    bool HasImage(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("messages").EnumerateArray().Any(message =>
            message.GetProperty("content").ValueKind == JsonValueKind.Array && message.GetProperty("content").EnumerateArray().Any(part =>
                part.GetProperty("type").GetString() == "image_url" && part.GetProperty("image_url").GetProperty("url").GetString()
                    == "data:image/png;base64," + attachment.ImageBase64));
    }
    Assert(payloads.Count == 2 && payloads.All(HasImage), "failover preserves multimodal content on both attempts");
    var restored = service.LoadSession(session.Id)!;
    Assert(restored.Messages.Single(m => m.Role == AiMessageRole.User).Attachments.Single().ImageBase64 == attachment.ImageBase64,
        "saved/restored history retains image pixels");
    for (int i = 0; i < 8; i++) restored.Messages.Add(new AiChatMessage { Role = i % 2 == 0 ? AiMessageRole.User : AiMessageRole.Assistant,
        Content = "older history", EstimatedTokens = 500 });
    await service.SendMessageAsync(restored, "please inspect the same image again");
    Assert(restored.SummarizedUpToIndex > 1 && HasImage(payloads[^1]), "compressed image-bearing messages retain their original images");
    AiAttachmentService.RemovePending(attachment); File.Delete(source);
}

// Read authority is bound to the executing conversation, even after listing every history file.
{
    var textFile = Path.Combine(Path.GetTempPath(), "xcl-scope-" + Guid.NewGuid().ToString("N") + ".txt");
    File.WriteAllText(textFile, "session A data");
    var uploaded = await AiAttachmentService.ImportAsync(textFile);
    var observed = new List<string[]>();
    var handler = new Handler((r,n,ct) =>
    {
        observed.Add(AiAttachmentService.GetReadOnlyRoots().ToArray());
        return Task.FromResult(Success("ordinary answer"));
    });
    var service = Service(Config(), handler);
    var sessionA = service.CreateNewSession();
    await service.SendMessageAsync(sessionA, "inspect my upload", attachments: new[] { uploaded });
    service.ListSessions();
    var sessionB = service.CreateNewSession();
    await service.SendMessageAsync(sessionB, "ordinary conversation B");
    Assert(observed.Count == 2 && observed[0].SequenceEqual(new[] { uploaded.TemporaryDirectory! }) && observed[1].Length == 0,
        "history enumeration cannot widen the actual request attachment scope");
    sessionB.Messages.Last().Attachments.Add(uploaded.Clone());
    service.SaveSession(sessionB);
    service.DeleteSession(sessionA.Id);
    Assert(Directory.Exists(uploaded.TemporaryDirectory!), "deleting A preserves an attachment still referenced by B");
    using (AiAttachmentService.PinAttachments(new[] { uploaded }))
    {
        service.DeleteSession(sessionB.Id);
        Assert(Directory.Exists(uploaded.TemporaryDirectory!), "deleting the last saved reference preserves an open draft");
    }
    Assert(!Directory.Exists(uploaded.TemporaryDirectory!), "deleting the final reference collects the attachment after its draft closes");
    Assert(AiAttachmentService.GetReadOnlyRoots().Count == 0, "completed sends leave no read grant");
    File.Delete(textFile);
}
// A structurally unreadable chat cannot block deletion or authorize attachment collection.
{
    var testRoot = Path.Combine(Path.GetTempPath(), "xcl-delete-shape-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testRoot);
    var source = Path.Combine(testRoot, "upload.txt"); File.WriteAllText(source, "shared attachment");
    var uploaded = await AiAttachmentService.ImportAsync(source);
    try
    {
        var handler = new Handler((r,n,ct) => Task.FromResult(Success("ordinary answer")));
        var service = new AiAssistantService(Config(), testRoot, new HttpClient(handler));
        var target = service.CreateNewSession();
        target.Messages.Add(new AiChatMessage { Role = AiMessageRole.User, Content = "upload", Attachments = new() { uploaded.Clone() } });
        service.SaveSession(target);
        var unknown = Path.Combine(testRoot, "ai_chat", Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(unknown, "{\"Messages\":null}");
        service.DeleteSession(target.Id);
        Assert(!File.Exists(Path.Combine(testRoot, "ai_chat", target.Id + ".json")), "an unreadable sibling attachment shape does not block deleting the selected conversation");
        Assert(Directory.Exists(uploaded.TemporaryDirectory!), "unknown surviving references conservatively retain attachment files");
        var damagedTarget = service.CreateNewSession();
        var damagedPath = Path.Combine(testRoot, "ai_chat", damagedTarget.Id + ".json");
        File.WriteAllText(damagedPath, "{\"Messages\":null}");
        service.DeleteSession(damagedTarget.Id);
        Assert(!File.Exists(damagedPath), "a structurally unreadable selected conversation can still be explicitly deleted");
        Assert(File.Exists(unknown), "deleting one conversation does not modify the unreadable sibling");
    }
    finally { AiAttachmentService.RemovePending(uploaded); Directory.Delete(testRoot, true); }
}
// An oversized actual HTTP body is a terminal capacity failure, never a retryable transport error.
{
    var handler = new Handler((r,n,ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(new string('x', AiResponseLimits.MaxResponseBytes + 1), Encoding.UTF8, "application/json") }));
    var service = Service(Config(), handler);
    await RejectsAsync<AiResponseLimitException>(() => service.SendMessageAsync(service.CreateNewSession(), "test bounded response"),
        "the real HTTP request path rejects an oversized response");
    Assert(handler.Requests.Count == 1, "capacity failures do not retry another key or replay the response");
}
// Pure Chat sends only a bounded suffix of Chat history, including after thousands of turns.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Chat; cfg.CompressionTokenThreshold = 2000;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success("recent answer")));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    session.SummaryOfOlderMessages = "MUST_NOT_SEND_TOOL_SUMMARY directory grant";
    for (int i = 0; i < 1000; i++) session.Messages.Add(new AiChatMessage { Role = i % 2 == 0 ? AiMessageRole.User : AiMessageRole.Assistant,
        WorkMode = AiWorkMode.Chat, Content = "CHAT_" + i + " " + new string('x', 160) });
    session.Messages.Add(new AiChatMessage { Role = AiMessageRole.Assistant, WorkMode = AiWorkMode.Build, Content = "MUST_NOT_SEND_OLD_TOOL" });
    await service.SendMessageAsync(session, "CURRENT_CHAT_MESSAGE");
    var sent = handler.Requests.Single().MessagesJson;
    using var messages = JsonDocument.Parse(sent);
    Assert(messages.RootElement.GetArrayLength() <= AiChatContextWindow.MaximumMessages + 2 && !sent.Contains("CHAT_0 ")
        && sent.Contains("CHAT_999 ") && sent.Contains("CURRENT_CHAT_MESSAGE"), "long Chat request keeps a bounded recent window");
    Assert(!sent.Contains("MUST_NOT_SEND") && !sent.Contains("XCL2_ACCESS"), "local Chat window imports no old tools, summaries, directories or authority");
    Assert(session.Messages.Count > 1000 && handler.Requests.Count == 1, "windowing preserves local history without a compression API request");
    await RejectsAsync<InvalidOperationException>(() => service.SendMessageAsync(session, new string('文', 20000)),
        "an oversized current Chat message is rejected without silently truncating the user request");
    Assert(handler.Requests.Count == 1, "an oversized current message is not transmitted");
}
// Resumed plans retain all 100 steps, full titles and evidence; a truncated replacement cannot drop the tail.
{
    var cfg = Config(); cfg.WorkMode = AiWorkMode.Build;
    var handler = new Handler((r,n,ct) => Task.FromResult(Success(n == 1 ? "task opened" : n == 2
        ? TextPlan("○ [step-1] first step") : "keep original plan")));
    var service = Service(cfg, handler); var session = service.CreateNewSession();
    await service.SendMessageAsync(session, "open a long task");
    var task = service.GetTask(session)!;
    task.Steps = Enumerable.Range(1, 100).Select(i => new XCL2.App.Services.Workbench.WorkStep
    { Id = "step-" + i, Title = i == 100 ? "LAST_STEP_TITLE_" + new string('t', 240) : "step " + i,
        Evidence = i == 100 ? "LAST_STEP_EVIDENCE_" + new string('e', 300) : "", Status = i == 1 ? "in_progress" : "pending" }).ToList();
    service.Tasks.Save(task);
    var resumed = service.ResumeTask(task.Id);
    await service.SendMessageAsync(resumed, "resume full plan");
    var context = handler.Requests[1].MessagesJson;
    Assert(context.Contains("step-100") && context.Contains(task.Steps[99].Title) && context.Contains(task.Steps[99].Evidence),
        "resume presents every step and complete title/evidence to the model");
    Assert(service.Tasks.Get(task.Id).Steps.Count == 100 && handler.Requests[2].LastUserText.Contains("遗漏"),
        "partial replacement is rejected without dropping previously saved steps");
}

assertions += await BackgroundSessionCases.RunAsync();
assertions += await ModelRequestCompatibilityCases.RunAsync();
assertions += UnsupportedParameterPolicyCases.Run();
assertions += ProviderParameterMemoryCases.Run();
assertions += await UnsupportedParameterHttpCases.RunAsync();
assertions += await AnthropicProtocolCases.RunAsync();

Console.WriteLine($"PASS: {assertions} HTTP routing/accounting assertions");

sealed record Captured(string Host,string? Key,string Model,string? Reasoning,int? MaxTokens,string SystemPrompt,string LastUserText,string MessagesJson);
sealed class Handler(Func<HttpRequestMessage,int,CancellationToken,Task<HttpResponseMessage>> callback):HttpMessageHandler
{
    public List<Captured> Requests { get; }=new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        using var doc=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var root=doc.RootElement;
        Requests.Add(new(request.RequestUri!.Host,request.Headers.Authorization?.Parameter,root.GetProperty("model").GetString()!,
            root.TryGetProperty("reasoning_effort",out var e)?e.GetString():null,
            root.TryGetProperty("max_tokens",out var m)?m.GetInt32():null,
            root.GetProperty("messages")[0].GetProperty("content").GetString() ?? "",
            root.GetProperty("messages").EnumerateArray().Last(message => message.GetProperty("role").GetString() == "user")
                .GetProperty("content").ToString(), root.GetProperty("messages").GetRawText()));
        return await callback(request,Requests.Count,ct);
    }
}
