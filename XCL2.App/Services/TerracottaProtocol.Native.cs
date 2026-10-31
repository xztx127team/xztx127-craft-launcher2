using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services;

public enum TerracottaApiOperation { Metadata, State, CreateRoom, JoinRoom, LeaveRoom, Log, StopService }

public static partial class TerracottaProtocol
{
    private const string RoomAlphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private static readonly Regex RoomCodePattern = new(@"U/[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GameAddressPattern = new(@"^127\.0\.0\.1(?::(?<port>[0-9]{1,5}))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>与 v0.4.2 rooms/scaffolding/room.rs 相同：允许粘贴整段分享文本，I/O 分别视作 1/0，校验 base34 值 % 7。</summary>
    public static bool TryNormalizeRoomCode(string? text, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16384) return false;
        foreach (Match match in RoomCodePattern.Matches(text.ToUpperInvariant()))
        {
            var code = match.Value.Replace('I', '1').Replace('O', '0');
            UInt128 value = 0;
            var valid = true;
            for (var i = code.Length - 1; i >= 2; i--)
            {
                if (code[i] == '-') continue;
                var digit = RoomAlphabet.IndexOf(code[i]);
                if (digit < 0) { valid = false; break; }
                value = value * 34 + (uint)digit;
            }
            if (!valid || value % 7 != 0) continue;
            normalized = code;
            return true;
        }
        return false;
    }

