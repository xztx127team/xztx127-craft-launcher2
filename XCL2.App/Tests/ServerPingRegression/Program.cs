using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
void Address(string address, string? port, string host, int expectedPort)
{
    Check(ServerPingAddress.TryParse(address, port, out var result, out var error), "Address rejected: " + address + " " + error);
    Check(result!.Host == host && result.Port == expectedPort, "Unexpected parsed address: " + address);
}
Address("mc.example.com", "", "mc.example.com", 25565);
Address(" 127.0.0.1 ", null, "127.0.0.1", 25565);
Address("example.com:25566", "", "example.com", 25566);
Address("example.com:25566", "25566", "example.com", 25566);
Address("example.com", "19132", "example.com", 19132); // Explicit port does not switch protocol.
Address("[2001:db8::1]:25566", null, "2001:db8::1", 25566);
Address("[2001:db8::1]", "", "2001:db8::1", 25565);
Address("2001:db8::2556", "", "2001:db8::2556", 25565);
Address("minecraft://example.com", "", "example.com", 25565);
Address("例子.测试", "", "xn--fsqu00a.xn--0zwm56d", 25565);
Address("https://example.com/path?q=test#fragment", "", "example.com", 25565);
Address("http://example.com:80/path", "", "example.com", 80);
Address("https://example.com:443/path", "", "example.com", 443);
Address("https://[2001:db8::1]:25566/status", "", "2001:db8::1", 25566);
Address("https://例子.测试/status", "", "xn--fsqu00a.xn--0zwm56d", 25565);
Check(ServerPingAddress.TryParse("https://example.com/path", "", out var srvAddress, out _) && srvAddress!.UseSrvLookup,
    "A URL without an explicit port uses Minecraft SRV, not HTTP default port.");
Check(ServerPingAddress.TryParse("example.com:25565", "", out var directAddress, out _) && !directAddress!.UseSrvLookup,
    "Explicit ports disable SRV redirection.");
Check(ServerPingAddress.TryParse("127.0.0.1", "", out var literalAddress, out _) && !literalAddress!.UseSrvLookup,
    "IP literals do not query SRV.");
foreach (var bad in new[] { "", "example.com:", "example.com:0", "example.com:65536", "example.com:-1", "[2001:db8::1", "[::1]suffix", "[host]:25565", "ftp://example.com", "https://user@example.com", "user@example.com", "example.com/path", "example.com?x=1", "ex ample.com", "example.com:\n12" })
    Check(!ServerPingAddress.TryParse(bad, "", out _, out _), "Invalid address accepted: " + bad);
Check(!ServerPingAddress.TryParse("example.com:25566", "25565", out _, out _), "Conflicting ports must not silently choose a destination.");
Check(!ServerPingAddress.TryParse("example.com", "65536", out _, out _), "Invalid standalone port rejected.");
Check(new ServerPingEndpoint("::1", 25565).DisplayAddress == "[::1]:25565", "IPv6 display preserves unambiguous port.");
// Deterministic DNS packets exercise SRV parsing without a public DNS dependency.
var queryName = "_minecraft._tcp.example.com";
var query = MinecraftSrvResolver.BuildQuery(queryName, 1234);
byte[] DnsReply(string host, ushort port = 25566)
{
    using var packet = new MemoryStream();
    var header = query.ToArray(); header[2] = 0x81; header[3] = 0x80; header[7] = 1;
    packet.Write(header); packet.Write(new byte[] { 0xC0, 0x0C, 0, 33, 0, 1, 0, 0, 0, 60 });
    using var data = new MemoryStream();
    data.Write(new byte[] { 0, 5, 0, 10, (byte)(port >> 8), (byte)port });
    if (host.Length > 0) foreach (var label in host.Split('.')) { var b = Encoding.ASCII.GetBytes(label); data.WriteByte((byte)b.Length); data.Write(b); }
    data.WriteByte(0); packet.WriteByte((byte)(data.Length >> 8)); packet.WriteByte((byte)data.Length); packet.Write(data.ToArray());
    return packet.ToArray();
}
var srvRecords = MinecraftSrvResolver.ParseResponse(DnsReply("backend.example.com"), 1234, queryName);
Check(srvRecords.Count == 1 && srvRecords[0].Host == "backend.example.com" && srvRecords[0].Port == 25566,
    "SRV response uses its target host and port.");
Check(MinecraftSrvResolver.ParseResponse(DnsReply("", 0), 1234, queryName)[0].Host == "", "SRV root target is unavailable service.");
Check(MinecraftSrvResolver.SelectRecord(new[] {
    new MinecraftSrvResolver.SrvRecord(10, 65535, 1, "high.example.com"),
    new MinecraftSrvResolver.SrvRecord(0, 0, 2, "low.example.com") }).Port == 2, "SRV lowest priority wins.");
