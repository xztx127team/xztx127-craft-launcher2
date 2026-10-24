using XCL2.App.Services;
using System.Text.Json;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
var queue = DownloadQueueService.Instance;
var folder = Path.GetFullPath("queue-regression-minecraft");

// Awaiting a prerequisite returns only the actual result and does not grant background retry.
var successGate = Signal();
var success = queue.RunAndWaitAsync("Java prerequisite", async (_, _) => { await successGate.Task.WaitAsync(TimeSpan.FromSeconds(10)); return 42; });
var successItem = queue.Items.Last();
Require(!success.IsCompleted && successItem.IsRunning, "Prerequisite finished too early");
Require(!successItem.CanPause && !successItem.CanResume, "Awaited operation offered pause/retry");
successItem.RegisterInstallationTarget("dynamic-target");
try
{
    queue.StartNew("conflicting dynamic target", (_, _) => Task.CompletedTask, "dynamic-target", folder);
    throw new Exception("Dynamically registered version allowed a concurrent writer");
}
catch (InvalidOperationException) { }
successGate.SetResult(true);
Require(await success == 42 && successItem.Status == DownloadQueueItemStatus.Completed, "Wrong successful result");

// Failed prerequisite must throw and remain visibly failed, with no background replay.
try
{
    await queue.RunAndWaitAsync<int>("broken prerequisite", (_, _) => Task.FromException<int>(new IOException("fixture failure")));
    throw new Exception("Failure was returned as success");
}
catch (IOException ex) when (ex.Message == "fixture failure") { }
var failed = queue.Items.Last();
Require(failed.Status == DownloadQueueItemStatus.Failed && !failed.CanResume, "Failed prerequisite was retryable");

