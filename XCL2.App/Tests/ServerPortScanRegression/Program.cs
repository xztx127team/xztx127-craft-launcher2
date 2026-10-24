using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
ServerPortScanRequest Request(string addresses, int start = 1, int end = 65535, int cap = 100)
{
    Check(ServerPortScanService.TryCreateRequest(addresses, start, end, cap, out var request, out var error), "Request rejected: " + error);
    return request!;
}
var parsed = Request("192.0.2.1, 2001:0db8:0000::1;\n[2001:db8::1]\t::ffff:192.0.2.1；127.0.0.1，::1");
Check(parsed.Addresses.SequenceEqual(new[] { "192.0.2.1", "2001:db8::1", "127.0.0.1", "::1" }), "Mixed separators, IPv6 canonicalization and mapped IPv4 deduplicate in first-seen order.");
Check(Request("fe80::192.0.2.1%4", 1, 1, 1).Addresses.Count == 1, "A valid IPv6 dotted tail retains its scope ID.");
Check(parsed.PortsPerAddress == 100 && parsed.EffectiveEndPort == 100 && parsed.TotalProbes == 400 && parsed.WasTruncated, "Port cap applies independently to each IP, ascending from start.");
var ending = Request("127.0.0.1", 65530, 65535, 100);
Check(ending.PortsPerAddress == 6 && ending.EffectiveEndPort == 65535 && !ending.WasTruncated, "A short requested range must not be extended by a larger cap.");
var sixtyFour = string.Join(',', Enumerable.Range(1, 64).Select(i => "192.0.2." + i));
var largest = Request(sixtyFour, 1, 65535, 65535);
Check(largest.Addresses.Count == 64 && largest.TotalProbes == 64 * 65535, "Maximum bounded plan remains counts plus 64 addresses.");
Check(!ServerPortScanService.TryCreateRequest(sixtyFour + ",192.0.2.65", 1, 1, 1, out _, out _), "A 65th unique IP is rejected.");
Check(Request(sixtyFour + ",192.0.2.1", 1, 1, 1).Addresses.Count == 64, "Duplicates do not consume the unique-IP limit.");
foreach (var bad in new[] { "", ", ; \n", "example.com", "192.0.2.1:25565", "[::1]:25565", "192.0.2.0/24", "192.0.2.1-5", "127.1", "1234", "010.0.0.1", "1.1.1.010", "::ffff:1.1.1.010", "192.0.2.256", "[garbage]", "0.0.0.0", "::", "::%4", "::ffff:0.0.0.0", "255.255.255.255", "224.0.0.1", "239.255.1.1", "ff02::1" })
    Check(!ServerPortScanService.TryCreateRequest(bad, 1, 1, 1, out _, out _), "Invalid/non-server/ambiguous target accepted: " + bad);
foreach (var ports in new[] { (0, 1, 1), (1, 65536, 1), (50, 49, 1), (1, 2, 0), (1, 2, 65536) })
    Check(!ServerPortScanService.TryCreateRequest("127.0.0.1", ports.Item1, ports.Item2, ports.Item3, out _, out _), "Invalid port bounds/cap accepted.");
Check(new ServerPortProbeResult("::1", 123, ServerPortProbeState.Open, 1).DisplayAddress == "[::1]:123", "IPv6 results keep the port unambiguous.");
Check(ServerPortScanService.ClassifySocketError(SocketError.ConnectionRefused) == ServerPortProbeState.Refused
    && ServerPortScanService.ClassifySocketError(SocketError.TimedOut) == ServerPortProbeState.TimedOut
    && ServerPortScanService.ClassifySocketError(SocketError.NetworkUnreachable) == ServerPortProbeState.Error,
    "Refusal, timeout and route errors are distinct outcomes.");

