using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace XCL2.App.Services;

public sealed record ServerPingEndpoint(string Host, int Port)
{
    public string DisplayAddress => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

/// <summary>Java status-ping addresses only. Bare IPv6 never has an inferred trailing port.</summary>
public static class ServerPingAddress
{
    public const int DefaultPort = 25565;

    public static bool TryParse(string? address, string? portText, out ServerPingEndpoint? endpoint, out string? error)
    {
        endpoint = null;
        error = null;
        var input = address?.Trim() ?? "";
        if (input.StartsWith("minecraft://", StringComparison.OrdinalIgnoreCase)) input = input[12..];
        if (input.Length == 0) { error = "请先填写 Java 服务器地址。"; return false; }
        if (input.Contains("://", StringComparison.Ordinal) || input.IndexOfAny(new[] { '/', '\\', '?', '#', '@' }) >= 0
            || input.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        { error = "请填写 Minecraft Java 服务器域名或 IP，不要填写网页链接、路径或账号信息。"; return false; }

        string host;
        string? embeddedPort = null;
        if (input.StartsWith('['))
        {
            var end = input.IndexOf(']');
            if (end <= 1 || !IPAddress.TryParse(input[1..end], out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
            { error = "IPv6 地址格式不正确，例如 [2001:db8::1]:25565。"; return false; }
            host = ip.ToString();
            var suffix = input[(end + 1)..];
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith(':')) { error = "IPv6 方括号后只能填写 :端口。"; return false; }
                embeddedPort = suffix[1..];
            }
        }
        else if (IPAddress.TryParse(input, out var wholeIp)) host = wholeIp.ToString();
        else
        {
            var separator = input.IndexOf(':');
            if (separator >= 0)
            {
                if (separator != input.LastIndexOf(':'))
                { error = "IPv6 地址不正确；指定端口时请使用 [IPv6]:端口。"; return false; }
                host = input[..separator];
                embeddedPort = input[(separator + 1)..];
            }
            else host = input;
            try { host = new IdnMapping().GetAscii(host); }
            catch (ArgumentException) { error = "服务器域名格式不正确。"; return false; }
            if (host.Length == 0 || host.Length > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown
                || host.IndexOfAny(new[] { '[', ']' }) >= 0)
            { error = "服务器域名或 IP 格式不正确。"; return false; }
        }

        int? addressPort = null, separatePort = null;
        if (embeddedPort != null)
        {
            if (!TryPort(embeddedPort, out var parsed)) { error = "地址中的端口应为 1–65535；省略端口时请连同冒号一起删除。"; return false; }
            addressPort = parsed;
        }
        if (!string.IsNullOrWhiteSpace(portText))
        {
            if (!TryPort(portText.Trim(), out var parsed)) { error = "端口应为 1–65535，也可以留空使用默认端口 25565。"; return false; }
            separatePort = parsed;
        }
        if (addressPort.HasValue && separatePort.HasValue && addressPort != separatePort)
        { error = "地址中的端口与右侧端口不同，请保留其中一个或填写相同端口。"; return false; }
        endpoint = new ServerPingEndpoint(host, separatePort ?? addressPort ?? DefaultPort);
        return true;
    }

    private static bool TryPort(string text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value is > 0 and <= 65535;
}
