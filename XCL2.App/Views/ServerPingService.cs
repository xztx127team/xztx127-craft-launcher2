using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>Java 1.7+ Server List Ping. Local link rate is reported separately from protocol RTT.</summary>
public static class ServerPingService
{
    public record PingResult(bool Success, string? MotdPlain, string? VersionName,
        int? OnlinePlayers, int? MaxPlayers, long? LatencyMs, string? ErrorMessage)
    {
        /// <summary>Valid status data survives a missing/invalid Pong; Success still requires a verified RTT.</summary>
        public bool StatusReceived { get; init; }
        public string? ServerAddress { get; init; }
        public string? ResolutionNote { get; init; }
        public long? ConnectionLatencyMs { get; init; }
        public ServerLocalLink? LocalLink { get; init; }
    }

    private const int MaximumPacketBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static Task<PingResult> PingAsync(string host, int port, CancellationToken ct = default)
    {
        if (!ServerPingAddress.TryParse(host, port.ToString(System.Globalization.CultureInfo.InvariantCulture), out var endpoint, out var error))
            return Task.FromResult(new PingResult(false, null, null, null, null, null, error));
        return PingAsync(endpoint!, ct);
    }

    public static async Task<PingResult> PingAsync(ServerPingEndpoint endpoint, CancellationToken ct = default)
    {
        try
        {
            var resolved = await MinecraftSrvResolver.ResolveAsync(endpoint, ct).ConfigureAwait(false);
            var result = await PingCoreAsync(resolved.Endpoint, TimeSpan.FromSeconds(5), ct, endpoint.Host).ConfigureAwait(false);
            return result with { ResolutionNote = resolved.Note };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        { return new(false, null, null, null, null, null, "地址解析失败：" + ex.Message) { ServerAddress = endpoint.DisplayAddress }; }
    }

    // Same code path, with a short deadline available to the local fake-server regression harness.
    internal static async Task<PingResult> PingCoreAsync(ServerPingEndpoint endpoint, TimeSpan stageTimeout, CancellationToken ct = default, string? handshakeHost = null)
    {
        ServerLocalLink? link = null;
        long? connectMs = null;
        string? version = null, motd = null;
        int? online = null, maximum = null;
        var statusReceived = false;
        var stage = "连接";
        PingResult Fail(string message) => new(false, motd, version, online, maximum, null,
            statusReceived ? "服务器状态已读取，但延迟测量失败：" + message : message)
            { StatusReceived = statusReceived, ServerAddress = endpoint.DisplayAddress,
                ConnectionLatencyMs = connectMs, LocalLink = link };
        try
        {
            ct.ThrowIfCancellationRequested();
            using var client = IPAddress.TryParse(endpoint.Host, out var literalAddress)
                ? new TcpClient(literalAddress.AddressFamily) : new TcpClient();
            using (var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectDeadline.CancelAfter(stageTimeout);
                var connectWatch = Stopwatch.StartNew();
                await client.ConnectAsync(endpoint.Host, endpoint.Port, connectDeadline.Token).ConfigureAwait(false);
                connectMs = connectWatch.ElapsedMilliseconds;
            }
            var localAddress = (client.Client.LocalEndPoint as IPEndPoint)?.Address;
            try
            {
                // Optional adapter metadata must never hold up the actual server request.
                using var metadataDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                metadataDeadline.CancelAfter(TimeSpan.FromMilliseconds(750));
                var adapters = await ServerLinkRateService.DetectAsync(metadataDeadline.Token)
                    .WaitAsync(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
                link = ServerLinkRateService.MatchLocalAddress(adapters, localAddress);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException or TimeoutException)
            { /* Link metadata is optional; failure must not discard a valid server probe. */ }

            stage = "读取服务器状态";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(stageTimeout);
            var token = deadline.Token;
            using var stream = client.GetStream();
            using var handshake = new MemoryStream();
            WriteVarInt(handshake, 0); WriteVarInt(handshake, -1);
            WriteString(handshake, handshakeHost ?? endpoint.Host);
            handshake.WriteByte((byte)(endpoint.Port >> 8)); handshake.WriteByte((byte)endpoint.Port);
            WriteVarInt(handshake, 1);
            await WritePacketAsync(stream, handshake.ToArray(), token).ConfigureAwait(false);
            await WritePacketAsync(stream, new byte[] { 0 }, token).ConfigureAwait(false);

            var response = await ReadPacketAsync(stream, token).ConfigureAwait(false);
            if (response.PacketId != 0) throw new InvalidDataException("服务器返回的状态包类型不正确。");
            using var payload = new MemoryStream(response.Payload);
            var jsonLength = ReadVarInt(payload);
            if (jsonLength < 0 || jsonLength != payload.Length - payload.Position)
                throw new InvalidDataException("服务器状态字符串长度不正确。");
            var json = StrictUtf8.GetString(response.Payload, (int)payload.Position, jsonLength);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("服务器未返回有效状态对象。");
            if (root.TryGetProperty("version", out var versionEl) && versionEl.ValueKind == JsonValueKind.Object
                && versionEl.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                version = name.GetString() is { } versionText ? versionText[..Math.Min(versionText.Length, 256)] : null;
            if (root.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Object)
            {
                if (players.TryGetProperty("online", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var n) && n >= 0) online = n;
                if (players.TryGetProperty("max", out var max) && max.ValueKind == JsonValueKind.Number && max.TryGetInt32(out var m) && m >= 0) maximum = m;
            }
            if (root.TryGetProperty("description", out var description))
            {
                var text = new StringBuilder();
                ExtractPlainText(description, text);
                motd = System.Text.RegularExpressions.Regex.Replace(text.ToString(), "§[0-9a-fk-orx]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            statusReceived = true;
            stage = "等待服务器 Pong";
            var ping = new byte[9]; ping[0] = 1;
            var nonce = Stopwatch.GetTimestamp();
            BinaryPrimitives.WriteInt64BigEndian(ping.AsSpan(1), nonce);
            var watch = Stopwatch.StartNew();
            await WritePacketAsync(stream, ping, token).ConfigureAwait(false);
            var pong = await ReadPacketAsync(stream, token).ConfigureAwait(false);
            watch.Stop();
            if (pong.PacketId != 1 || pong.Payload.Length != 8 || BinaryPrimitives.ReadInt64BigEndian(pong.Payload) != nonce)
                throw new InvalidDataException("服务器 Pong 与本次请求不匹配，不能计算有效延迟。");
            return new PingResult(true, motd, version, online, maximum, watch.ElapsedMilliseconds, null)
                { StatusReceived = true, ServerAddress = endpoint.DisplayAddress, ConnectionLatencyMs = connectMs, LocalLink = link };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Fail(stage + "超时；请检查地址、端口和服务器状态。"); }
        catch (OperationCanceledException) { throw; }
        catch (SocketException ex)
        {
            return Fail(ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "连接被拒绝，服务器可能已关闭或端口不对。",
                SocketError.HostNotFound => "找不到该地址，请检查域名/IP。",
                SocketError.TimedOut => "连接超时，请确认服务器在线。",
                _ => "网络错误：" + ex.Message
            });
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException)
        { return Fail("测速失败：" + ex.Message); }
    }

    private static void ExtractPlainText(JsonElement element, StringBuilder output)
    {
        if (output.Length >= 4096) return;
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString() ?? "";
            output.Append(text.AsSpan(0, Math.Min(text.Length, 4096 - output.Length)));
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) { ExtractPlainText(child, output); if (output.Length >= 4096) break; }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("text", out var text)) ExtractPlainText(text, output);
            if (element.TryGetProperty("extra", out var extra)) ExtractPlainText(extra, output);
        }
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var bits = unchecked((uint)value);
        while (bits >= 0x80) { stream.WriteByte((byte)((bits & 0x7F) | 0x80)); bits >>= 7; }
        stream.WriteByte((byte)bits);
    }
    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); WriteVarInt(stream, bytes.Length); stream.Write(bytes);
    }
    private static async Task WritePacketAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
        using var packet = new MemoryStream(); WriteVarInt(packet, payload.Length); packet.Write(payload);
        await stream.WriteAsync(packet.ToArray(), ct).ConfigureAwait(false);
    }
    private static async Task<(int PacketId, byte[] Payload)> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var prefix = new byte[5];
        var count = 0;
        do
        {
            if (count == prefix.Length) throw new InvalidDataException("VarInt 数据格式异常（超长）。");
            await stream.ReadExactlyAsync(prefix.AsMemory(count, 1), ct).ConfigureAwait(false);
        } while ((prefix[count++] & 0x80) != 0);
        using var sizeStream = new MemoryStream(prefix, 0, count);
        var length = ReadVarInt(sizeStream);
        if (length is <= 0 or > MaximumPacketBytes) throw new InvalidDataException("服务器数据包长度超出允许范围。");
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        using var packet = new MemoryStream(buffer);
        var id = ReadVarInt(packet);
        return (id, buffer[(int)packet.Position..]);
    }
    private static int ReadVarInt(Stream stream)
    {
        uint result = 0;
        for (var index = 0; index < 5; index++)
        {
            var value = stream.ReadByte();
            if (value < 0) throw new EndOfStreamException("服务器数据不完整。");
            if (index == 4 && (value & 0xF0) != 0) throw new InvalidDataException("VarInt 数据溢出。");
            result |= (uint)(value & 0x7F) << (index * 7);
            if ((value & 0x80) == 0) return unchecked((int)result);
        }
        throw new InvalidDataException("VarInt 数据格式异常（超长）。");
    }
}
