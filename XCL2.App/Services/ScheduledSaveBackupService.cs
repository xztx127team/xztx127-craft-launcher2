using System.IO;
using System.Windows;
using XCL2.App.Models;
using XCL2.App.Services.Worlds;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>Opt-in save-only schedules. Uses known game roots; never reads a running world's files.</summary>
public sealed class ScheduledSaveBackupService : IDisposable
{
    private readonly MainWindow _owner;
    private readonly CancellationTokenSource _stop = new();
    private System.Threading.Timer? _timer;
    private bool _disposed;
    private int _running;
    public ScheduledSaveBackupService(MainWindow owner)
    {
        _owner = owner;
        _owner.ConfigService.Saved += Configure;
        Configure();
    }
    private void Configure()
    {
        if (_disposed) return;
        if (!_owner.Dispatcher.CheckAccess()) { _ = _owner.Dispatcher.BeginInvoke(new Action(Configure)); return; }
        var enabled = _owner.ConfigService.Config.SaveBackupProfiles?.Values.Any(p => p.Enabled) == true;
        if (enabled) _timer ??= new System.Threading.Timer(_ => _ = RunAsync(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(15));
        else { _timer?.Dispose(); _timer = null; }
    }
    private async Task RunAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0 || _stop.IsCancellationRequested) return;
        try
        {
            var ct = _stop.Token;
            var profiles = await _owner.Dispatcher.InvokeAsync(() =>
                (_owner.ConfigService.Config.SaveBackupProfiles ?? new()).Values.Where(p => p.Enabled).Select(p => new SaveBackupProfile
                { WorldPath = p.WorldPath, WorldName = p.WorldName, DestinationDirectory = p.DestinationDirectory, IntervalHours = p.IntervalHours, RetentionCount = p.RetentionCount }).ToArray());
            foreach (var profile in profiles)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await _owner.Dispatcher.InvokeAsync(() => WorldCatalog.RequireBackupKnown(profile.WorldPath, _owner.ServerInstanceService.Instances.Select(s => s.Directory)));
                    var running = await _owner.Dispatcher.InvokeAsync(async () => { await _owner.ProcessManager.RefreshAsync(); return IsWorldRunning(_owner, profile.WorldPath); });
                    if (await running) continue;
                    var directory = string.IsNullOrWhiteSpace(profile.DestinationDirectory) ? Path.Combine(App.DataDir, "save_backups") : profile.DestinationDirectory;
                    var store = new SaveBackupService(directory);
                    var created = await Task.Run(() =>
                    {
                        var last = store.List(profile.WorldPath).FirstOrDefault(r => r.Scheduled);
                        if (last != null && DateTime.UtcNow - last.CreatedUtc < TimeSpan.FromHours(Math.Clamp(profile.IntervalHours, 1, 720))) return false;
                        store.Create(profile.WorldPath, profile.WorldName, true, ct);
                        store.TrimScheduled(profile.WorldPath, profile.RetentionCount); return true;
                    }, ct);
                    if (created) TaskResultHistoryService.Record(TaskResultKind.Backup, TaskResultOutcome.Completed);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    TaskResultHistoryService.Record(TaskResultKind.Backup, TaskResultOutcome.Failed);
                    LauncherLogService.AppendLine("存档定时备份未完成：" + ex.Message);
                    ReminderCenterService.Publish("save-backup-failed:" + SaveBackupService.Key(profile.WorldPath), "存档自动备份未完成",
                        "请在存档页面检查备份位置和可用空间。已有备份及原存档未被覆盖。", "查看存档", () =>
                        { _owner.OpenSaveBackups(profile.WorldPath); return Task.CompletedTask; });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LauncherLogService.AppendLine("存档备份调度失败：" + ex.Message); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }
    public static bool IsWorldRunning(MainWindow owner, string worldPath)
    {
        var full = Path.GetFullPath(worldPath);
        if (owner.ServerInstanceService.Instances.Any(server =>
        {
            if (!owner.ServerProcessManager.IsRunning(server.Id)) return false;
            try
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(server.Directory));
                return full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        })) return true;
        return owner.ProcessManager.Running.Any(game =>
        {
            try
            {
                var dir = Path.GetFullPath(game.GameDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return full.StartsWith(Path.Combine(dir, "saves") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        });
    }
    public void Dispose()
    {
        _disposed = true; _owner.ConfigService.Saved -= Configure; _timer?.Dispose(); _timer = null; _stop.Cancel();
        // The token source remains alive until any already-running asynchronous copy has observed cancellation.
    }
}