// Synthetic connectors never contact these documentation-only IPs. Check the real scheduler's
// bounded concurrency, selected endpoints and incremental result delivery independently of the OS.
var plan = Request("192.0.2.1 2001:db8::1", 100, 999, 40);
var seen = new ConcurrentBag<(string Address, int Port)>();
var progress = new ConcurrentQueue<ServerPortScanProgress>();
var openResults = new ConcurrentQueue<ServerPortProbeResult>();
var saturated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var active = 0; var peak = 0; var nonPoolCalls = 0;
using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
{
    var scan = ServerPortScanService.ScanCoreAsync(plan, async (address, port, token) =>
    {
        if (!Thread.CurrentThread.IsThreadPoolThread) Interlocked.Increment(ref nonPoolCalls);
        seen.Add((address.ToString(), port));
        var now = Interlocked.Increment(ref active);
        int before;
        do { before = Volatile.Read(ref peak); if (before >= now) break; } while (Interlocked.CompareExchange(ref peak, now, before) != before);
        if (now == 32) saturated.TrySetResult(true);
        try { await release.Task.WaitAsync(token); }
        finally { Interlocked.Decrement(ref active); }
        return new(address.ToString(), port, (port % 4) switch
        { 0 => ServerPortProbeState.Open, 1 => ServerPortProbeState.Refused, 2 => ServerPortProbeState.TimedOut, _ => ServerPortProbeState.Error }, 4, "synthetic");
    }, new InlineProgress<ServerPortScanProgress>(progress.Enqueue), new InlineProgress<ServerPortProbeResult>(openResults.Enqueue), deadline.Token);
    try
    {
        await saturated.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(seen.Count == 32 && peak == 32 && active == 32, "Only 32 endpoint probes are started while workers wait, not all 80.");
        var firstBatch = seen.ToArray();
        Check(firstBatch.GroupBy(item => item.Address).All(group => group.Count() == 16)
            && firstBatch.All(item => item.Port is >= 100 and <= 115), "Initial work round-robins IPs at ascending ports.");
    }
    finally { release.TrySetResult(true); }
    var summary = await scan;
    Check(summary is { Completed: 80, Total: 80, Open: 20, Refused: 20, TimedOut: 20, Errors: 20, Cancelled: false }, "Every outcome contributes once to the consistent final summary.");
    Check(nonPoolCalls == 0 && peak <= 32, "All probes run on background workers and concurrency never exceeds 32.");
}
Check(seen.Distinct().Count() == 80 && seen.All(item => item.Port is >= 100 and <= 139), "Every selected address/port occurs once; no probe exceeds the per-IP cap.");
Check(openResults.Count == 20 && openResults.All(result => result.State == ServerPortProbeState.Open), "Only actual Open results are reported incrementally.");
var snapshots = progress.ToArray();
Check(snapshots.First().Completed == 0 && snapshots.Last().Completed == 80
    && snapshots.All(item => item.Completed == item.Open + item.Refused + item.TimedOut + item.Errors), "Progress includes initial/final state and internally consistent counts.");
Check(snapshots.Select(item => item.Completed).SequenceEqual(snapshots.Select(item => item.Completed).Order()), "Progress cannot move backwards.");

var started = 0;
var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
using (var cancel = new CancellationTokenSource())
{
    var scan = ServerPortScanService.ScanCoreAsync(largest, async (address, port, token) =>
    {
        if (Interlocked.Increment(ref started) == 32) ready.TrySetResult(true);
        await Task.Delay(Timeout.Infinite, token);
        return null;
    }, null, null, cancel.Token);
    try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
    finally { cancel.Cancel(); }
    var watch = Stopwatch.StartNew();
    var summary = await scan.WaitAsync(TimeSpan.FromSeconds(2));
    Check(summary.Cancelled && summary.Completed == 0 && started == 32 && watch.Elapsed < TimeSpan.FromSeconds(2),
        "Cancel interrupts all pending probes promptly without scheduling the remaining four million endpoints or counting cancellation as failure.");
}
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    var summary = await ServerPortScanService.ScanAsync(largest, ct: cancelled.Token);
    Check(summary.Cancelled && summary.Completed == 0, "A pre-cancelled request starts no network probes.");
}
var faulted = await ServerPortScanService.ScanCoreAsync(Request("127.0.0.1", 1, 3, 3),
    (_, _, _) => throw new InvalidOperationException("synthetic failure"),
    new InlineProgress<ServerPortScanProgress>(_ => throw new InvalidOperationException("consumer failure")), null, CancellationToken.None);
Check(faulted is { Completed: 3, Errors: 3, Cancelled: false } && faulted.NotificationError != null && faulted.LastError != null,
    "Probe and consumer failures are captured without stranding other workers or corrupting port counters.");

// Actual production connector: one loopback listening socket, then the same closed port. No data is
// sent after the TCP handshake, and no external address is ever scanned by this test project.
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
try
{
    var result = await ServerPortScanService.ScanAsync(Request("127.0.0.1", localPort, localPort, 1), connectTimeout: TimeSpan.FromSeconds(1));
    Check(result is { Completed: 1, Open: 1, Errors: 0, Cancelled: false }, "Production TCP connector detects a real listening loopback port.");
    using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
    var read = new byte[1];
    Check(await accepted.GetStream().ReadAsync(read).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == 0,
        "Scanner closes immediately after TCP connect and sends no protocol/authentication payload.");
}
finally { listener.Stop(); }
var refusedResult = await ServerPortScanService.ScanAsync(Request("127.0.0.1", localPort, localPort, 1), connectTimeout: TimeSpan.FromSeconds(1));
Check(refusedResult is { Completed: 1, Refused: 1, Open: 0 }, "Production TCP connector distinguishes a closed loopback port from a timeout.");
Console.WriteLine($"Server TCP port-probe regression passed: {checks} checks. External hosts were not probed.");

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
