using XCL2.App.Services;

internal static class HandoffCases
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "XCL handoff regression " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var tokenPath = Path.Combine(directory, "authorization");
        var abortPath = Path.Combine(directory, "abort");
        try
        {
            var handoff = new LauncherUpdateHandoff();
            var writes = 0;
            try
            {
                handoff.Commit(() => { File.WriteAllText(tokenPath, "authorized"); writes++; });
                // The real Closed callback can shut down the dispatcher immediately,
                // canceling the still-awaiting UI operation after authorization.
                throw new TaskCanceledException("Dispatcher stopped after the real close");
            }
            catch (TaskCanceledException)
            {
                if (handoff.TryCancel())
                {
                    File.WriteAllText(abortPath, "cancel");
                    File.Delete(tokenPath);
                }
            }
            check(handoff.IsCommitted && File.ReadAllText(tokenPath) == "authorized" && !File.Exists(abortPath),
                "A shutdown exception after authorization cannot abort or erase the committed update.");
            handoff.Commit(() => throw new InvalidOperationException("Duplicate authorization must not run"));
            check(writes == 1, "A repeated commit must not write another authorization or execute its callback.");
            check(!handoff.TryCancel(), "Finally cleanup cannot cancel an already committed handoff.");

            handoff = new LauncherUpdateHandoff();
            var failedAuthorization = false;
            try { handoff.Commit(() => throw new IOException("Authorization disk failure")); }
            catch (IOException) { failedAuthorization = true; }
            check(failedAuthorization && !handoff.IsCommitted && handoff.TryCancel(),
                "Failed authorization remains uncommitted and may be canceled for cleanup.");
            check(handoff.TryCancel(), "Cancellation cleanup can be called repeatedly.");

            handoff = new LauncherUpdateHandoff();
            check(handoff.TryCancel(), "Cancellation before the actual close is accepted.");
            var forbiddenWrite = false;
            var canceledCommit = false;
            try { handoff.Commit(() => forbiddenWrite = true); }
            catch (OperationCanceledException) { canceledCommit = true; }
            check(canceledCommit && !forbiddenWrite && !handoff.IsCommitted,
                "A delayed close callback cannot authorize a canceled update.");

            handoff = new LauncherUpdateHandoff();
            var attempts = 0;
            try { handoff.Commit(() => { attempts++; throw new IOException("First write failed"); }); }
            catch (IOException) { }
            handoff.Commit(() => attempts++);
            check(attempts == 2 && handoff.IsCommitted,
                "An authorization failure can be retried before cancellation has won.");

            handoff = new LauncherUpdateHandoff();
            var reentrantCancel = true;
            var reentrantCommitRejected = false;
            handoff.Commit(() =>
            {
                reentrantCancel = handoff.TryCancel();
                try { handoff.Commit(() => throw new Exception("Nested authorization must not run")); }
                catch (InvalidOperationException) { reentrantCommitRejected = true; }
                File.WriteAllText(tokenPath, "reentrant authorization finished");
            });
            check(!reentrantCancel && reentrantCommitRejected && handoff.IsCommitted,
                "Reentrant shutdown code cannot cancel or duplicate an in-progress authorization.");

            handoff = new LauncherUpdateHandoff();
            var failedReentrantCancel = true;
            try
            {
                handoff.Commit(() =>
                {
                    failedReentrantCancel = handoff.TryCancel();
                    throw new IOException("Write failed after reentrant callback");
                });
            }
            catch (IOException) { }
            check(!failedReentrantCancel && handoff.TryCancel(),
                "A reentrant cancel cannot interrupt authorization, but a failed write can still be cleaned up.");

            await CommitBeforeCancelAsync(directory, check, failAuthorization: false);
            await CommitBeforeCancelAsync(directory, check, failAuthorization: true);

            // Exercise either legal winner under actual parallel scheduling. The
            // invariant is that a successful cancellation never follows a write.
            for (var i = 0; i < 128; i++)
            {
                var race = new LauncherUpdateHandoff();
                var writeCount = 0;
                using var start = new ManualResetEventSlim();
                var commit = Task.Run(() =>
                {
                    start.Wait();
                    try { race.Commit(() => Interlocked.Increment(ref writeCount)); }
                    catch (OperationCanceledException) { }
                });
                var cancel = Task.Run(() => { start.Wait(); return race.TryCancel(); });
                start.Set();
                await Task.WhenAll(commit, cancel).WaitAsync(TimeSpan.FromSeconds(10));
                check(cancel.Result ? writeCount == 0 && !race.IsCommitted : writeCount == 1 && race.IsCommitted,
                    "Concurrent authorization and cancellation must choose one consistent winner.");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task CommitBeforeCancelAsync(string directory, Action<bool, string> check, bool failAuthorization)
    {
        var handoff = new LauncherUpdateHandoff();
        var path = Path.Combine(directory, failAuthorization ? "failed-write" : "committed-write");
        using var authorizationEntered = new ManualResetEventSlim();
        using var releaseAuthorization = new ManualResetEventSlim();
        using var cancellationAttempted = new ManualResetEventSlim();
        var authorizer = Task.Run(() =>
        {
            try
            {
                handoff.Commit(() =>
                {
                    File.WriteAllText(path, "authorization bytes");
                    authorizationEntered.Set();
                    if (!releaseAuthorization.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test authorization was not released");
                    if (failAuthorization) throw new IOException("Synthetic flush failure");
                });
                return true;
            }
            catch (IOException) { return false; }
        });
        Task<bool>? cancellation = null;
        try
        {
            if (!authorizationEntered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test authorizer did not start");
            cancellation = Task.Run(() => { cancellationAttempted.Set(); return handoff.TryCancel(); });
            if (!cancellationAttempted.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test cancellation did not start");
            check(!cancellation.IsCompleted,
                "A competing cancel must wait while authorization bytes are written under the handoff lock.");
        }
        finally { releaseAuthorization.Set(); }
        var committed = await authorizer.WaitAsync(TimeSpan.FromSeconds(10));
        var canceled = await cancellation!.WaitAsync(TimeSpan.FromSeconds(10));
        if (canceled) File.Delete(path);
        check(failAuthorization
                ? !committed && canceled && !handoff.IsCommitted && !File.Exists(path)
                : committed && !canceled && handoff.IsCommitted && File.Exists(path),
            failAuthorization
                ? "A cancel waiting behind a failed authorization may clean up its partial bytes."
                : "A cancel waiting behind a successful authorization must preserve its bytes.");
    }
}
