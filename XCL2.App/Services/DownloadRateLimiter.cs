namespace XCL2.App.Services;

/// <summary>
/// 全局下载限速器（令牌桶算法），供多线程下载时的所有并发连接共享同一个实例，
/// 保证"设置里填的 KB/s 是所有连接加总的速度"，而不是每个连接各自能跑到这个速度
/// （并发数一多，后者会让实际总速度远超用户预期的上限）。
///
/// 用法：每次从网络流读到一批字节后，调用 <see cref="ConsumeAsync"/>，
/// 传入本次读到的字节数；如果当前令牌不够，会异步等待到令牌补充够为止再返回，
/// 从而把整体吞吐量压到目标速率。
///
/// 令牌桶而不是简单的"每秒读多少就 sleep 多久"：令牌桶允许短时突发（桶里囤的令牌可以
/// 一次性用掉），对小文件下载更友好——不会让"限速"变成"每个文件都要额外等待固定延迟"。
/// </summary>
public sealed class DownloadRateLimiter
{
    private readonly object _lock = new();
    private long _bucketBytes;
    private long _capacityBytes;
    private long _lastRefill = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>当前限速目标，单位字节/秒。0 或负数表示不限速（<see cref="ConsumeAsync"/> 直接放行）。
    /// 可以在下载过程中动态调整（智能限速会周期性地改这个值），下一次 ConsumeAsync 调用即生效。</summary>
    private long _bytesPerSecond;
    public long BytesPerSecond { get => Interlocked.Read(ref _bytesPerSecond); set => Interlocked.Exchange(ref _bytesPerSecond, value); }
    public Func<long>? DynamicLimit { get; set; }

    public DownloadRateLimiter(long bytesPerSecond = 0)
    {
        BytesPerSecond = bytesPerSecond;
        _capacityBytes = Math.Max(bytesPerSecond, 64 * 1024); // 桶容量至少 64KB，避免限速值很小时桶太小导致频繁小额等待
        _bucketBytes = _capacityBytes;
    }

    /// <summary>消耗指定字节数对应的令牌；令牌不够时异步等待。不限速时立即返回。</summary>
    public async Task ConsumeAsync(int bytes, CancellationToken ct = default)
    {
        if (bytes <= 0) return;

        var remaining = bytes;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan waitFor;
            lock (_lock)
            {
                var target = DynamicLimit?.Invoke() ?? BytesPerSecond;
                if (target <= 0) { _bucketBytes = _capacityBytes; return; }
                _capacityBytes = Math.Max(target, 64 * 1024);
                _bucketBytes = Math.Min(_bucketBytes, _capacityBytes);
                RefillLocked(target);
                // 一个网络 buffer 可能比桶容量大，分批消耗，避免永远凑不齐令牌。
                var granted = (int)Math.Min(remaining, _bucketBytes);
                _bucketBytes -= granted;
                remaining -= granted;
                if (remaining <= 0) return;
                waitFor = TimeSpan.FromSeconds(Math.Clamp((double)Math.Min(remaining, _capacityBytes) / target, 0.005, 1));
            }
            await Task.Delay(waitFor, ct);
        }
    }

    private void RefillLocked(long target)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_lastRefill, now).TotalSeconds;
        if (elapsed <= 0) return;
        var refill = (long)(elapsed * target);
        if (refill <= 0) return;
        _lastRefill = now;

        _bucketBytes = Math.Min(_capacityBytes, _bucketBytes + refill);
    }
}
