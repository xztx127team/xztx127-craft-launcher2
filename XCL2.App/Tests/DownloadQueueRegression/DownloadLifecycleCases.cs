using System.Collections.Concurrent;
using XCL2.App.Services;

internal static class DownloadLifecycleCases
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    public static async Task RunAsync()
    {
        var queue = DownloadQueueService.Instance;
        queue.Config ??= new();
        queue.Config.DownloadTaskConcurrency = 0;
        var root = Path.Combine(Path.GetTempPath(), "xcl2-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // A queued progress callback must neither replace the stopping state nor cross a resume boundary.
            var callbacks = new HeldProgressContext();
            IProgress<ProgressInfo>? oldProgress = null;
            IProgress<string>? oldTextProgress = null;
            IProgress<ProgressInfo>? newProgress = null;
            var writerStopping = Signal();
            var writerMayExit = Signal();
            var resumedReady = Signal();
            var part = Path.Combine(root, "payload.jar.part");
            var installed = Path.Combine(root, "installed.jar");
            var unrelatedPart = Path.Combine(root, "unrelated.part");
            File.WriteAllText(installed, "verified complete file");
            File.WriteAllText(unrelatedPart, "another task's cache");
            var attempt = 0;
            var item = queue.StartNew("stop/resume lifecycle", async (entry, ct) =>
            {
                if (Interlocked.Increment(ref attempt) == 1)
                {
                    (oldProgress, oldTextProgress) = callbacks.Capture(entry);
                    using var ownership = await DownloadTargetLock.AcquireAsync(part[..^5], ct);
                    File.WriteAllText(part, "partial payload");
                    DownloadTransferPolicy.TrackTemporary(part, true);
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    finally
                    {
                        writerStopping.TrySetResult();
                        await writerMayExit.Task.WaitAsync(Deadline);
                        File.AppendAllText(part, " last write before actual exit");
                    }
                }
                else
                {
                    (newProgress, _) = callbacks.Capture(entry);
                    resumedReady.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
            }, "lifecycle-target", root);
            var unrelatedGate = Signal();
            var unrelated = queue.StartNew("unrelated active task", async (_, _) => await unrelatedGate.Task.WaitAsync(Deadline));
            queue.Pause(item);
            Require(item.Status == DownloadQueueItemStatus.Pausing && item.IsStopping && item.IsRunning,
                "Pause did not immediately expose an honest stopping state");
            Require(item.StateReason.Contains("用户请求暂停") && !item.CanResume, "Stopping reason or control state missing");
            oldProgress!.Report(new ProgressInfo("stale download progress", 99, 100, "old payload"));
            oldTextProgress!.Report("stale shell line");
            callbacks.Drain();
            Require(item.StatusText.Contains("正在暂停") && !item.StatusText.Contains("stale"), "Late progress overwrote stopping state");
            await writerStopping.Task.WaitAsync(Deadline);
            Require(!item.Completion.IsCompleted && File.Exists(part) && item.IsRunning,
                "Pause cleaned or claimed completion before the old writer actually exited");
            writerMayExit.TrySetResult();
            Require(await item.Completion.WaitAsync(Deadline) == DownloadQueueItemStatus.Paused, "Pause did not settle after writer exit");
            Require(!item.IsRunning && !item.IsCleaningUp && item.CanResume && !File.Exists(part), "Paused cache was not automatically cleared");
            Require(unrelated.IsRunning && File.Exists(unrelatedPart) && File.ReadAllText(installed) == "verified complete file",
                "Cleanup waited for unrelated work or deleted a complete/unregistered file");
            Require(item.StateReason.Contains("已清理本任务临时文件") && item.StateReason.Contains("完整文件保留"),
                "Cleanup outcome only appeared in a notification");

            // Explicit cleanup also scopes to one stopped task while another download remains active.
            var extra = Path.Combine(root, "extra.jar.part");
            File.WriteAllText(extra, "extra partial"); item.TransferObservation.Temporary(extra, true);
            Require(await queue.CleanupRecordedPartsAsync(item) == 1 && !File.Exists(extra) && unrelated.IsRunning,
                "Manual task cleanup still requires every download to stop");

            queue.Resume(item);
            await resumedReady.Task.WaitAsync(Deadline);
            newProgress!.Report(new ProgressInfo("new attempt", 1, 10, "new payload"));
            callbacks.Drain();
            var currentText = item.StatusText;
            oldProgress.Report(new ProgressInfo("stale old attempt", 100, 100, "bad"));
            oldTextProgress.Report("stale old shell attempt"); callbacks.Drain();
            Require(item.StatusText == currentText && currentText.Contains("new attempt"), "Old attempt progress contaminated resumed execution");
            queue.Cancel(item);
            Require(item.Status == DownloadQueueItemStatus.Canceling && item.IsStopping, "Cancel prematurely reported a terminal result");
            Require(await item.Completion.WaitAsync(Deadline) == DownloadQueueItemStatus.Canceled, "Resumed cancellation failed");
            unrelatedGate.TrySetResult(); await unrelated.Completion.WaitAsync(Deadline);

            // Socket/plugin cancellation callbacks may block; Cancel must return before those callbacks finish.
            var callbackEntered = Signal();
            var callbackMayExit = Signal();
            var blockedCallbackItem = queue.StartNew("slow cancellation callback", async (_, ct) =>
            {
                using var registration = ct.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    callbackMayExit.Task.GetAwaiter().GetResult();
                });
                await Task.Delay(Timeout.Infinite, ct);
            });
            try
            {
                var cancelReturned = Task.Run(() => queue.Cancel(blockedCallbackItem));
                await callbackEntered.Task.WaitAsync(Deadline);
                await cancelReturned.WaitAsync(TimeSpan.FromSeconds(2));
                Require(blockedCallbackItem.IsStopping && blockedCallbackItem.IsRunning && !blockedCallbackItem.Completion.IsCompleted,
                    "Queue ignored still-running cancellation callbacks");
            }
            finally { callbackMayExit.TrySetResult(); }
            Require(await blockedCallbackItem.Completion.WaitAsync(Deadline) == DownloadQueueItemStatus.Canceled,
                "Cancellation callbacks stranded the task");

            // An OCE from HttpClient's own timeout is a failure, never a fictional user cancellation.
            var timeout = queue.StartNew("network timeout", (_, _) => Task.FromException(new TaskCanceledException("idle timeout fixture", new TimeoutException("server did not respond"))));
            Require(await timeout.Completion.WaitAsync(Deadline) == DownloadQueueItemStatus.Failed && timeout.LastError is TimeoutException,
                "Unrequested network timeout was mislabeled as cancellation");
            Require(timeout.StateReason.Contains("超时") && timeout.StateReason.Contains("idle timeout fixture"), "Timeout cause not visible on task");
            queue.Cancel(timeout);
            var unknownStop = queue.StartNew("unexpected operation stop", (_, _) => Task.FromException(new OperationCanceledException("unexpected stopped fixture")));
            Require(await unknownStop.Completion.WaitAsync(Deadline) == DownloadQueueItemStatus.Failed &&
                unknownStop.StateReason.Contains("unexpected stopped fixture") && !unknownStop.StateReason.Contains("超时"),
                "An unknown OCE invented either user intent or a timeout");
            queue.Cancel(unknownStop);
            var deniedInstall = queue.StartNew("declined elevation", (_, _) => Task.FromException(new DownloadUserCanceledException("用户未授予管理员权限 fixture")));
            Require(await deniedInstall.Completion.WaitAsync(Deadline) == DownloadQueueItemStatus.Canceled &&
                deniedInstall.StateReason.Contains("未授予管理员权限 fixture") && !deniedInstall.StateReason.Contains("超时"),
                "Explicit installer cancellation lost its real cause");
            queue.Cancel(deniedInstall);
            var failure = queue.StartNew("nested download failure", (_, _) => Task.FromException(
                new IOException("all sources failed", new InvalidDataException("SHA256 fixture mismatch"))));
            await failure.Completion.WaitAsync(Deadline);
            Require(failure.StateReason.Contains("SHA256 fixture mismatch"), "Wrapped failure hid its actual cause");
            queue.Cancel(failure);

            // Retry and target contention reasons update without a progress report or toast.
            var retryStarted = Signal();
            var retry = queue.StartNew("retry reason", async (_, ct) =>
            {
                var delay = DownloadTransferPolicy.WaitRetryAsync(TimeSpan.FromSeconds(30), ct, "server returned 429 fixture");
                retryStarted.TrySetResult(); await delay;
            });
            await retryStarted.Task.WaitAsync(Deadline);
            Require(retry.StateReason.Contains("429 fixture") && retry.StateReason.Contains("等待重试"), "Retry reason requires a progress callback");
            queue.Cancel(retry); await retry.Completion.WaitAsync(Deadline);
            var lockTarget = Path.Combine(root, "contended.jar");
            var lockHeld = await DownloadTargetLock.AcquireAsync(lockTarget, CancellationToken.None);
            try
            {
                var waiting = queue.StartNew("target contention", async (_, ct) =>
                {
                    using var phase = DownloadTransferPolicy.BeginPhase("等待文件占用");
                    using var lease = await DownloadTargetLock.AcquireAsync(lockTarget, ct);
                });
                Require(waiting.StateReason.Contains("其他下载任务或进程"), "File ownership wait has no task explanation");
                queue.Cancel(waiting); await waiting.Completion.WaitAsync(Deadline);
            }
            finally { lockHeld.Dispose(); }

            // Finishing an old pause must never complete a freshly resumed attempt through a mutable TCS.
            queue.Config.DownloadTaskConcurrency = 1;
            var queueBlockerGate = Signal();
            var queueBlocker = queue.StartNew("pause reentry blocker", async (_, _) => await queueBlockerGate.Task.WaitAsync(Deadline),
                supportsPause: false, supportsCancel: false);
            var reentrant = queue.StartNew("resume from cleanup notification", (_, _) => Task.CompletedTask);
            var oldCompletion = reentrant.Completion;
            Task<DownloadQueueItemStatus>? resumedCompletion = null;
            var didResume = false;
            reentrant.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(DownloadQueueItem.IsCleaningUp) && !reentrant.IsCleaningUp && !didResume)
                {
                    didResume = true; queue.Resume(reentrant); resumedCompletion = reentrant.Completion;
                }
            };
            Require(queue.PauseAllSafe() == 1, "Reentrant pause fixture selected an unsafe task");
            Require(await oldCompletion.WaitAsync(Deadline) == DownloadQueueItemStatus.Paused, "Old waiting pause lost its outcome");
            Require(didResume && resumedCompletion != null && resumedCompletion != oldCompletion && !resumedCompletion.IsCompleted,
                "Old cleanup completed a new waiting attempt's completion source");
            queueBlockerGate.TrySetResult();
            await queueBlocker.Completion.WaitAsync(Deadline);
            Require(await resumedCompletion!.WaitAsync(Deadline) == DownloadQueueItemStatus.Completed, "New resumed attempt did not run exactly once");
            queue.Config.DownloadTaskConcurrency = 0;

            // An unconfirmable path is retained for retry, rather than reported as already removed.
            var unconfirmable = Path.Combine(root, "not-a-regular-file.part");
            Directory.CreateDirectory(unconfirmable);
            var inaccessible = new DownloadTransferObservation(); inaccessible.Temporary(unconfirmable, true);
            var retained = await inaccessible.DeleteRecordedTemporaryFilesAsync();
            Require(retained.Deleted == 0 && retained.Retained == 1 && inaccessible.HasRecordedTemporaryFiles &&
                inaccessible.Summary().Contains("保留 1 个"), "Unavailable temporary path was falsely forgotten as missing");
            Directory.Delete(unconfirmable); File.WriteAllText(unconfirmable, "now a regular temporary file");
            Require((await inaccessible.DeleteRecordedTemporaryFilesAsync()).Deleted == 1, "Retained registration could not be retried");

            // A newer task registering the same .part owns it, even if the older task once recorded that name.
            var shared = Path.Combine(root, "shared.jar.part");
            var previousOwner = new DownloadTransferObservation(); var latestOwner = new DownloadTransferObservation();
            previousOwner.Temporary(shared, true); latestOwner.Temporary(shared, true); File.WriteAllText(shared, "new owner's bytes");
            var rejected = await previousOwner.DeleteRecordedTemporaryFilesAsync();
            Require(rejected.Deleted == 0 && rejected.Retained == 1 && File.Exists(shared), "Old task deleted a newer task's cache");
            Require((await latestOwner.DeleteRecordedTemporaryFilesAsync()).Deleted == 1 && !File.Exists(shared), "Current owner cannot clean its own cache");

            // The recent-file display cap must not disable temporary tracking in long asset downloads.
            var longTransfer = new DownloadTransferObservation();
            for (var number = 0; number < 2100; number++)
            {
                var destination = Path.Combine(root, "completed-" + number + ".jar");
                longTransfer.Temporary(destination + ".part", true); longTransfer.Completed(destination);
            }
            var late = Path.Combine(root, "late.jar.part");
            File.WriteAllText(late, "late partial"); longTransfer.Temporary(late, true);
            Require((await longTransfer.DeleteRecordedTemporaryFilesAsync()).Deleted == 1 && !File.Exists(late),
                "Completed temporary registrations exhausted the cap and disabled late cleanup");
            Require(longTransfer.CompletedFiles == 2100 && longTransfer.CompletedPaths().Length == 100,
                "Temporary tracking change broke the bounded recent-file list");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class HeldProgressContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        public (IProgress<ProgressInfo>, IProgress<string>) Capture(DownloadQueueItem item)
        {
            var previous = Current; SetSynchronizationContext(this);
            try { return (item.CreateProgress(), item.CreateTextProgress()); }
            finally { SetSynchronizationContext(previous); }
        }
        public void Drain() { while (_callbacks.TryDequeue(out var callback)) callback.Callback(callback.State); }
    }
}
