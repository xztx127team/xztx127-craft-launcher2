using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace XCL2.App.Services;

/// <summary>Only called by the background performance sampler; metadata is refreshed at most every 30 seconds.</summary>
internal static class PerformanceHardwareMetadata
{
    public static Dictionary<string, IReadOnlyDictionary<string, string>> Read()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        if (!OperatingSystem.IsWindows()) return result;
        try
        {
            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var fields = new Dictionary<string, string>();
            if (cpu?.GetValue("VendorIdentifier") is string vendor && !string.IsNullOrWhiteSpace(vendor)) fields["Manufacturer"] = vendor;
            // This registry value is firmware/system metadata, not a live clock measurement.
            if (cpu?.GetValue("~MHz") is int mhz && mhz > 0) fields["DefaultRate"] = $"{mhz} MHz（系统登记，非实时）";
            result["CPU"] = fields;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { }
        result["Memory"] = PerformanceMemoryMetadata.Read();
        try
        {
            var links = new List<string>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Take(32))
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    // NetworkInterface.Speed is bits/sec, distinct from the bytes/sec traffic counters.
                    if (nic.Speed > 0) links.Add(nic.Name + " " + (nic.Speed >= 1_000_000_000 ? $"{nic.Speed / 1_000_000_000d:0.##} Gbit/s" : $"{nic.Speed / 1_000_000d:0.##} Mbit/s"));
                }
                catch (NetworkInformationException) { }
            }
            if (links.Count > 0) result["Network"] = new Dictionary<string, string> { ["Speed"] = string.Join(" / ", links) };
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }
        return result;
    }
}
