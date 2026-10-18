namespace XCL2.App.Models;

public partial class AppConfig
{
    [ExperiencePreference("日志与辅助", "查看历史日志时暂停跟随，滚到底部后恢复")]
    public bool Ux50LogSmartFollow { get; set; } = true;
    [ExperiencePreference("日志与辅助", "复制选中日志时附带原始行号")]
    public bool Ux50LogCopyLineNumbers { get; set; }
    [ExperiencePreference("日志与辅助", "时间提示显示完整日期与时区")]
    public bool Ux50FullTimeTooltips { get; set; } = true;
    [ExperiencePreference("日志与辅助", "诊断导出前显示最终文件名与保存位置")]
    public bool Ux50ExportLocationPreview { get; set; } = true;
    [ExperiencePreference("日志与辅助", "数值与容量单位采用统一间距")]
    public bool Ux50UniformNumberSpacing { get; set; } = true;
    [ExperiencePreference("日志与辅助", "危险操作输入确认码后，还需按住确认按钮")]
    public bool Ux50HoldDangerousConfirm { get; set; }

    [ExperiencePreference("日志与辅助", "按住确认适用于系统设置与痕迹清理")]
    public bool Ux50HoldSystemOperations { get; set; } = true;
    [ExperiencePreference("日志与辅助", "按住确认也适用于强制启动")]
    public bool Ux50HoldForceLaunch { get; set; }

    // At most 20 per-version decisions. No automatic download or change to the update policy.
    public Dictionary<string, DateTimeOffset> Ux50UpdateReminderSnoozes { get; set; } = new();
}
