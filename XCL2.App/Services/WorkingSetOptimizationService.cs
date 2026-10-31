using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XCL2.App.Services;

public static class WorkingSetOptimizationService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    public sealed record Result(int Completed, int Skipped, long AvailableChangeMb)
    {
        public string Summary => $"已整理 {Completed} 个进程，跳过 {Skipped} 个进程。可用内存变化 {AvailableChangeMb:+0;-0;0} MB。";
    }

    public static Task<Result> RunAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => Run(cancellationToken), cancellationToken);

    public static Result Run(CancellationToken cancellationToken = default)
    {
        Gate.Wait(cancellationToken);
        try
        {
            var before = MemoryOptimizerService.ReadPhysicalMemory()?.AvailableMb;
            using var self = Process.GetCurrentProcess();
            int completed = 0, skipped = 0;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        // 只处理当前桌面会话；受保护的进程由系统拒绝，不申请提权。
                        if (process.SessionId != self.SessionId || process.HasExited) { skipped++; continue; }
                        if (EmptyWorkingSet(process.Handle)) completed++; else skipped++;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                    { skipped++; }
                }
            }
            var after = MemoryOptimizerService.ReadPhysicalMemory()?.AvailableMb;
            return new(completed, skipped, before.HasValue && after.HasValue ? (long)after.Value - (long)before.Value : 0);
        }
        finally { Gate.Release(); }
    }
}
