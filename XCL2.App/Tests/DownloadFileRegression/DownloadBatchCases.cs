using System.IO.Compression;
using System.Reflection;
using XCL2.App.Services;

internal static class DownloadBatchCases
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static TaskCompletionSource<bool> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task RunAsync(string root)
    {
        await FirstFailureStopsAndDrainsSiblingsAsync();
        await NativeCancellationDoesNotPublishPartialFilesAsync(root);
    }

    private static async Task FirstFailureStopsAndDrainsSiblingsAsync()
    {
        var siblingStarted = Signal();
        var siblingStopping = Signal();
        var allowSiblingExit = Signal();
        var siblingExited = false;
        var firstFailure = new IOException("first download source exhausted");
        async Task Action(int entry, CancellationToken ct)
        {
            if (entry == 0)
            {
                await siblingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                throw firstFailure;
            }
            siblingStarted.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally
            {
                siblingStopping.TrySetResult(true);
                await allowSiblingExit.Task.WaitAsync(TimeSpan.FromSeconds(5));
                siblingExited = true;
            }
        }
        var method = typeof(DownloadService).GetMethod("RunDownloadBatchAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(int));
        var batch = (Task)method.Invoke(null, new object[]
        {
            new[] { 0, 1 }, (Func<int, CancellationToken, Task>)Action, "batch fixture", CancellationToken.None
        })!;
        try
        {
            await siblingStopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!batch.IsCompleted && !siblingExited,
                "A first failure reported the batch stopped while a sibling writer was still closing.");
        }
        finally { allowSiblingExit.TrySetResult(true); }
        try
        {
            await batch.WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("Failed batch returned success.");
        }
        catch (IOException error)
        {
            Require(ReferenceEquals(error, firstFailure),
                "Sibling cancellation replaced the original download failure.");
        }
        Require(siblingExited, "Batch returned before the canceled sibling really exited.");
    }

    private static async Task NativeCancellationDoesNotPublishPartialFilesAsync(string root)
    {
        var destination = Path.Combine(root, "cancel-native-extraction");
        Directory.CreateDirectory(destination);
        var existing = Path.Combine(destination, "existing.dll");
        await File.WriteAllTextAsync(existing, "previous complete native");
        var package = Path.Combine(root, "large-native-cancellation.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        using (var output = archive.CreateEntry("large.dll", CompressionLevel.NoCompression).Open())
        {
            var buffer = new byte[81920];
            for (var i = 0; i < 128; i++) output.Write(buffer);
        }
        var method = typeof(DownloadService).GetMethod("ExtractNativesAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        Task Extract(IProgress<ProgressInfo>? progress, CancellationToken ct) =>
            (Task)method.Invoke(null, new object?[] { package, destination, progress, ct })!;

        using (var preCanceled = new CancellationTokenSource())
        {
            preCanceled.Cancel();
            await RequireCanceled(Extract(null, preCanceled.Token));
        }
        Require(!File.Exists(Path.Combine(destination, "large.dll")),
            "An extraction canceled before starting published a native DLL.");

        using (var cancelDuringCopy = new CancellationTokenSource())
        {
            var canceledAfterWriting = false;
            var progress = new InlineProgress(info =>
            {
                // This report occurs after the first buffer was written to the private
                // temporary file, while the final DLL must still be absent.
                if (info.CurrentFile != "large.dll" || info.Done != 0) return;
                Require(Directory.EnumerateFiles(destination, "large.dll.xcl-tmp-*")
                    .Any(path => new FileInfo(path).Length > 0), "Native cancellation fixture did not write a real partial buffer.");
                canceledAfterWriting = true;
                cancelDuringCopy.Cancel();
            });
            await RequireCanceled(Extract(progress, cancelDuringCopy.Token));
            Require(canceledAfterWriting, "Large native stream was not canceled during its copy.");
        }
        Require(!File.Exists(Path.Combine(destination, "large.dll")) &&
            !Directory.EnumerateFiles(destination, "*.xcl-tmp-*").Any(),
            "Canceled native extraction left a partial DLL or temporary fragment.");
        Require(await File.ReadAllTextAsync(existing) == "previous complete native",
            "Native cancellation modified an unrelated completed DLL.");
    }

    private static async Task RequireCanceled(Task action)
    {
        try { await action.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Native extraction ignored cancellation or reported success.");
    }

    private sealed class InlineProgress(Action<ProgressInfo> report) : IProgress<ProgressInfo>
    { public void Report(ProgressInfo value) => report(value); }
}
