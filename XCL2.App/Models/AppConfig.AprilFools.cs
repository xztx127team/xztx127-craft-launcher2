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
    [ExperiencePreference("趣味与彩蛋", "移动间隔", Description = "强力模式最多间隔 35 毫秒；F1 随时恢复。", Minimum = 16, Maximum = 1000, Unit = "毫秒")]
    public int AprilFoolsMoveIntervalMs { get; set; } = 35;
    [ExperiencePreference("趣味与彩蛋", "鼠标避让距离", Minimum = 40, Maximum = 500, Unit = "逻辑像素")]
    public int AprilFoolsMouseAvoidanceDistance { get; set; } = 240;
    [ExperiencePreference("趣味与彩蛋", "允许新增十五种视觉彩蛋", Description = "上浮、下坠、旋转、环绕、彩纸雨等效果；F1 随时恢复。") ]
    public bool AprilFoolsExtraEffects { get; set; } = true;
    // 保留旧配置字段供兼容；白旗始终直接退出，关闭总开关的挑战由专用流程处理。
    public bool AprilFoolsExitChallengeEnabled { get; set; } = true;
}
