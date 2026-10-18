namespace XCL2.App.Services;

public sealed partial class DownloadQueueService
{
    private DownloadRecoveryStore? _recovery;
    private Func<DownloadRecoveryRequest, bool>? _canRecover;
    private Func<DownloadRecoveryRequest, DownloadQueueItem, CancellationToken, Task>? _recover;
    public void InitializeRecovery(DownloadRecoveryStore store, Func<DownloadRecoveryRequest, bool> canRecover,
        Func<DownloadRecoveryRequest, DownloadQueueItem, CancellationToken, Task> recover)
    {
        _recovery = store; _canRecover = canRecover; _recover = recover;
    }
    private DownloadRecoveryRecord[] CaptureRecoveryRecords() => Config?.RememberInterruptedDownloads == false
        ? Array.Empty<DownloadRecoveryRecord>() : Items.Where(item => item.RecoveryRequest != null &&
            item.Status is not (DownloadQueueItemStatus.Completed or DownloadQueueItemStatus.Canceled) &&
            _canRecover?.Invoke(item.RecoveryRequest) == true)
            .Select(item => new DownloadRecoveryRecord(item.RecoveryId, item.Name, item.RecoveryRequest!)).ToArray();
    public void PersistRecovery()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        { dispatcher.BeginInvoke(new Action(PersistRecovery)); return; }
        if (_recovery == null) return;
        try { _recovery.Save(CaptureRecoveryRecords()); }
        catch (Exception ex) { LauncherLogService.AppendLine("[下载恢复] 保存任务失败：" + ex.GetType().Name); }
    }
    public async Task<int> RestoreInterruptedAsync()
    {
        if (_recovery == null || _recover == null || Config?.RememberInterruptedDownloads == false) return 0;
        var count = 0;
        await Task.Run(() => _recovery.ImportPending(records => RunOnUi(() =>
        {
            foreach (var record in records)
            {
                if (_canRecover?.Invoke(record.Request) != true || Items.Any(item => item.RecoveryId == record.Id ||
                    (item.RecoveryRequest is { } existingRequest && existingRequest.VersionId == record.Request.VersionId &&
                     string.Equals(NormalizeFolder(existingRequest.Folder), NormalizeFolder(record.Request.Folder), StringComparison.OrdinalIgnoreCase)))) continue;
                var request = record.Request;
                var entry = new DownloadQueueItem(record.Name, new CancellationTokenSource(), (item, ct) =>
                {
                    if (_canRecover?.Invoke(request) != true) throw new InvalidOperationException("实例目录已移除或恢复信息无效，未执行下载。");
                    return _recover(request, item, ct);
                })
                {
                    RecoveryId = record.Id, RecoveryRequest = request, OriginPage = "下载中心（上次任务恢复）",
                    TargetFolderPath = request.Folder, TargetVersionId = request.VersionId,
                    Status = DownloadQueueItemStatus.Paused, StatusText = "上次下载未完成，点击继续",
                    IsRunning = false, IsIndeterminate = false
                };
                Items.Add(entry); count++;
            }
            // 保存失败时异常传回 ImportPending，旧记录留在原处供下次恢复。
            _recovery.Save(CaptureRecoveryRecords());
        })));
        return count;
    }
    public void CloseRecovery()
    { PersistRecovery(); _recovery?.Dispose(); _recovery = null; }
}
