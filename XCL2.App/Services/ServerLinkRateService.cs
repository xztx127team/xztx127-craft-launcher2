using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;

namespace XCL2.App.Services;

public sealed record ServerLocalLink(string Name, string InterfaceType, long? BitsPerSecond, IReadOnlyList<IPAddress> Addresses);

/// <summary>OS-reported local link speed, not server bandwidth or Internet throughput.</summary>
public static class ServerLinkRateService
{
    public static Task<IReadOnlyList<ServerLocalLink>> DetectAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<ServerLocalLink>>(() => Read(ct), ct);

    private static IReadOnlyList<ServerLocalLink> Read(CancellationToken ct)
    {
        var result = new List<ServerLocalLink>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var addresses = nic.GetIPProperties().UnicastAddresses.Select(a => a.Address).ToArray();
                if (addresses.Length == 0) continue;
                long? speed = null;
                try { speed = nic.Speed > 0 ? nic.Speed : null; }
                catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }
                result.Add(new(nic.Name, nic.NetworkInterfaceType.ToString(), speed, addresses));
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }
        }
        return result;
    }

    public static ServerLocalLink? MatchLocalAddress(IEnumerable<ServerLocalLink> links, IPAddress? localAddress)
    {
        if (localAddress == null || IPAddress.IsLoopback(localAddress)) return null;
        var normalized = localAddress.IsIPv4MappedToIPv6 ? localAddress.MapToIPv4() : localAddress;
        // Do not guess the fastest/default adapter: VPNs and route-specific interfaces can differ.
        var matches = links.Where(link => link.Addresses.Any(address =>
            (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).Equals(normalized))).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static string FormatRate(long? bitsPerSecond)
    {
        if (bitsPerSecond is not > 0) return "暂不可用（驱动未报告）";
        var rate = bitsPerSecond.Value;
        var unit = rate >= 1_000_000_000 ? " Gbit/s" : rate >= 1_000_000 ? " Mbit/s" : " kbit/s";
        var divisor = rate >= 1_000_000_000 ? 1_000_000_000d : rate >= 1_000_000 ? 1_000_000d : 1_000d;
        return (rate / divisor).ToString("0.###", CultureInfo.InvariantCulture) + unit;
    }
}
