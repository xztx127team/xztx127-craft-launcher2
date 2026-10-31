using XCL2.App.Services;

internal static class Ux50DownloadCases
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task RunAsync()
    {
        var queue = DownloadQueueService.Instance;
        queue.Config ??= new(); queue.Config.DownloadTaskConcurrency = 1;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var safe = queue.StartNew("safe fixture", async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = queue.StartNew("waiting fixture", (_, _) => Task.CompletedTask);
        Require(waiting.CanMoveWaiting, "Fixture must actually wait");
        Require(queue.CancelSelectedWaiting(new[] { safe, waiting, waiting }) == 1, "Batch cancel must only cancel a distinct still-waiting item");
        Require(safe.IsRunning && await waiting.Completion == DownloadQueueItemStatus.Canceled, "Waiting cancellation interrupted running task");
        var safeWaiting = queue.StartNew("pause waiting fixture", (_, _) => throw new Exception("Paused queue entry executed unexpectedly"));
        Require(queue.PauseAllSafe() == 2, "Safe pause must select running and waiting safe items");
        Require(await safe.Completion == DownloadQueueItemStatus.Paused && await safeWaiting.Completion == DownloadQueueItemStatus.Paused && !safeWaiting.IsRunning,
            "Pause-all started a waiting task or lost paused outcome");
        queue.Cancel(safe); queue.Cancel(safe); queue.Cancel(safeWaiting); queue.Cancel(safeWaiting);
        queue.Config.DownloadTaskConcurrency = 0;
        var guardedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guardedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guarded = queue.StartNew("installer fixture", async (_, _) => { guardedStarted.TrySetResult(); await guardedRelease.Task.WaitAsync(TimeSpan.FromSeconds(10)); }, supportsPause: false, supportsCancel: false);
        await guardedStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(queue.PauseAllSafe() == 0 && guarded.IsRunning, "Pause-all tried to interrupt unsafe installer");
        guardedRelease.SetResult(); await guarded.Completion;
        for (var wait = 0; !queue.RecentCompleted.Contains(guarded) && wait < 100; wait++) await Task.Delay(10);
        Require(queue.RecentCompleted.Contains(guarded), "Completed task missing in explicit recent-file menu");

        var config = queue.Config;
        config.Ux50DownloadQuietHours = false;
        Require(!DownloadNotificationPolicy.IsQuiet(config, new DateTime(2026, 1, 1, 23, 0, 0)), "Quiet hours must be opt-in");
        config.Ux50DownloadQuietHours = true; config.Ux50DownloadQuietStart = "22:00"; config.Ux50DownloadQuietEnd = "08:00";
        Require(DownloadNotificationPolicy.IsQuiet(config, new DateTime(2026, 1, 1, 22, 0, 0)) &&
            DownloadNotificationPolicy.IsQuiet(config, new DateTime(2026, 1, 1, 7, 59, 0)) &&
            !DownloadNotificationPolicy.IsQuiet(config, new DateTime(2026, 1, 1, 8, 0, 0)), "Overnight quiet boundary incorrect");
        config.Ux50DownloadQuietStart = "bad";
        Require(!DownloadNotificationPolicy.IsQuiet(config, DateTime.Now), "Invalid setting must not silently suppress all notifications");
        config.Ux50DownloadQuietStart = "08:00"; config.Ux50DownloadQuietEnd = "08:00";
        Require(DownloadNotificationPolicy.IsQuiet(config, DateTime.Now), "Equal endpoints must mean documented full-day quiet");
        config.Ux50DownloadQuietHours = false;

        var observation = new DownloadTransferObservation();
        using (DownloadTransferPolicy.Observe(observation))
        {
            using (DownloadTransferPolicy.BeginPhase("连接")) Require(observation.ActivitySummary(true, false).Contains("连接 1"), "Real connecting activity not reported");
            Require(observation.ActivitySummary(true, false) == "", "Disposed activity remained visible");
            var delay = DownloadTransferPolicy.WaitRetryAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
            Require(observation.ActivitySummary(false, true).Contains("等待重试"), "Actual retry wait not observable");
            await delay;
            Require(!observation.ActivitySummary(false, true).Contains("等待重试"), "Countdown remained after actual wait");
            await DownloadTransferPolicy.ConsumeAsync(1024, CancellationToken.None);
            Require(observation.ReceivedBytes == 1024, "Speed observation must use actual transfer bytes");
            DownloadTransferPolicy.TrackCompleted(Path.Combine(Path.GetTempPath(), "exact-completed.bin"));
            Require(observation.CompletedPaths().Single().EndsWith("exact-completed.bin"), "Completed file identity lost");
        }
    }
}
