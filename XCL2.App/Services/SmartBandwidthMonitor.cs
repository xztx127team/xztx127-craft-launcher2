using System.Diagnostics;

namespace XCL2.App.Services;

public sealed class SmartBandwidthMonitor : IDisposable
{
    private const long OtherTrafficLowThresholdBytesPerSec = 200 * 1024;   // 低于该值：链路基本空闲，不限速
    private const long OtherTrafficHighThresholdBytesPerSec = 1024 * 1024; // 高于该值：明显有其他大流量活动，压到保守值
    private const long ThrottledTargetBytesPerSec = 512 * 1024;            // 判定"应该让路"时的目标下载速率
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    private readonly DownloadRateLimiter _limiter;
    private readonly int? _manualCapKBps; // 用户手动设置的固定上限（0/null=无手动上限），智能限速计算出的目标不能超过它
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    public SmartBandwidthMonitor(DownloadRateLimiter limiter, int manualCapKBps)
    {
        _limiter = limiter;
        _manualCapKBps = manualCapKBps > 0 ? manualCapKBps : null;
        _loopTask = Task.Run(MonitorLoopAsync);
    }

    /// <summary>仅供未调用 DownloadTransferPolicy.ConsumeAsync 的自定义传输登记字节；统一传输层会自动登记。</summary>
    public void ReportSelfBytes(int bytes) => DownloadTransferPolicy.ReportReceived(bytes);

    private async Task MonitorLoopAsync()
    {
        var nics = SafeGetNics();
        var lastTotal = SafeReadTotalBytes(nics);
        var lastTime = Stopwatch.GetTimestamp();
        var lastSelf = DownloadTransferPolicy.TotalReceivedBytes;

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SampleInterval, _cts.Token);
            }
            catch (OperationCanceledException) { break; }

            var now = Stopwatch.GetTimestamp();
            var elapsedSec = (now - lastTime) / (double)Stopwatch.Frequency;
            lastTime = now;
            if (elapsedSec <= 0) continue;

            var currentTotal = SafeReadTotalBytes(nics);
            var totalDelta = Math.Max(0, currentTotal - lastTotal);
            lastTotal = currentTotal;

            // 多个任务/不同下载页面共享同一累计计数，不能把其它XCL下载误认为外部抢带宽。
            var currentSelf = DownloadTransferPolicy.TotalReceivedBytes;
            var selfDelta = Math.Max(0, currentSelf - lastSelf);
            lastSelf = currentSelf;

            var totalRate = (long)(totalDelta / elapsedSec);
            var selfRate = (long)(selfDelta / elapsedSec);
            var otherRate = Math.Max(0, totalRate - selfRate);

            long targetBytesPerSec;
            if (otherRate <= OtherTrafficLowThresholdBytesPerSec)
            {
                targetBytesPerSec = 0; // 不限速
            }
            else if (otherRate >= OtherTrafficHighThresholdBytesPerSec)
            {
                targetBytesPerSec = ThrottledTargetBytesPerSec;
            }
            else
            {
                // 线性插值：其他流量在低/高阈值之间时，下载速率也在"不限速的一个较高参考值"
                // 和 ThrottledTargetBytesPerSec 之间线性过渡，避免在阈值边界附近速率反复横跳。
                var span = OtherTrafficHighThresholdBytesPerSec - OtherTrafficLowThresholdBytesPerSec;
                var ratio = (otherRate - OtherTrafficLowThresholdBytesPerSec) / (double)span;
                const long highRefBytesPerSec = 4 * 1024 * 1024; // "基本空闲"时给的参考速率上限
                targetBytesPerSec = (long)(highRefBytesPerSec - ratio * (highRefBytesPerSec - ThrottledTargetBytesPerSec));
            }

            if (_manualCapKBps is { } capKBps)
            {
                var capBytes = capKBps * 1024L;
                if (targetBytesPerSec <= 0 || targetBytesPerSec > capBytes)
                    targetBytesPerSec = capBytes;
            }

            _limiter.BytesPerSecond = targetBytesPerSec;
        }
    }

    private static List<System.Net.NetworkInformation.NetworkInterface> SafeGetNics()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                            && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .ToList();
        }
        catch
        {
            // 极少数环境下(权限受限/驱动异常)枚举网卡可能抛异常；智能限速是锦上添花功能，
            // 拿不到网卡列表就直接放弃采样（后续 SafeReadTotalBytes 对空列表返回 0，
            // 差值算出来的 otherRate 也会是 0，等价于"不限速"，不影响正常下载）。
            return new();
        }
    }

    private static long SafeReadTotalBytes(List<System.Net.NetworkInformation.NetworkInterface> nics)
    {
        long total = 0;
        foreach (var nic in nics)
        {
            try
            {
                var stats = nic.GetIPv4Statistics();
                total += stats.BytesReceived + stats.BytesSent;
            }
            catch { /* 单个网卡读取失败不影响其他网卡的统计 */ }
        }
        return total;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loopTask.Wait(TimeSpan.FromSeconds(1)); } catch { /* 忽略退出时的等待异常 */ }
        _cts.Dispose();
    }
}
