using System.Net;

namespace XCL2.App.Services;

/// <summary>A bounded plan; no per-address/port task list is materialized.</summary>
public sealed class ServerPortScanRequest
{
    internal ServerPortScanRequest(IPAddress[] addresses, int startPort, int endPort, int maximumPortsPerAddress)
    {
        Addresses = Array.AsReadOnly(addresses.Select(address => address.ToString()).ToArray());
        StartPort = startPort;
        EndPort = endPort;
        MaximumPortsPerAddress = maximumPortsPerAddress;
        PortsPerAddress = Math.Min(maximumPortsPerAddress, endPort - startPort + 1);
    }
    public IReadOnlyList<string> Addresses { get; }
    public int StartPort { get; }
    public int EndPort { get; }
    public int MaximumPortsPerAddress { get; }
    public int PortsPerAddress { get; }
    public int EffectiveEndPort => StartPort + PortsPerAddress - 1;
    public int TotalProbes => Addresses.Count * PortsPerAddress;
    public bool WasTruncated => EffectiveEndPort < EndPort;
}

public enum ServerPortProbeState { Open, Refused, TimedOut, Error }

public sealed record ServerPortProbeResult(string Address, int Port, ServerPortProbeState State,
    long ElapsedMilliseconds, string? ErrorMessage = null)
{
    public string DisplayAddress => Address.Contains(':') ? $"[{Address}]:{Port}" : $"{Address}:{Port}";
}

public sealed record ServerPortScanProgress(int Completed, int Total, int Open, int Refused, int TimedOut, int Errors);

public sealed record ServerPortScanSummary(int Completed, int Total, int Open, int Refused, int TimedOut, int Errors, bool Cancelled)
{
    public long ElapsedMilliseconds { get; init; }
    public string? LastError { get; init; }
    public string? NotificationError { get; init; }
}