// Canceling a version task keeps its reservation until cleanup actually finishes.
var cleanupStarted = Signal();
var allowCleanup = Signal();
using var callerCts = new CancellationTokenSource();
var canceled = queue.RunAndWaitAsync("repair 1.20.1", async (_, ct) =>
{
    try { await Task.Delay(Timeout.Infinite, ct); return true; }
    finally { cleanupStarted.TrySetResult(true); await allowCleanup.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
}, callerCts.Token, "1.20.1", folder);
var canceledItem = queue.Items.Last();
callerCts.Cancel();
await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
Require(!canceled.IsCompleted && canceledItem.IsRunning, "Cancellation returned before cleanup");
Require(queue.FindActiveForVersion(folder + Path.DirectorySeparatorChar, "1.20.1") == canceledItem,
    "Canceled-but-writing task lost its reservation");
try
{
    queue.StartNew("conflicting install", (_, _) => Task.CompletedTask, "1.20.1", folder);
    throw new Exception("Concurrent repair/install was accepted");
}
catch (InvalidOperationException) { }
allowCleanup.SetResult(true);
try { await canceled; throw new Exception("Canceled prerequisite was successful"); }
catch (OperationCanceledException) { }
Require(canceledItem.Status == DownloadQueueItemStatus.Canceled && !canceledItem.IsRunning, "Wrong canceled state");
Require(queue.FindActiveForVersion(folder, "1.20.1") == null, "Finished task retained reservation");

// A running installer cannot be paused, canceled, or replayed by download controls.
var installGate = Signal();
var installer = queue.StartNew("driver installer", async (_, _) => await installGate.Task,
    supportsPause: false, supportsRetry: false, supportsCancel: false);
Require(!installer.CanPauseOrResume && !installer.CanCancel, "Unsafe installer controls were enabled");
queue.Cancel(installer);
Require(installer.IsRunning && installer.Status == DownloadQueueItemStatus.Downloading, "Installer was falsely canceled");
installGate.SetResult(true);
Require(await installer.Completion == DownloadQueueItemStatus.Completed, "Installer did not complete");
Require(installer.CanCancel && installer.CancelLabel == "移除", "Terminal installer cannot be dismissed");

// Real pause waits for cleanup, and resume starts a distinct attempt with a fresh completion.
var pauseCleanup = Signal();
var pauseObserved = Signal();
var attempts = 0;
var resumable = queue.StartNew("pause fixture", async (_, ct) =>
{
    if (++attempts > 1) return;
    try { await Task.Delay(Timeout.Infinite, ct); }
    finally { pauseObserved.SetResult(true); await pauseCleanup.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
}, "pause-version", folder);
queue.Pause(resumable);
await pauseObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
Require(!resumable.CanResume, "Resume allowed before old writer stopped");
pauseCleanup.SetResult(true);
Require(await resumable.Completion == DownloadQueueItemStatus.Paused && resumable.CanResume, "Pause state incorrect");
queue.Resume(resumable);
Require(await resumable.Completion == DownloadQueueItemStatus.Completed && attempts == 2, "Resume did not start one fresh attempt");

// Runtime adapters reuse their parent queue item across awaits, without leaking context into callers.
Require(queue.CurrentItem == null, "Queue context leaked into the caller");
var parentReady = Signal();
var releaseParent = Signal();
DownloadQueueItem? observedParent = null;
var nestedJava = new JavaService
{
    Download = async (_, _, _) =>
    {
        observedParent = queue.CurrentItem;
        parentReady.SetResult(true);
        await releaseParent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(queue.CurrentItem == observedParent, "Parent context was lost across await");
        return "nested-javaw.exe";
    }
};
var parent = queue.RunAndWaitAsync("parent runtime pipeline", async (item, ct) =>
{
    Require(queue.CurrentItem == item, "Current item not set for pipeline");
    return await JavaDownloadQueueService.DownloadAsync(nestedJava, new JavaDownloadRequest(8, "x64", JavaInstallMode.Portable), ct: ct);
});
await parentReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
Require(queue.CurrentItem == null, "Active pipeline polluted another async flow");
Require(queue.Items.Count(i => i.IsRunning) == 1 && observedParent?.Name == "parent runtime pipeline", "Nested Java registered a duplicate item");
releaseParent.SetResult(true);
Require(await parent == "nested-javaw.exe" && queue.CurrentItem == null, "Nested result/context incorrect");

// Direct Java downloads are ordinary awaited entries in the same manager.
var directGate = Signal();
var directJava = new JavaService { Download = async (_, _, _) => { await directGate.Task.WaitAsync(TimeSpan.FromSeconds(10)); return "direct-javaw.exe"; } };
var directResult = JavaDownloadQueueService.DownloadAsync(directJava, new JavaDownloadRequest(21, "x64", JavaInstallMode.Portable));
var directItem = queue.Items.Last();
Require(directItem.Name == "下载 Java 21 (x64)" && !directItem.CanPause && !directItem.CanResume, "Direct Java missing unified queue semantics");
directGate.SetResult(true);
Require(await directResult == "direct-javaw.exe" && directItem.Status == DownloadQueueItemStatus.Completed, "Direct Java result lost");

// A system-Java package remains cancellable while it is only downloading.
var systemDownloadReady = Signal();
var systemJava = new JavaService
{
    Download = async (_, _, ct) =>
    {
        systemDownloadReady.SetResult(true);
        await Task.Delay(Timeout.Infinite, ct);
        throw new Exception("Canceled system download reached installer");
    }
};
var systemDownload = JavaDownloadQueueService.DownloadAsync(systemJava,
    new JavaDownloadRequest(21, "x64", JavaInstallMode.System));
await systemDownloadReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
var systemDownloadItem = queue.Items.Last();
Require(systemDownloadItem.CanCancel, "System Java download disabled cancellation before installation");
queue.Cancel(systemDownloadItem);
try { await systemDownload; throw new Exception("Canceled system Java download succeeded"); }
catch (OperationCanceledException) { }
Require(systemDownloadItem.Status == DownloadQueueItemStatus.Canceled, "System Java cancellation not reflected in queue");

// Caller cancellation during an irreversible installer waits for its real exit before finishing.
var installerEntered = Signal();
var installerExit = Signal();
using var installerCts = new CancellationTokenSource();
var guardedInstall = queue.RunAndWaitAsync("guarded runtime installer", async (item, _) =>
{
    await item.RunInstallerPhaseAsync(async () =>
    {
        installerEntered.SetResult(true);
        await installerExit.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return 0;
    });
    installerCts.Token.ThrowIfCancellationRequested();
    return true;
}, installerCts.Token);
await installerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
var guardedItem = queue.Items.Last();
Require(!guardedItem.CanPauseOrResume && !guardedItem.CanCancel, "Installer controls were not disabled");
installerCts.Cancel();
Require(!guardedInstall.IsCompleted && guardedItem.IsRunning, "Installer cancellation claimed an early exit");
installerExit.SetResult(true);
try { await guardedInstall; throw new Exception("Canceled installer continued caller"); }
catch (OperationCanceledException) { }
Require(!guardedItem.IsRunning && guardedItem.Status == DownloadQueueItemStatus.Canceled, "Installer final state incorrect");
Require(guardedItem.SupportsCancel && guardedItem.CanCancel, "Installer did not restore its controls");

// Failure also restores a parent pipeline's phase controls.
await queue.RunAndWaitAsync("phase recovery", async (item, _) =>
{
    try { await item.RunInstallerPhaseAsync<int>(() => throw new IOException("phase failure")); }
    catch (IOException) { }
    Require(item.SupportsCancel && item.CanCancel, "Failed installer left parent locked");
    return true;
});
// ZIP and MSI come from distinct official metadata. Never apply a ZIP checksum to an invented MSI URL.
using var metadata = JsonDocument.Parse(JsonSerializer.Serialize(new
{
    package = new { link = "https://github.com/adoptium/fixture/releases/download/v1/java.zip", name = "java.zip", checksum = new string('a', 64), size = 123L },
    installer = new { link = "https://github.com/adoptium/fixture/releases/download/v1/setup.msi", name = "setup.msi", checksum = new string('b', 64), size = 456L }
}));
var zip = JavaDownloadSource.FromAdoptiumBinary(metadata.RootElement, false);
var msi = JavaDownloadSource.FromAdoptiumBinary(metadata.RootElement, true);
Require(zip.FileName == "java.zip" && zip.Sha256 == new string('a', 64) && zip.Size == 123, "ZIP metadata lost");
Require(msi.FileName == "setup.msi" && msi.Sha256 == new string('b', 64) && msi.Size == 456, "MSI used ZIP metadata");
using var noInstaller = JsonDocument.Parse("{\"package\":{}}");
try { JavaDownloadSource.FromAdoptiumBinary(noInstaller.RootElement, true); throw new Exception("MSI URL was guessed"); }
catch (InvalidOperationException) { }
using var noHash = JsonDocument.Parse("{\"installer\":{\"link\":\"https://example.com/setup.msi\",\"name\":\"setup.msi\"}}");
try { JavaDownloadSource.FromAdoptiumBinary(noHash.RootElement, true); throw new Exception("Unverifiable MSI was accepted"); }
catch (InvalidOperationException) { }
// Restoring a checkpoint must only register a paused download, never execute it.
var recoveryRoot = Path.Combine(Path.GetTempPath(), "xcl2-queue-recovery-" + Guid.NewGuid().ToString("N"));
var recoveryFolder = Path.Combine(recoveryRoot, "minecraft");
Directory.CreateDirectory(recoveryFolder);
try
{
    var recoveryDirectory = Path.Combine(recoveryRoot, "checkpoints");
    var request = new DownloadRecoveryRequest(recoveryFolder, "1.0", "https://launchermeta.mojang.com/fixture.json");
    using (var previous = new DownloadRecoveryStore(recoveryDirectory))
        previous.Save(new[] { new DownloadRecoveryRecord(Guid.NewGuid(), "paused fixture", request) });
    var executions = 0;
    queue.Config = new XCL2.App.Models.AppConfig();
    queue.InitializeRecovery(new DownloadRecoveryStore(recoveryDirectory),
        value => value.IsAllowed(new[] { recoveryFolder }), (_, _, _) => { executions++; return Task.CompletedTask; });
    Require(await queue.RestoreInterruptedAsync() == 1, "Interrupted download not registered");
    var restored = queue.Items.Last();
    Require(executions == 0 && !restored.IsRunning && restored.Status == DownloadQueueItemStatus.Paused,
        "Restore auto-executed a download");
    Require(restored.CanResume && queue.FindActiveForVersion(recoveryFolder, "1.0") == restored,
        "Paused recovered task lost its version reservation");
    queue.Resume(restored);
    Require(await restored.Completion == DownloadQueueItemStatus.Completed && executions == 1,
        "Explicit continue did not execute exactly once");
    queue.Resume(restored);
    Require(executions == 1, "Completed recovery was replayed");
}
finally { queue.CloseRecovery(); Directory.Delete(recoveryRoot, true); }
// A configured task limit creates real waiting work; only those jobs can be reordered.
queue.Config!.DownloadTaskConcurrency = 1;
var schedulerGate = Signal();
var blocker = queue.StartNew("scheduler blocker", async (_, _) => await schedulerGate.Task);
var executionOrder = new List<string>();
var waitingFirst = queue.StartNew("waiting first", (_, _) => { executionOrder.Add("first"); return Task.CompletedTask; }, "queued-version", folder);
var waitingSecond = queue.StartNew("waiting second", (_, _) => { executionOrder.Add("second"); return Task.CompletedTask; });
Require(waitingFirst.Status == DownloadQueueItemStatus.Waiting && !waitingFirst.IsRunning && !waitingFirst.Completion.IsCompleted, "Queue limit did not create honest waiting state");
Require(queue.FindActiveForVersion(folder, "queued-version") == waitingFirst, "Waiting installer did not reserve its target");
queue.MoveWaiting(waitingSecond, -1);
queue.MoveWaiting(blocker, 1); // Running work cannot be reordered or interrupted.
Require(blocker.IsRunning, "Reordering changed running work");
schedulerGate.SetResult(true);
await Task.WhenAll(blocker.Completion, waitingFirst.Completion, waitingSecond.Completion);
Require(executionOrder.SequenceEqual(new[] { "second", "first" }), "Visible queue order was not execution order");

// Canceling a prerequisite before its turn must complete its caller and never run its action.
var cancelWaitingGate = Signal();
var cancelWaitingBlocker = queue.StartNew("waiting cancellation blocker", async (_, _) => await cancelWaitingGate.Task);
var ranCanceled = false;
var pendingPrerequisite = queue.RunAndWaitAsync("pending prerequisite", (_, _) => { ranCanceled = true; return Task.FromResult(true); });
var pendingEntry = queue.Items.Last();
Require(pendingEntry.Status == DownloadQueueItemStatus.Waiting, "Prerequisite bypassed configured queue");
queue.Cancel(pendingEntry);
try { await pendingPrerequisite; throw new Exception("Pending cancellation returned success"); }
catch (OperationCanceledException) { }
Require(!ranCanceled && pendingEntry.Completion.IsCompleted && pendingEntry.StatusText.Contains("本次执行尚未开始"), "Canceled pending task executed, stranded its caller, or misstated prior writes");
cancelWaitingGate.SetResult(true); await cancelWaitingBlocker.Completion;

// Retrying failed tasks excludes completed, canceled and non-replayable prerequisite entries.
var retryAttempts = 0;
var replayable = queue.StartNew("retry fixture", (_, _) => ++retryAttempts == 1
    ? Task.FromException(new IOException("retry once")) : Task.CompletedTask);
await replayable.Completion;
Require(queue.RetryFailed() == 1, "Retry-failed included non-replayable or nonfailed work");
await replayable.Completion;
Require(retryAttempts == 2 && replayable.Status == DownloadQueueItemStatus.Completed, "Failed retry did not execute once");
queue.Config.DownloadTaskConcurrency = 0;

// Dismissing a terminal row preserves its actual outcome and emits no second status/history event.
foreach (var expected in new[] { DownloadQueueItemStatus.Completed, DownloadQueueItemStatus.Failed, DownloadQueueItemStatus.Canceled })
{
    var terminal = queue.StartNew("dismiss " + expected, (_, token) => expected switch
    {
        DownloadQueueItemStatus.Failed => Task.FromException(new IOException("terminal failure")),
        DownloadQueueItemStatus.Canceled => Task.Delay(Timeout.Infinite, token),
        _ => Task.CompletedTask
    });
    if (expected == DownloadQueueItemStatus.Canceled) queue.Cancel(terminal);
    Require(await terminal.Completion == expected, "Terminal dismissal fixture ended incorrectly");
    var originalText = terminal.StatusText;
    var originalCompletion = terminal.Completion;
    var statusNotifications = 0;
    terminal.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(DownloadQueueItem.Status)) statusNotifications++; };
    queue.Cancel(terminal);
    Require(!queue.Items.Contains(terminal) && terminal.Status == expected && terminal.StatusText == originalText,
        "Removing a terminal row changed its real outcome");
    Require(statusNotifications == 0 && terminal.Completion == originalCompletion && await terminal.Completion == expected,
        "Terminal removal emitted another status/history event or changed completion");
}

