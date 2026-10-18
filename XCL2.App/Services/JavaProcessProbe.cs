using System.Diagnostics;
using System.IO;
using System.Text;

namespace XCL2.App.Services;

/// <summary>Bounded Java probe. Drain both pipes concurrently and stop a hung executable.</summary>
internal static class JavaProcessProbe
{
    internal static async Task<string?> ReadVersionOutputAsync(string javaExecutable,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(javaExecutable)
        {
            UseShellExecute = false, RedirectStandardError = true,
            RedirectStandardOutput = true, CreateNoWindow = true
        };
        start.ArgumentList.Add("-version");
        using var process = Process.Start(start);
        if (process == null) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var stdout = DrainAsync(process.StandardOutput, deadline.Token);
        var stderr = DrainAsync(process.StandardError, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            // A child process can retain an inherited pipe even after Java exits.
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
            var errors = await stderr.ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            return errors + "\n" + output;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            // Stream readers observe the same token. Never wait indefinitely while cleaning up.
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException) { }
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                // Keep draining after the bound so a verbose shim cannot block on its output pipe.
                var keep = Math.Min(count, Math.Max(0, 64 * 1024 - text.Length));
                if (keep > 0) text.Append(buffer, 0, keep);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        return text.ToString();
    }
}