foreach (var packet in new[] { DnsReply("backend.example.com")[..^1], DnsReply("backend.example.com") })
{
    if (packet.Length == DnsReply("backend.example.com").Length) packet[0] ^= 1;
    try { MinecraftSrvResolver.ParseResponse(packet, 1234, queryName); throw new Exception("Malformed DNS response accepted."); }
    catch (InvalidDataException) { checks++; }
}
var cyclic = DnsReply("backend.example.com"); cyclic[12] = 0xC0; cyclic[13] = 12;
try { MinecraftSrvResolver.ParseResponse(cyclic, 1234, queryName); throw new Exception("DNS compression loop accepted."); }
catch (InvalidDataException) { checks++; }
var ethernet = new ServerLocalLink("Ethernet", "Ethernet", 2_500_000_000, new[] { IPAddress.Parse("192.0.2.3") });
var vpn = new ServerLocalLink("VPN", "Tunnel", 100_000_000, new[] { IPAddress.Parse("192.0.2.4") });
Check(ReferenceEquals(ServerLinkRateService.MatchLocalAddress(new[] { ethernet, vpn }, IPAddress.Parse("192.0.2.4")), vpn), "Route matching must not choose the fastest adapter.");
Check(ReferenceEquals(ServerLinkRateService.MatchLocalAddress(new[] { ethernet }, IPAddress.Parse("::ffff:192.0.2.3")), ethernet), "IPv4 mapped local address matches interface.");
Check(ServerLinkRateService.MatchLocalAddress(new[] { ethernet, ethernet }, IPAddress.Parse("192.0.2.3")) == null, "Ambiguous interface is unavailable, not guessed.");
Check(ServerLinkRateService.MatchLocalAddress(new[] { ethernet }, IPAddress.Loopback) == null, "Loopback has no physical negotiated link rate.");
Check(ServerLinkRateService.FormatRate(2_500_000_000) == "2.5 Gbit/s" && ServerLinkRateService.FormatRate(100_000_000) == "100 Mbit/s", "Link units use decimal bits/sec.");
Check(ServerLinkRateService.FormatRate(0).Contains("暂不可用") && ServerLinkRateService.FormatRate(null).Contains("暂不可用"), "No speed must not become fabricated zero throughput.");

async Task<ServerPingService.PingResult> Probe(string mode, TimeSpan? timeout = null, CancellationToken ct = default)
{
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var server = Task.Run(async () =>
    {
        using var peer = await listener.AcceptTcpClientAsync(lifetime.Token);
        using var stream = peer.GetStream();
        await ReadPacket(stream, lifetime.Token); // handshake
        await ReadPacket(stream, lifetime.Token); // status request
        if (mode == "stall") { await Task.Delay(Timeout.Infinite, lifetime.Token); return; }
        if (mode == "oversize") { await stream.WriteAsync(VarInt(1024 * 1024 + 1), lifetime.Token); return; }
        if (mode == "overflow") { await stream.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }, lifetime.Token); return; }
        if (mode == "negative") { await stream.WriteAsync(VarInt(-1), lifetime.Token); return; }
        var json = Encoding.UTF8.GetBytes("{\"version\":{\"name\":\"Test\"},\"players\":{\"online\":3,\"max\":20},\"description\":[{\"text\":\"Hello \"},\"world\"]}");
        var payload = new[] { (byte)(mode == "wrong-status" ? 2 : 0) }.Concat(VarInt(json.Length + (mode == "bad-string" ? 5 : 0))).Concat(json).ToArray();
        await SendPacket(stream, payload, lifetime.Token);
        if (mode is "bad-string" or "wrong-status") return;
        var ping = await ReadPacket(stream, lifetime.Token);
        if (mode == "wrong-pong") ping[0] = 0;
        if (mode == "wrong-nonce") ping[^1] ^= 1;
        await SendPacket(stream, ping, lifetime.Token);
    }, lifetime.Token);
    try { return await ServerPingService.PingCoreAsync(new("127.0.0.1", port), timeout ?? TimeSpan.FromSeconds(3), ct); }
    finally
    {
        lifetime.Cancel(); listener.Stop();
        try { await server; }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
    }
}
var success = await Probe("good");
Check(success.Success && success.MotdPlain == "Hello world" && success.OnlinePlayers == 3 && success.MaxPlayers == 20, "Valid Java SLP result, including text component arrays.");
Check(success.LatencyMs >= 0 && success.ConnectionLatencyMs >= 0 && success.LocalLink == null, "Measured RTT and connection cost remain distinct from unavailable loopback link rate.");
foreach (var mode in new[] { "wrong-status", "bad-string", "wrong-pong", "wrong-nonce", "oversize", "negative", "overflow" })
    Check(!(await Probe(mode)).Success, "Malformed server response accepted: " + mode);
var partialStatus = await Probe("wrong-nonce");
Check(!partialStatus.Success && partialStatus.StatusReceived && partialStatus.LatencyMs == null &&
    partialStatus.MotdPlain == "Hello world" && partialStatus.OnlinePlayers == 3 && partialStatus.VersionName == "Test",
    "A bad Pong never fabricates RTT and does not discard already verified status data.");
var watch = Stopwatch.StartNew();
Check(!(await Probe("stall", TimeSpan.FromMilliseconds(150))).Success && watch.Elapsed < TimeSpan.FromSeconds(3), "Async read timeout must bound stalled servers.");
using (var cancel = new CancellationTokenSource(150))
{
    try { await Probe("stall", ct: cancel.Token); throw new Exception("Caller cancellation was swallowed."); }
    catch (OperationCanceledException) { checks++; }
}
Console.WriteLine($"Server URL/SRV/link metadata/loopback protocol regression passed: {checks} checks. No public-server bandwidth test was performed.");

static byte[] VarInt(int value)
{
    using var result = new MemoryStream(); var bits = unchecked((uint)value);
    while (bits >= 128) { result.WriteByte((byte)((bits & 127) | 128)); bits >>= 7; }
    result.WriteByte((byte)bits); return result.ToArray();
}
static async Task SendPacket(NetworkStream stream, byte[] data, CancellationToken ct)
    => await stream.WriteAsync(VarInt(data.Length).Concat(data).ToArray(), ct);
static async Task<byte[]> ReadPacket(NetworkStream stream, CancellationToken ct)
{
    var length = 0; var offset = 0; var next = new byte[1];
    do
    {
        if (offset >= 35) throw new IOException("Bad test packet.");
        await stream.ReadExactlyAsync(next, ct); length |= (next[0] & 127) << offset; offset += 7;
    } while ((next[0] & 128) != 0);
    if (length is < 1 or > 1024 * 1024) throw new IOException("Bad test packet length.");
    var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct); return bytes;
}
