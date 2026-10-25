using System.Diagnostics;
using System.IO;

namespace XCL2.App.Services;

/// <summary>
/// An interrupted installer may have descendants that still hold files. Its working directory
/// must be retained even after the parent exits; a process handle is not a process-tree lease.
/// </summary>
internal sealed class InstallerProcessCleanupDeferredException : IOException
{
    internal string WorkingDirectory { get; }
    internal int ProcessId { get; }
    internal bool ParentExited { get; }

    internal InstallerProcessCleanupDeferredException(string operation, string workingDirectory,
        int processId, bool parentExited, bool timedOut, Exception? stopError, bool terminationRequested = true)
        : base($"{operation}{(timedOut ? "运行超时" : "已取消")}。"
            + (parentExited ? (terminationRequested
                ? "已请求终止进程树并等待父进程退出，但无法确认所有后代进程均已退出。"
                : "安装器父进程已经退出，但输出管道尚未关闭，无法确认所有后代进程均已退出。")
                : "未能确认安装器已经退出，请在任务管理器中检查安装器进程。")
            + $"\n安装器父进程 PID（供排查）：{processId}\n已保留安装目标目录：{workingDirectory}"
            + "\n确认安装器及其子进程全部退出后再检查残留文件；不要删除整个游戏目录或原有数据。", stopError)
    {
        WorkingDirectory = workingDirectory;
        ProcessId = processId;
        ParentExited = parentExited;
    }
}

internal static class InstallerProcessLifetime
{
    /// <summary>
    /// Cancellation and timeout both terminate the installer, then wait with an independent bound.
    /// Kill(true) + WaitForExit proves only that the parent exited (per the Process API contract),
    /// so interrupted working directories remain quarantined instead of being recursively deleted.
    /// </summary>
    internal static async Task WaitForExitAsync(Process process, TimeSpan timeout, string operation,
        string workingDirectory, CancellationToken ct, TimeSpan? stopTimeout = null)
    {
        using var running = CancellationTokenSource.CreateLinkedTokenSource(ct);
        running.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(running.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested || ct.IsCancellationRequested)
        {
            var pid = process.Id;
            Exception? stopError = null;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception
                or InvalidOperationException or NotSupportedException or AggregateException)
            {
                stopError = error;
                // Some legacy runtimes cannot enumerate/terminate a process tree. Stop the parent
                // when possible, but never treat that fallback as proof its children are gone.
                try { if (!process.HasExited) process.Kill(); }
                catch (Exception fallback) when (fallback is System.ComponentModel.Win32Exception
                    or InvalidOperationException or NotSupportedException)
                { stopError = new AggregateException(error, fallback); }
            }

            var parentExited = false;
            using var stopping = new CancellationTokenSource(stopTimeout ?? TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(stopping.Token).ConfigureAwait(false);
                parentExited = process.HasExited;
            }
            catch (Exception error) when (error is OperationCanceledException
                or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                stopError = stopError == null ? error : new AggregateException(stopError, error);
                try { parentExited = process.HasExited; } catch { }
            }

            var deferred = new InstallerProcessCleanupDeferredException(operation, Path.GetFullPath(workingDirectory),
                pid, parentExited, timedOut: !ct.IsCancellationRequested, stopError: stopError);
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(deferred.Message, deferred, ct);
            throw deferred;
        }
    }

    internal static bool RequiresPreservedDirectory(Exception error)
    {
        if (error is InstallerProcessCleanupDeferredException) return true;
        if (error is AggregateException aggregate && aggregate.InnerExceptions.Any(RequiresPreservedDirectory)) return true;
        return error.InnerException != null && RequiresPreservedDirectory(error.InnerException);
    }
}
