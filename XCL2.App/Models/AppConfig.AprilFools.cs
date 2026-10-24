namespace XCL2.App.Models;

public partial class AppConfig
{
    [ExperiencePreference("趣味与彩蛋", "允许愚人节彩蛋", Description = "关闭时直接进入闯关；窗口中可查看本次启动专用密码并验证关闭。点击小白旗或按 F1 可立即结束当天整蛊。")]
    public bool EnableAprilFoolsEasterEggs { get; set; } = true;
    [ExperiencePreference("趣味与彩蛋", "允许窗口移动")]
    public bool AprilFoolsWindowMovement { get; set; } = true;
    [ExperiencePreference("趣味与彩蛋", "允许启动按钮躲避")]
    public bool AprilFoolsButtonDodge { get; set; } = true;
    [ExperiencePreference("趣味与彩蛋", "允许磁贴跳动")]
    public bool AprilFoolsTileMovement { get; set; } = true;
    [ExperiencePreference("趣味与彩蛋", "允许彩蛋打开外部视频", Description = "只有自行允许才会打开浏览器。")]
    public bool AprilFoolsExternalVideos { get; set; }
    [ExperiencePreference("趣味与彩蛋", "允许模拟报错", Description = "模拟消息会标明彩蛋。")]
    public bool AprilFoolsFakeErrors { get; set; }
    [ExperiencePreference("趣味与彩蛋", "允许拒绝启动台词", Description = "视觉整蛊中成功触发启动时始终结束整蛊。")]
    public bool AprilFoolsLaunchRefusal { get; set; }
    [ExperiencePreference("趣味与彩蛋", "移动间隔", Description = "越小越快；仍遵守减少动画和低功耗设置。", Minimum = 100, Maximum = 1000, Unit = "毫秒")]
    public int AprilFoolsMoveIntervalMs { get; set; } = 150;
    [ExperiencePreference("趣味与彩蛋", "鼠标避让距离", Minimum = 40, Maximum = 200, Unit = "逻辑像素")]
    public int AprilFoolsMouseAvoidanceDistance { get; set; } = 100;
    // 保留旧配置字段供兼容；白旗始终直接退出，关闭总开关的挑战由专用流程处理。
    public bool AprilFoolsExitChallengeEnabled { get; set; } = true;
}