// A network buffer larger than the token bucket must finish rather than wait forever.
var limiter = new DownloadRateLimiter(65536);
await limiter.ConsumeAsync(81920).WaitAsync(TimeSpan.FromSeconds(3));
using (var rateCancel = new CancellationTokenSource(100))
{
    try { await new DownloadRateLimiter(1).ConsumeAsync(81920, rateCancel.Token); throw new Exception("Limit wait ignored cancellation"); }
    catch (OperationCanceledException) { }
}
var transferConfig = ConfigService.Active!.Config;
transferConfig.DownloadSpeedLimitKBps = 4096;
transferConfig.ReduceDownloadSpeedWhileGaming = true;
transferConfig.GamingDownloadSpeedLimitKBps = 1024;
DownloadTransferPolicy.SetGameRunning(false);
Require(DownloadTransferPolicy.EffectiveBytesPerSecond == 4096L * 1024, "Normal limit changed while no game runs");
DownloadTransferPolicy.SetGameRunning(true);
Require(DownloadTransferPolicy.EffectiveBytesPerSecond == 1024L * 1024, "Opt-in game limit not applied");
transferConfig.GamingDownloadSpeedLimitKBps = 8192;
Require(DownloadTransferPolicy.EffectiveBytesPerSecond == 4096L * 1024, "Gaming policy raised a stricter normal limit");
transferConfig.ReduceDownloadSpeedWhileGaming = false;
transferConfig.DownloadSpeedLimitKBps = 0;
Require(DownloadTransferPolicy.EffectiveBytesPerSecond == 0, "Disabled limits did not restore unlimited behavior");

