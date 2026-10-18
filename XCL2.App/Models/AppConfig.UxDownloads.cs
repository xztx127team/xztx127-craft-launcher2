namespace XCL2.App.Models;

public partial class AppConfig
{
    /// <summary>同时执行的下载任务数；0 保持原有不限制任务数的行为。单任务内部连接数仍遵循原设置。</summary>
    public int DownloadTaskConcurrency { get; set; }
    /// <summary>游戏运行时额外降低下载总速度，默认关闭。</summary>
    public bool ReduceDownloadSpeedWhileGaming { get; set; }
    public int GamingDownloadSpeedLimitKBps { get; set; } = 1024;
}
