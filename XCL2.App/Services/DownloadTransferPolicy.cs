using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;

namespace XCL2.App.Services;

/// <summary>只观察当前任务实际创建的临时文件；不扫描或删除整个缓存目录。</summary>
public sealed class DownloadTransferObservation
{
    private readonly ConcurrentDictionary<string, bool> _temporaries = new(StringComparer.OrdinalIgnoreCase);
    private int _completed;
    private long _receivedBytes;
    private readonly ConcurrentDictionary<Guid, (string Kind, long Deadline)> _activities = new();
    private readonly ConcurrentDictionary<string, byte> _completedPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _targetPath;
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
    public string? TargetPath => Volatile.Read(ref _targetPath);
    public string[] CompletedPaths() => _completedPaths.Keys.ToArray();
    internal void Received(int bytes) { if (bytes > 0) Interlocked.Add(ref _receivedBytes, bytes); }
    public void Target(string path) { try { Volatile.Write(ref _targetPath, Path.GetFullPath(path)); } catch { } }
    internal IDisposable Phase(string kind, TimeSpan? delay = null)
    {
        var id = Guid.NewGuid();
        var deadline = delay.HasValue ? Stopwatch.GetTimestamp() + (long)(delay.Value.TotalSeconds * Stopwatch.Frequency) : 0;
        _activities[id] = (kind, deadline);
        return new ObservationScope(() => _activities.TryRemove(id, out _));
    }
    private sealed class ObservationScope(Action close) : IDisposable
    { private Action? _close = close; public void Dispose() => Interlocked.Exchange(ref _close, null)?.Invoke(); }
    public string ActivitySummary(bool includePhases, bool includeRetry)
    {
        var activities = _activities.Values.ToArray();
        var parts = new List<string>();
        if (includePhases)
            foreach (var kind in new[] { "连接", "等待连接", "传输", "校验", "限速等待" })
            { var count = activities.Count(activity => activity.Kind == kind); if (count > 0) parts.Add($"{kind} {count}"); }
        if (includeRetry)
        {
            var retry = activities.Where(activity => activity.Kind == "重试等待").ToArray();
            if (retry.Length > 0)
            {
                var remaining = Math.Max(0, (retry.Min(activity => activity.Deadline) - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                parts.Add($"{retry.Length} 项等待重试，最近一次约 {remaining:0.0} 秒后");
            }
        }
        return string.Join(" · ", parts);
    }
    public int CompletedFiles => Volatile.Read(ref _completed);
    public bool HasRecordedResumableFiles => _temporaries.Any(pair => pair.Value);
    public void Temporary(string path, bool resumable)
    {
        if (_temporaries.Count < 2000) _temporaries.TryAdd(Path.GetFullPath(path), resumable);
    }
    public void Completed(string? path = null)
    {
        Interlocked.Increment(ref _completed);
        if (path == null || _completedPaths.Count >= 100) return;
        try { _completedPaths.TryAdd(Path.GetFullPath(path), 0); } catch { }
    }
    public string Summary()
    {
        var retained = 0; var removed = 0;
        foreach (var path in _temporaries.Keys)
        {
            if (File.Exists(path)) retained++; else removed++;
        }
        return $"本次记录的已完成文件 {CompletedFiles} 个；已记录的临时文件：保留 {retained} 个，已清理或提交 {removed} 个。完整文件不会因取消而删除。";
    }
    public string[] ResumableFiles() => _temporaries.Where(pair => pair.Value && File.Exists(pair.Key)).Select(pair => pair.Key).ToArray();
    public int DeleteResumableFiles()
    {
        var deleted = 0;
        foreach (var path in ResumableFiles())
        {
            if (!path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || !IsRegularPath(path)) continue;
            // A canceled task may share this destination with a different process/task.
            // Never delete its fragment without the same ownership used for transfer and commit.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try
            {
                using var ownership = DownloadTargetLock.AcquireAsync(path[..^5], timeout.Token).GetAwaiter().GetResult();
                if (!File.Exists(path) || !IsRegularPath(path)) continue;
                File.Delete(path); deleted++;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { /* Busy targets remain resumable. */ }
        }
        return deleted;
    }
    private static bool IsRegularPath(string path)
    {
        try
        {
            for (var current = new FileInfo(path) as FileSystemInfo; current != null;
                 current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

}

/// <summary>原版下载与统一镜像下载共用总限速；只在真实网络读写时等待。</summary>
public static class DownloadTransferPolicy
{
    private static readonly DownloadRateLimiter SharedLimiter = new() { DynamicLimit = () => EffectiveBytesPerSecond };
    private static readonly AsyncLocal<DownloadTransferObservation?> Observation = new();
    private static int _gameRunning;
    public static Func<long, string>? CapacityFormatter { get; set; }
    public static string FormatCapacity(long bytes) => CapacityFormatter?.Invoke(bytes) ?? $"{bytes / 1048576.0:0.#} MB";
    public static void SetGameRunning(bool value) => Volatile.Write(ref _gameRunning, value ? 1 : 0);
    public static IDisposable Observe(DownloadTransferObservation report)
    {
        var previous = Observation.Value; Observation.Value = report;
        return new Scope(() => Observation.Value = previous);
    }
    private sealed class Scope(Action close) : IDisposable { public void Dispose() => close(); }
    public static void TrackTemporary(string path, bool resumable) => Observation.Value?.Temporary(path, resumable);
    public static void TrackCompleted(string? path = null) => Observation.Value?.Completed(path);
    public static void TrackTarget(string path) => Observation.Value?.Target(path);
    public static IDisposable BeginPhase(string kind) => Observation.Value?.Phase(kind) ?? new Scope(() => { });
    public static async Task WaitRetryAsync(TimeSpan duration, CancellationToken ct)
    { using var phase = Observation.Value?.Phase("重试等待", duration); await Task.Delay(duration, ct); }
    public static long EffectiveBytesPerSecond
    {
        get
        {
            var cfg = ConfigService.Active?.Config;
            var normal = Math.Max(0L, cfg?.DownloadSpeedLimitKBps ?? 0) * 1024L;
            if (cfg?.ReduceDownloadSpeedWhileGaming == true && Volatile.Read(ref _gameRunning) != 0)
            {
                var gaming = Math.Clamp((long)cfg.GamingDownloadSpeedLimitKBps, 1, 1048576) * 1024;
                return normal > 0 ? Math.Min(normal, gaming) : gaming;
            }
            return normal;
        }
    }
    public static async Task ConsumeAsync(int bytes, CancellationToken ct)
    {
        Observation.Value?.Received(bytes);
        SharedLimiter.BytesPerSecond = EffectiveBytesPerSecond;
        using var phase = EffectiveBytesPerSecond > 0 ? BeginPhase("限速等待") : null;
        await SharedLimiter.ConsumeAsync(bytes, ct);
    }
    public static void EnsureDiskSpace(string targetPath, long additionalBytes)
    {
        if (additionalBytes <= 0) return; // 未知体积不伪造完整安装所需空间。
        long available; string? root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(targetPath));
            if (string.IsNullOrWhiteSpace(root)) return;
            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return; }
        const long margin = 16 * 1024 * 1024;
        if (additionalBytes > Math.Max(0, available - margin))
            throw new DownloadDiskSpaceException($"目标磁盘 {root} 空间不足：本文件还需约 {FormatCapacity(additionalBytes)}，可用 {FormatCapacity(available)}（另预留 16 MB）。请释放空间或选择其他目录后重试。");
    }
}
public sealed class DownloadDiskSpaceException(string message) : IOException(message) { }