var cleanupRoot = Path.Combine(Path.GetTempPath(), "xcl2-download-cleanup-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(cleanupRoot);
try
{
    var part = Path.Combine(cleanupRoot, "known.part");
    var unrelated = Path.Combine(cleanupRoot, "unrelated.part");
    var finalFile = Path.Combine(cleanupRoot, "installed.jar");
    File.WriteAllText(part, "partial"); File.WriteAllText(unrelated, "user data"); File.WriteAllText(finalFile, "complete");
    var report = new DownloadTransferObservation();
    report.Temporary(part, true); report.Temporary(finalFile, true); report.Temporary(Path.Combine(cleanupRoot, "already-cleaned.fbpart"), false);
    Require(report.DeleteResumableFiles() == 1 && !File.Exists(part), "Recorded resumable fragment was not deleted");
    Require(File.Exists(unrelated) && File.Exists(finalFile), "Cleanup deleted an unrelated or final file");
    try { DownloadTransferPolicy.EnsureDiskSpace(finalFile, long.MaxValue); throw new Exception("Clearly insufficient disk space was accepted"); }
    catch (DownloadDiskSpaceException) { }
    Require(File.ReadAllText(finalFile) == "complete", "Disk preflight modified target data");

    // Pausing automatically clears only its recorded temporary cache; canceling preserves complete files and the explanation.
    var pausedWithFiles = queue.StartNew("paused files fixture", async (_, ct) =>
    {
        File.WriteAllText(part, "resumable bytes"); DownloadTransferPolicy.TrackTemporary(part, true);
        File.WriteAllText(finalFile, "complete"); DownloadTransferPolicy.TrackCompleted();
        await Task.Delay(Timeout.Infinite, ct);
    }, "paused-files-version", folder);
    queue.Pause(pausedWithFiles);
    var pausedCompletion = pausedWithFiles.Completion;
    Require(await pausedCompletion == DownloadQueueItemStatus.Paused, "File retention fixture did not pause");
    Require(!File.Exists(part), "Paused task retained its temporary cache");
    var retainedSummary = pausedWithFiles.TransferObservation.Summary();
    var canceledNotifications = 0;
    pausedWithFiles.PropertyChanged += (_, change) =>
    { if (change.PropertyName == nameof(DownloadQueueItem.Status) && pausedWithFiles.Status == DownloadQueueItemStatus.Canceled) canceledNotifications++; };
    queue.Cancel(pausedWithFiles);
    while (pausedWithFiles.IsCleaningUp) await Task.Delay(10);
    Require(queue.Items.Contains(pausedWithFiles) && pausedWithFiles.Status == DownloadQueueItemStatus.Canceled && pausedWithFiles.CancelLabel == "移除",
        "Paused cancellation removed the explanation row or left a resumable task");
    Require(pausedWithFiles.TaskExplanation.Contains(retainedSummary) && pausedWithFiles.TaskExplanation.Contains("完整文件保留"),
        "Paused cancellation claimed no previous writes or lost its actual file summary");
    Require(!File.Exists(part) && File.ReadAllText(finalFile) == "complete" && File.Exists(unrelated), "Paused cancellation deleted complete or unrelated files");
    Require(queue.FindActiveForVersion(folder, "paused-files-version") == null && await pausedCompletion == DownloadQueueItemStatus.Paused,
        "Paused cancellation retained its version reservation or rewrote the completed attempt result");
    queue.Cancel(pausedWithFiles);
    Require(!queue.Items.Contains(pausedWithFiles) && canceledNotifications == 1, "Second remove recorded a duplicate cancellation");
}
finally { Directory.Delete(cleanupRoot, true); }
await DownloadLifecycleCases.RunAsync();
await Ux50DownloadCases.RunAsync();
Console.WriteLine("Download queue scheduling, retry, safe cleanup, limiter, Java metadata, paused checkpoint recovery and Ux50 cases passed.");
