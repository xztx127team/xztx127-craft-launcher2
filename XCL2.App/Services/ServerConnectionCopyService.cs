using System.IO;

namespace XCL2.App.Services;

/// <summary>Normal game address copy never includes URL credentials or query parameters.</summary>
public static class ServerConnectionCopyService
{
    public static string AddressOnly(string? address)
    {
        var value = (address ?? "").Trim();
        if (value.Any(char.IsControl)) throw new FormatException("连接地址包含无效字符。");
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.Host.Contains(':') ? $"[{uri.Host.Trim('[', ']')}]:{uri.Port}" : uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var cut = value.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) value = value[..cut];
        var at = value.LastIndexOf('@');
        if (at >= 0) value = value[(at + 1)..];
        if (value.IndexOfAny(new[] { '/', '\\', ' ', '\t' }) >= 0 || value.Length > 512)
            throw new FormatException("连接地址格式无效。");
        return value;
    }

    /// <summary>Called only from the explicit sensitive-copy menu. No passwords are logged or retained.</summary>
    public static string BuildRconConnection(string directory, string address)
    {
        var path = Path.Combine(directory, "server.properties");
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 1024 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("无法读取此服务器的 RCON 配置。");
        string? password = null; var enabled = false; var port = 25575;
        foreach (var line in File.ReadLines(path))
        {
            var text = line.Trim();
            if (text.StartsWith('#') || text.StartsWith('!')) continue;
            var equals = text.IndexOf('='); if (equals < 0) continue;
            var key = text[..equals].Trim(); var value = text[(equals + 1)..];
            if (key == "enable-rcon") enabled = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            else if (key == "rcon.password") password = value;
            else if (key == "rcon.port" && int.TryParse(value, out var number) && number is > 0 and < 65536) port = number;
        }
        if (!enabled || string.IsNullOrEmpty(password)) throw new InvalidOperationException("此服务器尚未启用 RCON，或未配置 RCON 密码。");
        var gameAddress = AddressOnly(address);
        var host = gameAddress.StartsWith('[') ? gameAddress[..(gameAddress.IndexOf(']') + 1)] : gameAddress.Split(':')[0];
        return $"RCON 地址：{host}:{port}\nRCON 密码：{password}";
    }
}
