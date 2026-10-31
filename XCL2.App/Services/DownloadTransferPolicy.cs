using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;

namespace XCL2.App.Services;

/// <summary>只观察当前任务实际创建的临时文件；不扫描或删除整个缓存目录。</summary>
public sealed class DownloadTransferObservation
{
    private sealed record TemporaryFile(bool Resumable, string? Target);
    private readonly ConcurrentDictionary<string, TemporaryFile> _temporaries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object OwnershipGate = new();
    private static readonly Dictionary<string, Guid> TemporaryOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Guid _owner = Guid.NewGuid();
    public event Action? Changed;
    private string _issue = "";
    private int _completed;
    private int _removedTemporaries;
    private long _receivedBytes;
    private readonly ConcurrentDictionary<Guid, (string Kind, long Deadline, string Reason)> _activities = new();
    private readonly ConcurrentDictionary<string, byte> _completedPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _targetPath;
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
    public string? TargetPath => Volatile.Read(ref _targetPath);
    public string[] CompletedPaths() => _completedPaths.Keys.ToArray();
    internal void Received(int bytes) { if (bytes > 0) Interlocked.Add(ref _receivedBytes, bytes); }
    public void Target(string path) { try { Volatile.Write(ref _targetPath, Path.GetFullPath(path)); } catch { } }
    public void ReportIssue(string reason)
    {
        Volatile.Write(ref _issue, reason ?? ""); Changed?.Invoke();
    }
    public void ClearIssue() { if (Volatile.Read(ref _issue).Length > 0) { Volatile.Write(ref _issue, ""); Changed?.Invoke(); } }
    public string CurrentReason
    {
        get
        {
            var activities = _activities.Values.ToArray();
            var retry = activities.Where(activity => activity.Kind == "重试等待").OrderBy(activity => activity.Deadline).FirstOrDefault();
            if (retry.Kind != null)
            {
                var remaining = Math.Max(0, (retry.Deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                return $"等待重试，约 {remaining:0.0} 秒后再次尝试：" + (retry.Reason.Length == 0 ? "上次连接或下载未成功。" : retry.Reason);
            }
            if (activities.Any(activity => activity.Kind == "等待文件占用"))
                return "目标文件正被其他下载任务或进程使用，正在等待释放；可暂停或取消本任务。";
            return Volatile.Read(ref _issue);
        }
    }
    internal IDisposable Phase(string kind, TimeSpan? delay = null, string? reason = null)
    {
        var id = Guid.NewGuid();
        var deadline = delay.HasValue ? Stopwatch.GetTimestamp() + (long)(delay.Value.TotalSeconds * Stopwatch.Frequency) : 0;
        _activities[id] = (kind, deadline, reason ?? "");
        var changesReason = kind is "重试等待" or "等待文件占用";
        if (changesReason) Changed?.Invoke();
        return new ObservationScope(() => { _activities.TryRemove(id, out _); if (changesReason) Changed?.Invoke(); });
    }
    private sealed class ObservationScope(Action close) : IDisposable
    { private Action? _close = close; public void Dispose() => Interlocked.Exchange(ref _close, null)?.Invoke(); }
    public string ActivitySummary(bool includePhases, bool includeRetry)
    {
        var activities = _activities.Values.ToArray();
        var parts = new List<string>();
        if (includePhases)
            foreach (var kind in new[] { "连接", "等待连接", "等待文件占用", "传输", "校验", "限速等待" })
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
    public bool HasRecordedResumableFiles => _temporaries.Any(pair => pair.Value.Resumable);
    public bool HasRecordedTemporaryFiles => !_temporaries.IsEmpty;
    public void Temporary(string path, bool resumable, string? targetPath = null)
    {
        if (_temporaries.Count >= 2000) return;
        var fullPath = Path.GetFullPath(path);
        var target = targetPath == null ? InferTemporaryTarget(fullPath) : Path.GetFullPath(targetPath);
        // A registration never authorizes deleting the final destination or an arbitrary file.
        if (target != null && (!fullPath.StartsWith(target + ".", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(fullPath), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase))) target = null;
        _temporaries[fullPath] = new TemporaryFile(resumable, target);
        lock (OwnershipGate) TemporaryOwners[fullPath] = _owner;
    }
    private static string? InferTemporaryTarget(string path)
    {
        if (path.EndsWith(".chunks.part", StringComparison.OrdinalIgnoreCase)) return path[..^12];
        if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return path[..^5];
        var fallback = path.LastIndexOf(".fbpart-", StringComparison.OrdinalIgnoreCase);
        return fallback > 0 ? path[..fallback] : null;
    }
    private bool OwnsTemporary(string path) { lock (OwnershipGate) return TemporaryOwners.TryGetValue(path, out var owner) && owner == _owner; }
    private void ReleaseTemporary(string path)
    {
        lock (OwnershipGate) if (TemporaryOwners.TryGetValue(path, out var owner) && owner == _owner) TemporaryOwners.Remove(path);
        if (_temporaries.TryRemove(path, out _)) Interlocked.Increment(ref _removedTemporaries);
    }
    public void Completed(string? path = null)
    {
        Interlocked.Increment(ref _completed);
        if (path == null) return;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (_completedPaths.Count < 100) _completedPaths.TryAdd(fullPath, 0);
            foreach (var temporary in _temporaries.Where(pair => string.Equals(pair.Value.Target, fullPath, StringComparison.OrdinalIgnoreCase) || string.Equals(pair.Key, fullPath, StringComparison.OrdinalIgnoreCase)).ToArray())
                ReleaseTemporary(temporary.Key);
        }
        catch { }
    }
    private enum TemporaryPathState { Missing, Present, Unavailable }
    private static TemporaryPathState ProbeTemporary(string path, out string reason)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            reason = (attributes & FileAttributes.Directory) != 0 ? "登记路径是目录，不能按临时文件删除" : "";
            return reason.Length == 0 ? TemporaryPathState.Present : TemporaryPathState.Unavailable;
        }
        catch (FileNotFoundException) { reason = ""; return TemporaryPathState.Missing; }
        catch (DirectoryNotFoundException) { reason = ""; return TemporaryPathState.Missing; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { reason = "无法确认临时文件状态（" + ex.Message + "）"; return TemporaryPathState.Unavailable; }
    }
    public string Summary()
    {
        var retained = 0; var removed = Volatile.Read(ref _removedTemporaries); var unavailable = 0;
        foreach (var path in _temporaries.Keys)
        {
            var state = ProbeTemporary(path, out _);
            if (state == TemporaryPathState.Missing) removed++;
            else { retained++; if (state == TemporaryPathState.Unavailable) unavailable++; }
        }
        return $"本次记录的已完成文件 {CompletedFiles} 个；已记录的临时文件：保留 {retained} 个，已清理或提交 {removed} 个。" +
            (unavailable == 0 ? "" : $"其中 {unavailable} 个路径无法确认或不能作为普通文件清理，登记仍保留供稍后重试。") +
            "完整文件不会因取消而删除。";
    }
    public string[] ResumableFiles() => _temporaries.Where(pair => pair.Value.Resumable && ProbeTemporary(pair.Key, out _) != TemporaryPathState.Missing).Select(pair => pair.Key).ToArray();
    // Compatibility entry point for the explicit old .part cleanup command and transport regression.
    public int DeleteResumableFiles() => DeleteTemporaryFilesAsync(resumableOnly: true).GetAwaiter().GetResult().Deleted;
    public Task<DownloadTemporaryCleanupResult> DeleteRecordedTemporaryFilesAsync() => DeleteTemporaryFilesAsync(resumableOnly: false);
    private async Task<DownloadTemporaryCleanupResult> DeleteTemporaryFilesAsync(bool resumableOnly)
    {
        var deleted = 0; var retained = 0;
        var reasons = new HashSet<string>();
        var files = _temporaries.ToArray();
        var cleanupWatch = Stopwatch.StartNew();
        for (var index = 0; index < files.Length; index++)
        {
            var pair = files[index];
            if (resumableOnly && !pair.Value.Resumable) continue;
            var path = pair.Key;
            var initialState = ProbeTemporary(path, out var probeReason);
            if (initialState == TemporaryPathState.Missing) { ReleaseTemporary(path); continue; }
            if (initialState == TemporaryPathState.Unavailable) { retained++; reasons.Add(probeReason); continue; }
            if (cleanupWatch.Elapsed > TimeSpan.FromSeconds(2))
            {
                retained += files.Skip(index).Count(file => (!resumableOnly || file.Value.Resumable) && ProbeTemporary(file.Key, out _) != TemporaryPathState.Missing);
                reasons.Add("本轮清理已限时结束，剩余文件保留，可稍后重试清理");
                break;
            }
            if (pair.Value.Target == null || !IsRegularPath(path))
            { retained++; reasons.Add("路径不是本任务可安全删除的临时文件"); continue; }
            if (!OwnsTemporary(path))
            { retained++; reasons.Add("该路径现由其他任务使用"); continue; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            try
            {
                using var ownership = await DownloadTargetLock.AcquireAsync(pair.Value.Target, timeout.Token).ConfigureAwait(false);
                var lockedState = ProbeTemporary(path, out probeReason);
                if (lockedState == TemporaryPathState.Missing) { ReleaseTemporary(path); continue; }
                if (lockedState == TemporaryPathState.Unavailable) { retained++; reasons.Add(probeReason); continue; }
                if (!OwnsTemporary(path)) { retained++; reasons.Add("该路径现由其他任务使用"); continue; }
                if (!IsRegularPath(path)) { retained++; reasons.Add("临时路径已改变"); continue; }
                File.Delete(path); deleted++;
                ReleaseTemporary(path);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            { retained++; reasons.Add("目标仍被其他任务或进程占用"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { retained++; reasons.Add("文件占用或权限不足（" + ex.Message + "）"); }
        }
        return new DownloadTemporaryCleanupResult(deleted, retained, string.Join("；", reasons));
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
    private static long _receivedBytes;
    /// <summary>所有下载通道累计的网络字节；智能限速用它扣除整个启动器的下载流量。</summary>
    public static long TotalReceivedBytes => Interlocked.Read(ref _receivedBytes);
    public static void ReportReceived(int bytes) { if (bytes > 0) Interlocked.Add(ref _receivedBytes, bytes); }
    public static Func<long, string>? CapacityFormatter { get; set; }
    public static string FormatCapacity(long bytes) => CapacityFormatter?.Invoke(bytes) ?? $"{bytes / 1048576.0:0.#} MB";
    public static void SetGameRunning(bool value) => Volatile.Write(ref _gameRunning, value ? 1 : 0);
    public static IDisposable Observe(DownloadTransferObservation report)
    {
        var previous = Observation.Value; Observation.Value = report;
        return new Scope(() => Observation.Value = previous);
    }
    private sealed class Scope(Action close) : IDisposable { public void Dispose() => close(); }
    public static void TrackTemporary(string path, bool resumable, string? targetPath = null) => Observation.Value?.Temporary(path, resumable, targetPath);
    public static void ReportIssue(string reason) => Observation.Value?.ReportIssue(reason);
    public static void TrackCompleted(string? path = null) => Observation.Value?.Completed(path);
    public static void TrackTarget(string path) => Observation.Value?.Target(path);
    public static IDisposable BeginPhase(string kind) => Observation.Value?.Phase(kind) ?? new Scope(() => { });
    public static async Task WaitRetryAsync(TimeSpan duration, CancellationToken ct, string? reason = null)
    {
        using var phase = Observation.Value?.Phase("重试等待", duration, reason);
        await Task.Delay(duration, ct);
    }
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
        ReportReceived(bytes);
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
/// <summary>安装程序或授权对话框明确返回的用户取消，与网络超时的 OCE 区分。</summary>
public sealed class DownloadUserCanceledException(string message, Exception? innerException = null)
    : OperationCanceledException(message, innerException) { }
public sealed record DownloadTemporaryCleanupResult(int Deleted, int Retained, string Reason);
public sealed class DownloadDiskSpaceException(string message) : IOException(message) { }

/// <summary>按文件体积、用户上限、共享限速、同期开启的大文件和最近真实传输选择连接数。</summary>
internal static class AdaptiveDownloadConcurrency
{
    internal static readonly SemaphoreSlim RangeConnections = new(8, 8);
    private sealed record Sample(double BytesPerSecondPerConnection, int Failures, DateTime UpdatedUtc);
    private static readonly ConcurrentDictionary<string, Sample> Samples = new(StringComparer.OrdinalIgnoreCase);
    private static int _activeTransfers;
    internal static IDisposable EnterTransfer()
    {
        Interlocked.Increment(ref _activeTransfers);
        return new Lease();
    }
    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _activeTransfers); }
    }
    internal static int Select(string url, long length, int maximum, bool supportsRange, long byteLimit)
    {
        Samples.TryGetValue(Host(url), out var sample);
        if (sample != null && DateTime.UtcNow - sample.UpdatedUtc > TimeSpan.FromMinutes(5)) sample = null;
        return Decide(length, maximum, supportsRange, byteLimit, Math.Max(1, Volatile.Read(ref _activeTransfers)),
            sample?.BytesPerSecondPerConnection ?? 0, sample?.Failures ?? 0);
    }
    internal static int Decide(long length, int maximum, bool supportsRange, long byteLimit, int activeTransfers,
        double measuredPerConnection = 0, int recentFailures = 0)
    {
        const long mb = 1024 * 1024;
        if (!supportsRange || maximum <= 1 || length < 8 * mb || byteLimit is > 0 and <= mb) return 1;
        var desired = length < 32 * mb ? 2 : length < 128 * mb ? 4 : 8;
        var shared = Math.Max(1, 8 / Math.Max(1, activeTransfers));
        desired = Math.Min(desired, Math.Min(Math.Clamp(maximum, 1, 8), shared));
        if (byteLimit > 0) desired = Math.Min(desired, (int)Math.Clamp(byteLimit / mb, 1, 8));
        // 已有单流能在约两秒内完成、或带宽已受限时，额外建连没有收益。
        if (measuredPerConnection > 0 && length / measuredPerConnection <= 2) return 1;
        if (measuredPerConnection is > 0 and < 256 * 1024) desired = Math.Min(desired, 2);
        if (recentFailures > 0) desired = Math.Max(1, desired / (recentFailures >= 2 ? 4 : 2));
        return Math.Max(1, desired);
    }
    internal static void Record(string url, long bytes, TimeSpan elapsed, int connections)
    {
        if (bytes < 1024 * 1024 || elapsed.TotalSeconds < .1) return;
        var speed = bytes / elapsed.TotalSeconds / Math.Max(1, connections);
        var host = Host(url); if (host.Length == 0) return;
        if (Samples.Count > 256) Samples.Clear();
        Samples.AddOrUpdate(host, new Sample(speed, 0, DateTime.UtcNow), (_, old) =>
            new Sample(old.BytesPerSecondPerConnection > 0 ? old.BytesPerSecondPerConnection * .4 + speed * .6 : speed, 0, DateTime.UtcNow));
    }
    internal static void Failed(string url)
    {
        var host = Host(url); if (host.Length == 0) return;
        if (Samples.Count > 256) Samples.Clear();
        Samples.AddOrUpdate(host, new Sample(0, 1, DateTime.UtcNow), (_, old) =>
            new Sample(old.BytesPerSecondPerConnection, Math.Min(3, old.Failures + 1), DateTime.UtcNow));
    }
    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
}