    public static void ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" ||
            endpoint.Port is < 1 or > 65535 || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            endpoint.AbsolutePath != "/" || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("陶瓦 API 地址必须是握手得到的本机 HTTP 根地址。", nameof(endpoint));
    }

    /// <summary>固定上游路由；不允许把任意路径拼入本机控制接口。</summary>
    public static Uri CreateApiUri(Uri endpoint, TerracottaApiOperation operation, string? room = null,
        string? player = null, IReadOnlyList<string>? publicNodes = null)
    {
        ValidateEndpoint(endpoint);
        var path = operation switch
        {
            TerracottaApiOperation.Metadata => "meta",
            TerracottaApiOperation.State => "state",
            TerracottaApiOperation.CreateRoom => "state/scanning",
            TerracottaApiOperation.JoinRoom => "state/guesting",
            TerracottaApiOperation.LeaveRoom => "state/ide",
            TerracottaApiOperation.Log => "log?fetch=true",
            TerracottaApiOperation.StopService => "panic?peaceful=true",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        if (operation is not (TerracottaApiOperation.CreateRoom or TerracottaApiOperation.JoinRoom)) return new Uri(endpoint, path);
        var parameters = new List<string>();
        if (!string.IsNullOrWhiteSpace(room))
        {
            if (!TryNormalizeRoomCode(room, out var code)) throw new ArgumentException("房间码格式或校验不正确，请粘贴完整的 U/XXXX-XXXX-XXXX-XXXX 分享码。", nameof(room));
            parameters.Add("room=" + Uri.EscapeDataString(code));
        }
        else if (operation == TerracottaApiOperation.JoinRoom) throw new ArgumentException("请先填写房间码。", nameof(room));
        if (!string.IsNullOrWhiteSpace(player))
        {
            var name = player.Trim();
            if (name.Length > 128 || name.Any(char.IsControl)) throw new ArgumentException("联机昵称不能包含控制字符，且最多 128 个字符。", nameof(player));
            parameters.Add("player=" + Uri.EscapeDataString(name));
        }
        if (publicNodes?.Count > 32) throw new ArgumentException("附加公共节点最多为 32 个。", nameof(publicNodes));
        foreach (var node in publicNodes ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(node)) continue;
            var address = node.Trim();
            if (address.Length > 2048 || address.Any(char.IsWhiteSpace) || address.Any(char.IsControl) ||
                !Uri.TryCreate(address, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("公共节点必须是完整的节点地址，例如 tcp://example.com:11010。", nameof(publicNodes));
            parameters.Add("public_nodes=" + Uri.EscapeDataString(address));
        }
        return new Uri(endpoint, path + (parameters.Count == 0 ? "" : "?" + string.Join("&", parameters)));
    }

    public static TerracottaMetadata ParseMetadata(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = RequireObject(document.RootElement);
            var version = RequiredString(root, "version");
            var easytier = RequiredString(root, "easytier_version");
            var bridgePort = OptionalInteger(root, "yggdrasil_port");
            if (bridgePort is < 1 or > 65535) throw new InvalidDataException("陶瓦返回的本机桥接端口无效。");
            return new TerracottaMetadata(version, easytier, OptionalString(root, "compile_timestamp"),
                bridgePort is { } port ? checked((int)port) : null,
                OptionalString(root, "target_tuple"), OptionalString(root, "target_arch"), OptionalString(root, "target_os"))
            { TargetVendor = OptionalString(root, "target_vendor"), TargetEnvironment = OptionalString(root, "target_env") };
        }
        catch (JsonException ex) { throw new InvalidDataException("陶瓦元数据不是有效的 JSON。", ex); }
    }

    public static TerracottaRoomState ParseState(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = RequireObject(document.RootElement);
            var name = RequiredString(root, "state");
            var index = OptionalInteger(root, "index") ?? throw new InvalidDataException("陶瓦状态缺少 index。请检查所选程序是否兼容 v0.4.2 API。");
            if (index is < 0 or > uint.MaxValue) throw new InvalidDataException("陶瓦返回的状态序号无效。");
            var mode = name switch
            {
                "waiting" => TerracottaRoomMode.Waiting,
                "host-scanning" => TerracottaRoomMode.HostScanning,
                "host-starting" => TerracottaRoomMode.HostStarting,
                "host-ok" => TerracottaRoomMode.Hosting,
                "guest-connecting" => TerracottaRoomMode.GuestConnecting,
                "guest-starting" => TerracottaRoomMode.GuestStarting,
                "guest-ok" => TerracottaRoomMode.Joined,
                "exception" => TerracottaRoomMode.Exception,
                _ => TerracottaRoomMode.Unknown
            };
            var room = OptionalString(root, "room");
            if (room != null && !TryNormalizeRoomCode(room, out _)) throw new InvalidDataException("陶瓦返回的房间码无效。");
            var address = OptionalString(root, "url");
            if (address != null)
            {
                var match = GameAddressPattern.Match(address);
                if (!match.Success || (match.Groups["port"].Success &&
                    (!int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)))
                    throw new InvalidDataException("陶瓦返回的游戏连接地址不是受支持的本机地址。");
            }
            if (mode == TerracottaRoomMode.Joined && address == null) throw new InvalidDataException("已连接状态缺少游戏地址，暂时不能确认连接就绪。");
            if ((mode is TerracottaRoomMode.HostStarting or TerracottaRoomMode.Hosting or TerracottaRoomMode.GuestConnecting or TerracottaRoomMode.GuestStarting) && room == null)
                throw new InvalidDataException("房间状态缺少分享码，请刷新或检查陶瓦版本。");
            var members = new List<TerracottaMember>();
            if (root.TryGetProperty("profiles", out var profiles))
            {
                if (profiles.ValueKind != JsonValueKind.Array || profiles.GetArrayLength() > 1024) throw new InvalidDataException("陶瓦返回的成员列表无效或过大。");
                foreach (var profile in profiles.EnumerateArray())
                {
                    RequireObject(profile);
                    var memberName = OptionalString(profile, "name");
                    var vendor = OptionalString(profile, "vendor");
                    members.Add(new TerracottaMember(PresentString(profile, "machine_id"),
                        string.IsNullOrWhiteSpace(memberName) ? "未命名成员" : memberName,
                        string.IsNullOrWhiteSpace(vendor) ? "未标明客户端" : vendor,
                        RequiredString(profile, "kind")));
                }
            }
            else if (mode is TerracottaRoomMode.Hosting or TerracottaRoomMode.Joined)
                throw new InvalidDataException("房间已连接，但未返回成员快照，请刷新状态。");
            TerracottaFailure? failure = null;
            if (mode == TerracottaRoomMode.Exception)
            {
                var code = OptionalInteger(root, "type");
                if (code is null or < int.MinValue or > int.MaxValue) throw new InvalidDataException("陶瓦异常状态缺少有效错误类型。");
                failure = DescribeFailure((int)code.Value);
            }
            var profileIndex = OptionalInteger(root, "profile_index");
            if (profileIndex is < 0 or > uint.MaxValue || profileIndex > index)
                throw new InvalidDataException("陶瓦返回的成员快照序号无效。");
            return new TerracottaRoomState(name, mode, index, profileIndex, room, address,
                OptionalString(root, "difficulty"), members.ToArray(), failure, json);
        }
        catch (JsonException ex) { throw new InvalidDataException("陶瓦状态不是有效的 JSON。", ex); }
    }

    public static TerracottaFailure DescribeFailure(int code) => code switch
    {
        0 => new(code, "加入房间失败", "房间已关闭或当前网络不稳定。", "确认房主仍在同一存档并开启局域网；重新核对分享码，尝试更换网络后再加入。"),
        1 => new(code, "房间连接断开", "与房主的连接已断开，房间可能关闭或网络不稳定。", "联系房主确认房间状态；检查双方网络，离开后重新加入。"),
        2 => new(code, "加入房间失败", "客户端 EasyTier 核心已退出。", "离开房间后重试，检查安全软件是否拦截；仍失败请导出联机日志反馈给开发者。"),
        3 => new(code, "创建房间失败", "房主 EasyTier 核心已退出。", "确认防火墙允许陶瓦及内置 EasyTier，重新创建；仍失败请保留联机日志。"),
        4 => new(code, "房间已关闭", "游戏存档已退出或局域网服务器已停止，陶瓦自动关闭了房间。", "重新打开 Minecraft 存档并选择“对局域网开放”，然后重新创建房间。"),
        5 => new(code, "房间协议错误", "房主返回的数据不符合陶瓦协议。", "双方更新到兼容的陶瓦版本，核对房间码并重试；仍失败请导出联机日志。"),
        _ => new(code, "未知陶瓦异常", "陶瓦返回了未识别的错误类型：" + code, "先离开房间，查看联机日志；确认双方陶瓦版本兼容后重试。")
    };

    private static JsonElement RequireObject(JsonElement element)
        => element.ValueKind == JsonValueKind.Object ? element : throw new InvalidDataException("陶瓦返回的 JSON 根节点不是对象。");
    private static string PresentString(JsonElement element, string name)
        => OptionalString(element, name) ?? throw new InvalidDataException("陶瓦返回的数据缺少字段：" + name);
    private static string RequiredString(JsonElement element, string name)
        => OptionalString(element, name) is { Length: > 0 } value ? value : throw new InvalidDataException("陶瓦返回的数据缺少有效字段：" + name);
    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidDataException("陶瓦返回的字段类型不正确：" + name);
    }
    private static long? OptionalInteger(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result : throw new InvalidDataException("陶瓦返回的字段类型不正确：" + name);
    }
}
