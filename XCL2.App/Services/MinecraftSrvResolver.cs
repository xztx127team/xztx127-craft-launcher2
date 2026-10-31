using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace XCL2.App.Services;

/// <summary>Bounded RFC 2782 lookup using only DNS servers already configured on the computer.</summary>
public static class MinecraftSrvResolver
{
    internal sealed record SrvRecord(int Priority, int Weight, int Port, string Host);
    public sealed record Resolution(ServerPingEndpoint Endpoint, string? Note);

    public static async Task<Resolution> ResolveAsync(ServerPingEndpoint original, CancellationToken ct = default)
    {
        if (!original.UseSrvLookup || IPAddress.TryParse(original.Host, out _)) return new(original, null);
        var name = "_minecraft._tcp." + original.Host.TrimEnd('.');
        IPAddress[] servers;
        try
        {
            servers = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().DnsAddresses).Distinct().Take(3).ToArray();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        { return new(original, "无法读取系统 DNS，使用原地址及默认端口 25565。"); }
        foreach (var server in servers)
        {
            ct.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(1200));
            try
            {
                var id = (ushort)RandomNumberGenerator.GetInt32(65536);
                var request = BuildQuery(name, id);
                using var client = new UdpClient(server.AddressFamily);
                client.Connect(server, 53);
                await client.SendAsync(request.AsMemory(), deadline.Token).ConfigureAwait(false);
                var reply = await client.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                var records = ParseResponse(reply.Buffer, id, name);
                if (records.Count == 0) return new(original, "未发现 SRV 记录，使用默认端口 25565。");
                if (records.Any(r => r.Host.Length == 0))
                    throw new InvalidOperationException("此域名的 SRV 记录明确声明 Minecraft 服务不可用，请向服主确认地址。");
                var selected = SelectRecord(records);
                var target = new ServerPingEndpoint(selected.Host, selected.Port);
                return new(target, "SRV 解析：" + original.Host + " → " + target.DisplayAddress);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is SocketException or IOException or ArgumentException) { }
        }
        ct.ThrowIfCancellationRequested();
        return new(original, "SRV 查询未完成，回退到原域名及默认端口 25565；若服务器使用特殊端口，请手动填写。");
    }

    internal static SrvRecord SelectRecord(IReadOnlyList<SrvRecord> records)
    {
        var priority = records.Min(r => r.Priority);
        var candidates = records.Where(r => r.Priority == priority).OrderBy(r => r.Weight).ToArray();
        var sum = candidates.Sum(r => r.Weight);
        if (sum == 0) return candidates[RandomNumberGenerator.GetInt32(candidates.Length)];
        var pick = RandomNumberGenerator.GetInt32(sum + 1);
        var running = 0;
        foreach (var candidate in candidates) { running += candidate.Weight; if (running >= pick) return candidate; }
        return candidates[^1];
    }

    internal static byte[] BuildQuery(string name, ushort id)
    {
        if (Encoding.ASCII.GetByteCount(name.TrimEnd('.')) > 253) throw new ArgumentException("DNS 查询名称过长。");
        using var stream = new MemoryStream();
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        header[2] = 1; header[5] = 1; // recursion desired, one question
        stream.Write(header);
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is < 1 or > 63) throw new ArgumentException("DNS 域名标签长度不正确。");
            stream.WriteByte((byte)bytes.Length); stream.Write(bytes);
        }
        stream.Write(new byte[] { 0, 0, 33, 0, 1 }); // SRV / IN
        if (stream.Length > 512) throw new ArgumentException("DNS 查询过长。");
        return stream.ToArray();
    }

    internal static IReadOnlyList<SrvRecord> ParseResponse(byte[] packet, ushort id, string expectedName)
    {
        if (packet.Length is < 12 or > 65535 || Read16(packet, 0) != id)
            throw new InvalidDataException("DNS 响应编号不匹配。");
        var flags = Read16(packet, 2);
        if ((flags & 0xF800) != 0x8000 || (flags & 0x0200) != 0 || (flags & 0x000F) is not (0 or 3))
            throw new InvalidDataException("DNS 响应异常或被截断。");
        if (Read16(packet, 4) != 1) throw new InvalidDataException("DNS 问题数量不正确。");
        var offset = 12;
        var question = ReadName(packet, ref offset);
        if (!question.Equals(expectedName.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            || Read16(packet, offset) != 33 || Read16(packet, offset + 2) != 1)
            throw new InvalidDataException("DNS 响应问题不匹配。");
        offset += 4;
        if ((flags & 0x000F) == 3) return Array.Empty<SrvRecord>();
        var result = new List<SrvRecord>();
        var answers = Read16(packet, 6);
        if (answers > 256) throw new InvalidDataException("DNS 响应记录过多。");
        for (var index = 0; index < answers; index++)
        {
            var owner = ReadName(packet, ref offset);
            var type = Read16(packet, offset); var recordClass = Read16(packet, offset + 2);
            var length = Read16(packet, offset + 8); offset += 10;
            var end = checked(offset + length);
            if (end > packet.Length) throw new InvalidDataException("DNS 记录不完整。");
            if (type == 33 && recordClass == 1 && owner.Equals(expectedName.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            {
                if (length < 7) throw new InvalidDataException("SRV 记录不完整。");
                var priority = Read16(packet, offset); var weight = Read16(packet, offset + 2); var port = Read16(packet, offset + 4);
                var cursor = offset + 6;
                var host = ReadName(packet, ref cursor);
                if (cursor != end || (host.Length > 0 && (port == 0 || Uri.CheckHostName(host) != UriHostNameType.Dns)))
                    throw new InvalidDataException("SRV 目标格式不正确。");
                result.Add(new(priority, weight, port, host));
            }
            offset = end;
        }
        return result;
    }

    private static ushort Read16(byte[] data, int offset)
    {
        if (offset < 0 || offset > data.Length - 2) throw new InvalidDataException("DNS 数据不完整。");
        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    }
    private static string ReadName(byte[] packet, ref int offset)
    {
        var cursor = offset; var jumped = false; var hops = 0;
        var labels = new List<string>(); var length = 0;
        while (true)
        {
            if (cursor >= packet.Length || ++hops > 128) throw new InvalidDataException("DNS 名称越界或循环。");
            var size = packet[cursor++];
            if (size == 0) { if (!jumped) offset = cursor; return string.Join(".", labels); }
            if ((size & 0xC0) == 0xC0)
            {
                if (cursor >= packet.Length) throw new InvalidDataException("DNS 指针不完整。");
                var target = ((size & 0x3F) << 8) | packet[cursor++];
                if (!jumped) offset = cursor;
                jumped = true; cursor = target; continue;
            }
            if (size > 63 || cursor + size > packet.Length || (length += size + 1) > 254)
                throw new InvalidDataException("DNS 名称格式不正确。");
            labels.Add(Encoding.ASCII.GetString(packet, cursor, size)); cursor += size;
        }
    }
}
