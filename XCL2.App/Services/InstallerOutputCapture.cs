using System.Diagnostics;
using System.IO;
using System.Text;

namespace XCL2.App.Services;

/// <summary>Drains installer pipes without retaining unlimited lines or allocating unlimited single lines.</summary>
internal sealed class InstallerOutputCapture
{
    internal const int MaximumLineChars = 4096;
    internal const int MaximumStoredChars = 256 * 1024;
    internal const int MaximumStoredLines = 512;
    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _characters;
    private long _nextProgress;
    internal bool Truncated { get; private set; }

    internal string[] Snapshot()
    {
        lock (_gate) return _lines.ToArray();
    }

    private void Add(string line, IProgress<string>? progress)
    {
        var report = false;
        lock (_gate)
        {
            _lines.Enqueue(line);
            _characters += line.Length;
            while (_characters > MaximumStoredChars || _lines.Count > MaximumStoredLines)
            {
                _characters -= _lines.Dequeue().Length;
                Truncated = true;
            }
            // Progress<T> posts asynchronously: reporting every line would grow the UI queue.
            if (progress != null && _clock.ElapsedMilliseconds >= _nextProgress)
            {
                _nextProgress = _clock.ElapsedMilliseconds + 200;
                report = true;
            }
        }
        if (report) progress!.Report(line);
    }

    internal async Task DrainAsync(TextReader reader, IProgress<string>? progress, CancellationToken ct)
    {
        var buffer = new char[4096];
        var line = new StringBuilder(MaximumLineChars);
        var discarded = false;
        var pendingCr = false;
        void Emit()
        {
            var text = line.ToString();
            if (discarded)
            {
                // A cut UTF-16 pair must not reach a file writer or UI text formatter.
                if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text = text[..^1];
                text += " [超长行已截断]";
                lock (_gate) Truncated = true;
            }
            Add(text, progress);
            line.Clear();
            discarded = false;
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;
            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (ch == '\n' && pendingCr) { pendingCr = false; continue; }
                pendingCr = ch == '\r';
                if (ch is '\r' or '\n') Emit();
                else if (line.Length < MaximumLineChars) line.Append(ch);
                else discarded = true;
            }
        }
        if (line.Length > 0 || discarded) Emit();
    }

    internal static async Task<InstallerOutputCapture> WaitForExitAsync(Process process, TimeSpan timeout,
        string operation, string workingDirectory, IProgress<string>? progress, CancellationToken ct)
    {
        var output = new InstallerOutputCapture();
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // A TextReader may complete ReadAsync synchronously for every buffered chunk.
        // Start both drains off the calling/UI thread before waiting for the process,
        // so a busy stdout cannot prevent stderr or the process timeout from starting.
        var stdout = process.StandardOutput;
        var stderr = process.StandardError;
        var readToken = reading.Token;
        var drains = Task.WhenAll(Task.Run(() => output.DrainAsync(stdout, progress, readToken)),
            Task.Run(() => output.DrainAsync(stderr, progress, readToken)));
        try
        {
            await InstallerProcessLifetime.WaitForExitAsync(process, timeout, operation, workingDirectory, ct)
                .ConfigureAwait(false);
            // A descendant may inherit a pipe after the parent exits; never wait on it forever.
            try { await drains.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (TimeoutException ex)
            {
                // Parent exit plus inherited open pipes is not proof the installer
                // tree stopped touching its staging directory. Do not publish success
                // and let the caller remove that directory while children may use it.
                throw new InstallerProcessCleanupDeferredException(operation + "日志等待",
                    Path.GetFullPath(workingDirectory), process.Id, parentExited: true,
                    timedOut: true, stopError: ex, terminationRequested: false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The parent has exited, but descendants can still hold these pipes
                // and the working directory. Preserve the same cleanup contract as
                // cancellation during WaitForExitAsync, including its inner marker.
                var deferred = new InstallerProcessCleanupDeferredException(operation,
                    Path.GetFullPath(workingDirectory), process.Id, parentExited: true,
                    timedOut: false, stopError: null, terminationRequested: false);
                throw new OperationCanceledException(deferred.Message, deferred, ct);
            }
            return output;
        }
        finally
        {
            reading.Cancel();
            // Process.Dispose does not own externally accessed synchronous readers.
            // Close the readers we took over even if an inherited pipe never reaches EOF.
            try { stdout.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
            try { stderr.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
            // Observe reader failures without replacing a more useful cancellation/cleanup exception.
            _ = drains.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
