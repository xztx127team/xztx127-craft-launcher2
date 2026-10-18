namespace XCL2.App.Models;

public partial class AppConfig
{
    [ExperiencePreference("下载细节", "显示真实重试倒计时", Description = "仅在下载器确实进入退避等待时显示，不改变重试次数或时长。")]
    public bool Ux50DownloadRetryCountdown { get; set; } = true;
    [ExperiencePreference("下载细节", "显示连接、等待、传输和校验阶段")]
    public bool Ux50DownloadStageDetails { get; set; } = true;
    [ExperiencePreference("下载细节", "启用下载提醒安静时段", Description = "时段内允许的下载完成提醒只进入右下角角标；原有不提醒偏好仍然生效，不修改模组更新提醒。")]
    public bool Ux50DownloadQuietHours { get; set; }
    [ExperiencePreference("下载细节", "安静时段开始", Description = "本地时间，24 小时制，例如 22:00。")]
    public string Ux50DownloadQuietStart { get; set; } = "22:00";
    [ExperiencePreference("下载细节", "安静时段结束", Description = "本地时间，例如 08:00；与开始相同时全天安静。")]
    public string Ux50DownloadQuietEnd { get; set; } = "08:00";
    [ExperiencePreference("下载细节", "任务详情显示发起页面")]
    public bool Ux50DownloadShowOrigin { get; set; } = true;
    [ExperiencePreference("下载细节", "下载面板速度曲线", Description = "默认关闭；仅在下载列表打开时按真实传输字节采样，不在后台积累历史。")]
    public bool Ux50DownloadSpeedGraph { get; set; }
}
