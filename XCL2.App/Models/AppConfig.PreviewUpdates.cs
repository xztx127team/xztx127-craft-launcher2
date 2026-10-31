namespace XCL2.App.Models;

public partial class AppConfig
{
    [ExperiencePreference("更新与预览", "用户体验改进计划（接收预览版更新提醒）",
        Description = "开启后，每次启动会检查比当前版本更高的快照或预览版（同时参考 GitHub 发布标记与版本号），由您选择是否安装；与正式版启动检查独立。不采集或上传使用数据。")]
    public bool EnableUserExperiencePreviewProgram { get; set; }

    /// <summary>Only exact preview release tags explicitly dismissed forever; Ignore never writes this list.</summary>
    public List<string> SuppressedLauncherPreviewTags { get; set; } = new();
}
