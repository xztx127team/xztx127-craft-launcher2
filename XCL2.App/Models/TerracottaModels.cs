namespace XCL2.App.Models;

/// <summary>Terracotta v0.4.2 实际公开的 HTTP 状态；Unknown 不等同于连接成功。</summary>
public enum TerracottaRoomMode
{
    Unknown, Waiting, HostScanning, HostStarting, Hosting,
    GuestConnecting, GuestStarting, Joined, Exception
}

public sealed record TerracottaMember(string MachineId, string Name, string Vendor, string Role)
{
    public string RoleText => Role switch { "HOST" => "房主", "LOCAL" => "本机", "GUEST" => "成员", _ => Role };
}

/// <summary>只确认本机 API 已无监听，不推断操作系统进程的最终退出码。</summary>
public sealed record TerracottaServiceStopResult(bool ConfirmedUnavailable, string Message);

public sealed record TerracottaFailure(int Code, string Title, string Message, string RepairSteps);

public sealed record TerracottaMetadata(string Version, string EasyTierVersion, string? CompileTimestamp,
    int? YggdrasilPort, string? TargetTuple, string? TargetArchitecture, string? TargetOs)
{
    public string? TargetVendor { get; init; }
    public string? TargetEnvironment { get; init; }
}

/// <summary>字段以 /state 返回值为准。guest-ok 不返回房间码，调用方可保留本次用户加入的码。</summary>
public sealed record TerracottaRoomState(
    string StateName, TerracottaRoomMode Mode, long Index, long? ProfileIndex,
    string? RoomCode, string? ConnectAddress, string? Difficulty,
    IReadOnlyList<TerracottaMember> Members, TerracottaFailure? Failure, string RawJson)
{
    public bool IsHost => Mode is TerracottaRoomMode.HostScanning or TerracottaRoomMode.HostStarting or TerracottaRoomMode.Hosting;
    public bool IsGuest => Mode is TerracottaRoomMode.GuestConnecting or TerracottaRoomMode.GuestStarting or TerracottaRoomMode.Joined;
    public bool IsBusy => Mode is TerracottaRoomMode.HostScanning or TerracottaRoomMode.HostStarting or TerracottaRoomMode.GuestConnecting or TerracottaRoomMode.GuestStarting;
    public bool CanCreateOrJoin => Mode == TerracottaRoomMode.Waiting;
    public bool IsConnected => Mode is TerracottaRoomMode.Hosting or TerracottaRoomMode.Joined;
    public string StatusText => Mode switch
    {
        TerracottaRoomMode.Waiting => "已就绪，等待创建或加入房间",
        TerracottaRoomMode.HostScanning => "正在发现局域网存档，请在 Minecraft 中打开存档并选择“对局域网开放”",
        TerracottaRoomMode.HostStarting => "已发现存档，正在创建联机房间",
        TerracottaRoomMode.Hosting => "房间已创建，等待好友加入",
        TerracottaRoomMode.GuestConnecting => "正在连接房主",
        TerracottaRoomMode.GuestStarting => "正在建立联机通道",
        TerracottaRoomMode.Joined => "已加入房间，可在 Minecraft 多人游戏中连接",
        TerracottaRoomMode.Exception => Failure?.Title ?? "陶瓦报告了未知异常",
        _ => "当前陶瓦版本返回了未识别状态：" + StateName
    };
    public string? DifficultyText => Difficulty switch
    {
        "UNKNOWN" => "正在探测连接难度",
        "EASIEST" => "连接难度：很容易",
        "SIMPLE" => "连接难度：容易",
        "MEDIUM" => "连接难度：中等",
        "TOUGH" => "连接难度：较高，可能需要更换网络",
        null => null,
        _ => "连接难度：" + Difficulty
    };
}

/// <summary>请求可能已经到达核心但未获得确认；调用方应刷新状态，不自动重发变更请求。</summary>
public sealed class TerracottaCommandException : System.IO.IOException
{
    public TerracottaCommandException(string message, Exception? inner = null) : base(message, inner) { }
}
