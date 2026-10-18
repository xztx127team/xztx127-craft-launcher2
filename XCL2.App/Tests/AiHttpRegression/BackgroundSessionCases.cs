using System.Net;
using System.Text;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class BackgroundSessionCases
{
    private const string Command = "<<XCL2_ACCESS:{\"action\":\"run_command\",\"shell\":\"powershell\",\"command\":\"Write-Output background\"}>>";

    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("Background session: " + message);
        }

        // Switching a new foreground conversation to Chat/Plan must not silently
        // downgrade an older Build request at its next tool boundary.
        foreach (var foregroundMode in new[] { AiWorkMode.Chat, AiWorkMode.Plan })
        {
            var entered = Signal();
            var release = Signal();
            var cfg = Configuration();
            var oldMode = AiWorkMode.Build;
            var handler = new CallbackHandler(async (number, ct) =>
            {
                if (number == 1)
                {
                    entered.TrySetResult(true);
                    await release.Task.WaitAsync(ct);
                    return Success(Command);
                }
                return Success(number == 2 ? "new foreground completed" : "old background completed");
            });
            var service = Service(cfg, handler);
            var oldSession = service.CreateNewSession();
            using var oldCancellation = new CancellationTokenSource();
            var before = AiFileAccessService.DispatchCalls;
            var oldTask = service.SendMessageAsync(oldSession, "old background", ct: oldCancellation.Token,
                currentWorkMode: () => oldMode);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cfg.WorkMode = foregroundMode;
            service.UpdateConfig(cfg);
            var foreground = service.CreateNewSession();
            var newReply = await service.SendMessageAsync(foreground, "new foreground", currentWorkMode: () => foregroundMode);
            Check(newReply.Content == "new foreground completed" && !oldTask.IsCompleted && !oldCancellation.IsCancellationRequested,
                "new conversation completes without cancelling its background predecessor: " + foregroundMode);
            release.TrySetResult(true);
            var oldReply = await oldTask.WaitAsync(TimeSpan.FromSeconds(5));
            Check(AiFileAccessService.DispatchCalls == before + 1 && AiFileAccessService.DispatchedModes.Last() == AiWorkMode.Build,
                "background conversation keeps its selected execution mode: " + foregroundMode);
            Check(oldReply.Content.Contains("old background completed") && service.LoadSession(oldSession.Id)!.Messages.Last().Content.Contains("old background completed")
                && service.LoadSession(foreground.Id)!.Messages.Last().Content == "new foreground completed",
                "background and foreground results persist independently: " + foregroundMode);
        }

        // Changing the actual running conversation still tightens its authority,
        // even when the shared selection belongs to a different Build conversation.
        foreach (var tightenedMode in new[] { AiWorkMode.Plan, AiWorkMode.Chat })
        {
            var entered = Signal();
            var release = Signal();
            var mode = AiWorkMode.Build;
            var handler = new CallbackHandler(async (_, ct) =>
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(ct);
                return Success(Command);
            });
            var service = Service(Configuration(), handler);
            var before = AiFileAccessService.DispatchCalls;
            var task = service.SendMessageAsync(service.CreateNewSession(), "tighten this conversation", currentWorkMode: () => mode);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mode = tightenedMode;
            release.TrySetResult(true);
            var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(AiFileAccessService.DispatchCalls == before && result.Content.Contains(AiWorkModePolicy.GetDeniedMessage(tightenedMode)),
                "explicit changes to the owning conversation still prevent execution: " + tightenedMode);
        }

        // A per-conversation callback never bypasses the read-only crash-analysis boundary.
        {
            var handler = new CallbackHandler((_, _) => Task.FromResult(Success(Command)));
            var service = Service(Configuration(), handler);
            service.ForceReadOnly = true;
            var before = AiFileAccessService.DispatchCalls;
            await service.SendMessageAsync(service.CreateNewSession(), "read-only analysis", currentWorkMode: () => AiWorkMode.Build);
            Check(AiFileAccessService.DispatchCalls == before, "ForceReadOnly still takes precedence over a session's Build selection");
        }

        // Static path/permission helpers inherit only the calling async request's
        // mode. A nested request and a concurrent child cannot leak it to siblings.
        {
            var global = AiWorkMode.Chat;
            Check(AiWorkModeContext.ResolveCurrentMode(global) == AiWorkMode.Chat, "outside a request helpers use the configured mode");
            var mode = AiWorkMode.Build;
            var context = new AiWorkModeContext(mode, () => mode);
            using (context.EnterScope())
            {
                await Task.Yield();
                Check(AiWorkModeContext.ResolveCurrentMode(global) == AiWorkMode.Build, "request mode survives an await despite foreground Chat");
                await Task.Run(async () =>
                {
                    var child = new AiWorkModeContext(AiWorkMode.Chat, () => AiWorkMode.Chat);
                    using (child.EnterScope())
                    {
                        await Task.Yield();
                        Check(AiWorkModeContext.ResolveCurrentMode(AiWorkMode.Build) == AiWorkMode.Chat, "nested Chat does not inherit Build grants");
                    }
                    Check(AiWorkModeContext.ResolveCurrentMode(global) == AiWorkMode.Build, "nested request disposal restores its inherited mode");
                });
                Check(AiWorkModeContext.ResolveCurrentMode(global) == AiWorkMode.Build, "a child scope cannot replace its parent's mode");
                mode = AiWorkMode.Plan;
                Check(AiWorkModeContext.ResolveCurrentMode(global) == AiWorkMode.Plan, "owning session can tighten static helpers during a run");
                mode = AiWorkMode.Build;
                Check(AiWorkModeContext.ResolveCurrentMode(global) == AiWorkMode.Plan, "a running request cannot regain authority after tightening");
            }
            Check(AiWorkModeContext.ResolveCurrentMode(global) == AiWorkMode.Chat, "request disposal restores the surrounding global mode");
        }

        // Cancelling a newer foreground request does not reach the retained old token.
        {
            var oldEntered = Signal();
            var newEntered = Signal();
            var releaseOld = Signal();
            var handler = new CallbackHandler(async (number, ct) =>
            {
                if (number == 1)
                {
                    oldEntered.TrySetResult(true);
                    await releaseOld.Task.WaitAsync(ct);
                    return Success("retained background result");
                }
                newEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Success("unreachable");
            });
            var service = Service(Configuration(), handler);
            using var oldCancellation = new CancellationTokenSource();
            using var newCancellation = new CancellationTokenSource();
            var oldTask = service.SendMessageAsync(service.CreateNewSession(), "retained", ct: oldCancellation.Token, currentWorkMode: () => AiWorkMode.Build);
            await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var newTask = service.SendMessageAsync(service.CreateNewSession(), "stop foreground", ct: newCancellation.Token, currentWorkMode: () => AiWorkMode.Chat);
            await newEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            newCancellation.Cancel();
            var cancelled = false;
            try { await newTask; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !oldTask.IsCompleted && !oldCancellation.IsCancellationRequested, "stopping the foreground request cannot cancel the retained background run");
            releaseOld.TrySetResult(true);
            Check((await oldTask.WaitAsync(TimeSpan.FromSeconds(5))).Content == "retained background result", "retained request finishes after foreground cancellation");
        }
        return checks;
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AiAssistantConfig Configuration() => new()
    {
        UseCustomApiKey = true, BaseUrl = "https://background.example/v1", ApiKey = "test-key",
        RoutingMode = AiRoutingMode.SpecificModel, SelectedModel = "background-test", WorkMode = AiWorkMode.Build,
        StreamResponses = false, MaxOutputTokens = 64,
        CustomModels = new() { new() { Id = "background-test", DisplayName = "Background test", ProviderId = "custom" } }
    };
    private static AiAssistantService Service(AiAssistantConfig config, HttpMessageHandler handler) => new(config,
        Path.Combine(Path.GetTempPath(), "xcl-background-" + Guid.NewGuid().ToString("N")), new HttpClient(handler));
    private static HttpResponseMessage Success(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } }), Encoding.UTF8, "application/json")
    };
    private sealed class CallbackHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        private int _requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => callback(Interlocked.Increment(ref _requests), ct);
    }
}
