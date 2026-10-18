using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>所有 Java 入口使用统一下载列表；已有下载流水线时复用当前条目。</summary>
public static class JavaDownloadQueueService
{
    public static Task<string> DownloadRecommendedAsync(JavaService service,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
        DownloadAsync(service, new JavaDownloadRequest(21,
            Environment.Is64BitOperatingSystem ? "x64" : "x86", JavaInstallMode.Portable), progress, ct);

    public static Task<string> DownloadAsync(JavaService service, JavaDownloadRequest request,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var queue = DownloadQueueService.Instance;
        return queue.CurrentItem is { } current
            ? DownloadIntoItemAsync(current, current.Cts.Token)
            : queue.RunAndWaitAsync($"下载 Java {request.MajorVersion} ({request.Arch})",
                DownloadIntoItemAsync, ct);

        async Task<string> DownloadIntoItemAsync(DownloadQueueItem item, CancellationToken token)
        {
            // 下载阶段可取消；MSI 真正运行时由 JavaService 临时关闭取消并等待真实退出。
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
            var queueProgress = item.CreateProgress();
            var combined = new Progress<ProgressInfo>(info =>
            {
                queueProgress.Report(info);
                progress?.Report(info);
            });
            return await service.DownloadJavaAsync(request, combined, linked.Token);
        }
    }
}
