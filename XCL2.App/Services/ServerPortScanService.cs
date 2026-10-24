using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

/// <summary>
/// TCP connect only. An open socket is not evidence of a Minecraft server or of available bandwidth.
/// Uses at most 32 background workers; memory is independent of the number of requested ports.
/// </summary>
public static class ServerPortScanService
{
    public const int MaximumAddresses = 64;
    public const int MaximumConcurrency = 32;
    public const int DefaultConnectTimeoutMilliseconds = 800;
    private const int MaximumAddressInputLength = 16384;

    public static bool TryCreateRequest(string addresses, int startPort, int endPort, int maximumPortsPerAddress,
        out ServerPortScanRequest? request, out string? error)
    {
        request = null;
        error = null;
        if (startPort is < 1 or > 65535 || endPort is < 1 or > 65535 || startPort > endPort)
        { error = "起止端口应为 1–65535，起始端口不能大于结束端口。"; return false; }
        if (maximumPortsPerAddress is < 1 or > 65535)
        { error = "每个 IP 的最大探测端口数应为 1–65535；从起始端口升序截取。"; return false; }
        if (string.IsNullOrWhiteSpace(addresses))
        { error = "请填写至少一个 IPv4 或 IPv6 地址。"; return false; }
        if (addresses.Length > MaximumAddressInputLength)
        { error = "地址输入过长；最多支持 64 个不同 IP。"; return false; }
        var parsed = new List<IPAddress>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Regex.Split(addresses.Trim(), @"[,;，；\s]+"))
        {
            if (item.Length == 0) continue;
            var token = item;
            if (token.StartsWith('[') && token.EndsWith(']')) token = token[1..^1];
            var scopeSeparator = token.IndexOf('%');
            var addressPart = scopeSeparator >= 0 ? token[..scopeSeparator] : token;
            if (!IPAddress.TryParse(token, out var address) ||
                (address.AddressFamily == AddressFamily.InterNetwork && !IsDottedIpv4(token)) ||
                (addressPart.Contains('.') && !IsDottedIpv4(addressPart[(addressPart.LastIndexOf(':') + 1)..])))
            { error = "无效 IP：" + item + "。只接受 IPv4/IPv6，不接受域名、CIDR、范围或附带端口的地址。"; return false; }
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (address.Equals(IPAddress.Any) ||
                (address.AddressFamily == AddressFamily.InterNetworkV6 && address.GetAddressBytes().All(value => value == 0)) || address.Equals(IPAddress.Broadcast)
                || address.IsIPv6Multicast || (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] is >= 224 and <= 239))
            { error = "地址 " + item + " 是未指定、广播或组播地址，不能作为 TCP 服务器目标。"; return false; }
            if (!seen.Add(address.ToString())) continue;
            parsed.Add(address);
            if (parsed.Count > MaximumAddresses)
            { error = "最多支持 64 个不同 IP；请减少目标地址。"; return false; }
        }
        if (parsed.Count == 0) { error = "请填写至少一个 IPv4 或 IPv6 地址。"; return false; }
        request = new ServerPortScanRequest(parsed.ToArray(), startPort, endPort, maximumPortsPerAddress);
        return true;
    }

    private static bool IsDottedIpv4(string input)
    {
        var parts = input.Split('.');
        return parts.Length == 4 && parts.All(part => part.Length > 0 && (part.Length == 1 || part[0] != '0') && part.All(char.IsAsciiDigit)
            && byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    public static Task<ServerPortScanSummary> ScanAsync(ServerPortScanRequest request,
        IProgress<ServerPortScanProgress>? progress = null, IProgress<ServerPortProbeResult>? openPorts = null,
        CancellationToken ct = default, TimeSpan? connectTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timeout = connectTimeout ?? TimeSpan.FromMilliseconds(DefaultConnectTimeoutMilliseconds);
        if (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(connectTimeout), "连接超时应为 1–60000 毫秒。");
        return ScanCoreAsync(request, (address, port, token) => ConnectAsync(address, port, timeout, token), progress, openPorts, ct);
    }

    // Separates bounded scheduling from sockets so cancellation/concurrency can be checked deterministically.
    internal static async Task<ServerPortScanSummary> ScanCoreAsync(ServerPortScanRequest request,
        Func<IPAddress, int, CancellationToken, Task<ServerPortProbeResult?>> probe,
        IProgress<ServerPortScanProgress>? progress, IProgress<ServerPortProbeResult>? openPorts, CancellationToken ct)
    {
        var addresses = request.Addresses.Select(IPAddress.Parse).ToArray();
        var total = request.TotalProbes;
        var gate = new object();
        var completed = 0; var open = 0; var refused = 0; var timedOut = 0; var errors = 0;
        var next = -1;
        string? lastError = null, notificationError = null;
        var watch = Stopwatch.StartNew();
        ServerPortScanProgress Snapshot()
        {
            lock (gate) return new(completed, total, open, refused, timedOut, errors);
        }
        void Report<T>(IProgress<T>? target, T value)
        {
            if (target == null) return;
            try { target.Report(value); }
            catch (Exception ex) { lock (gate) notificationError ??= ex.Message; }
        }
        using var reportingCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task ReportPeriodicallyAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                while (await timer.WaitForNextTickAsync(reportingCancellation.Token).ConfigureAwait(false))
                    Report(progress, Snapshot());
            }
            catch (OperationCanceledException) when (reportingCancellation.IsCancellationRequested) { }
        }
        Report(progress, Snapshot());
        // Start even the reporter away from the UI; user IProgress callbacks need not be dispatcher-bound.
        var reporting = progress == null ? Task.CompletedTask : Task.Run(ReportPeriodicallyAsync);
        async Task WorkerAsync()
        {
            while (!ct.IsCancellationRequested)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= total) return;
                // Round-robin targets at each ascending port so a large first-IP range does not
                // postpone all other targets. Every IP still receives exactly the same capped range.
                var address = addresses[index % addresses.Length];
                var port = request.StartPort + index / addresses.Length;
                ServerPortProbeResult? result;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    result = await probe(address, port, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                { result = new(address.ToString(), port, ServerPortProbeState.Error, 0, ex.Message); }
                if (result == null) return; // A caller-cancelled probe is not counted as a closed/error port.
                lock (gate)
                {
                    completed++;
                    switch (result.State)
                    {
                        case ServerPortProbeState.Open: open++; break;
                        case ServerPortProbeState.Refused: refused++; break;
                        case ServerPortProbeState.TimedOut: timedOut++; break;
                        default: errors++; lastError = result.DisplayAddress + "：" + result.ErrorMessage; break;
                    }
                }
                if (result.State == ServerPortProbeState.Open) Report(openPorts, result);
            }
        }
        // Never queue one Task per port. Task.Run also prevents synchronous local refusals from
        // repeatedly executing inside a WPF button handler before the first incomplete await.
        var workers = Enumerable.Range(0, Math.Min(MaximumConcurrency, total))
            .Select(_ => Task.Run(WorkerAsync)).ToArray();
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        finally
        {
            reportingCancellation.Cancel();
            await reporting.ConfigureAwait(false);
        }
        var final = Snapshot();
        Report(progress, final);
        return new(final.Completed, final.Total, final.Open, final.Refused, final.TimedOut, final.Errors,
            ct.IsCancellationRequested && final.Completed < total)
        {
            ElapsedMilliseconds = watch.ElapsedMilliseconds,
            LastError = lastError,
            NotificationError = notificationError
        };
    }

    internal static ServerPortProbeState ClassifySocketError(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => ServerPortProbeState.Refused,
        SocketError.TimedOut => ServerPortProbeState.TimedOut,
        _ => ServerPortProbeState.Error
    };

    private static async Task<ServerPortProbeResult?> ConnectAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(address, port), deadline.Token).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return null;
            return new(address.ToString(), port, ServerPortProbeState.Open, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (OperationCanceledException)
        { return new(address.ToString(), port, ServerPortProbeState.TimedOut, watch.ElapsedMilliseconds, "TCP 连接超时。"); }
        catch (SocketException) when (ct.IsCancellationRequested) { return null; }
        catch (SocketException ex)
        {
            var state = deadline.IsCancellationRequested ? ServerPortProbeState.TimedOut : ClassifySocketError(ex.SocketErrorCode);
            return new(address.ToString(), port, state, watch.ElapsedMilliseconds, ex.Message);
        }
        catch (Exception ex)
        { return new(address.ToString(), port, ServerPortProbeState.Error, watch.ElapsedMilliseconds, ex.Message); }
    }
}
