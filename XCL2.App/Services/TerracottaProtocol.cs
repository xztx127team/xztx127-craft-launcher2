using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>Terracotta v0.4.2 上游 main.rs：--hmcl 接收 JSON 端口文件路径，不打开外部浏览器。</summary>
public static partial class TerracottaProtocol
{
    public static ProcessStartInfo CreateEmbeddedStart(string executable, string portFile)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
        };
        info.ArgumentList.Add("--hmcl");
        info.ArgumentList.Add(portFile);
        return info;
    }

    public static Uri ReadEndpoint(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("port", out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out var port) || port is < 1 or > 65535)
                throw new InvalidDataException("陶瓦返回的本地端口无效。");
            return new Uri($"http://127.0.0.1:{port}/");
        }
        catch (JsonException ex) { throw new InvalidDataException("陶瓦返回的端口文件不是有效的 JSON。", ex); }
    }

    public static bool IsSameOrigin(Uri endpoint, string candidate)
        => Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == "http" &&
            uri.Host == "127.0.0.1" && uri.Port == endpoint.Port && string.IsNullOrEmpty(uri.UserInfo);
}
