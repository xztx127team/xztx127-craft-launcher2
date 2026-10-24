using System.Windows;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    /// <summary>运行中的日志问题只登记为警告。合并同一进程的提醒，不抢占游戏窗口。</summary>
    private void StartLiveGameDiagnostics(GameProcessInfo process)
    {
        var gate = new object();
        var pending = false;
        var nextNotice = DateTime.MinValue;
        var stopped = false;
        var latestObserved = "";
        var pollCancellation = new CancellationTokenSource();
        Action<string>? observe = null;
        EventHandler? exited = null;
        EventHandler? closed = null;
        void Stop()
        {
            lock (gate)
            {
                if (stopped) return;
                stopped = true;
                pollCancellation.Cancel();
            }
            if (observe != null) process.OutputReceived -= observe;
            if (exited != null) process.Process.Exited -= exited;
            if (closed != null) Closed -= closed;
        }
        observe = line =>
        {
            if (!GameLogDiagnostics.IsIssueLine(line)) return;
            lock (gate)
            {
                if (stopped) return;
                latestObserved = line;
                if (pending || DateTime.UtcNow < nextNotice) return;
                pending = true;
            }
            _ = PublishLaterAsync();
        };
        async Task PublishLaterAsync()
        {
            try
            {
                await Task.Delay(1800);
                lock (gate) if (stopped) return;
                if (process.HasExited || _isWindowClosed || Dispatcher.HasShutdownStarted) return;
                string observed;
                lock (gate) observed = latestObserved;
                var issues = GameLogDiagnostics.Scan(process.GetOutputSnapshot() + "\n" + observed, 40);
                if (issues.Count == 0) return;
                var description = DiagnosticRedactionService.Sanitize(
                    $"游戏仍在运行；发现 {issues.Count} 类 WARN / ERROR 日志，暂作为警告。\n" +
                    GameLogDiagnostics.Format(issues.Take(1).ToArray(), running: true), 1600);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (process.HasExited || _isWindowClosed) return;
                    ReminderCenterService.Publish($"game-log-{process.Pid}-{process.StartedAt.Ticks}",
                        $"{process.VersionId} · 日志警告", description, "查看全部与修复建议",
                        () => { CrashReportDialog.ShowLogWarnings(this, process); return Task.CompletedTask; });
                });
            }
            catch (Exception ex) { LauncherLogService.AppendLine("[游戏日志监视] " + ex.GetType().Name); }
            finally { lock (gate) { pending = false; nextNotice = DateTime.UtcNow.AddSeconds(30); } }
        }
        exited = (_, _) => Stop();
        closed = (_, _) => Stop();
        process.OutputReceived += observe;
        process.Process.Exited += exited;
        Closed += closed;
        // 订阅前的启动输出也要观察，避免极快产生的告警被遗漏。
        foreach (var line in process.GetOutputSnapshot().Split('\n')) observe(line);
        if (process.HasExited) Stop();
        _ = PollDiskLogAsync();
        async Task PollDiskLogAsync()
        {
            try
            {
                var path = System.IO.Path.Combine(process.GameDir, "logs", "latest.log");
                var lastWrite = DateTime.MinValue;
                while (!pollCancellation.IsCancellationRequested && !process.HasExited)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), pollCancellation.Token);
                    if (!System.IO.File.Exists(path)) continue;
                    var modified = System.IO.File.GetLastWriteTime(path);
                    if (modified < process.StartedAt.AddSeconds(-3) || modified == lastWrite) continue;
                    lastWrite = modified;
                    foreach (var line in DiagnosticRedactionService.ReadTextTail(path, 64 * 1024).Split('\n')) observe(line);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LauncherLogService.AppendLine("[游戏日志监视] 磁盘日志：" + ex.GetType().Name); }
            finally { Stop(); pollCancellation.Dispose(); }
        }
    }

    private void ShowExitDiagnostic(GameProcessInfo process, string title, string description, Action show)
    {
        ReminderCenterService.Remove($"game-log-{process.Pid}-{process.StartedAt.Ticks}");
        PublishGameDiagnostic(process, title, description, show);
        // 报错只在进程已经退出后弹出，使用启动器自身覆盖层，绝不把弹窗附着到游戏原生窗口。
        show();
    }
}
