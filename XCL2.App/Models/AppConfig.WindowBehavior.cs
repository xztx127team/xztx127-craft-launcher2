namespace XCL2.App.Models;

public partial class AppConfig
{
    [ExperiencePreference("窗口与快捷键", "ALT+F4 关闭启动器", Description = "默认开启。仅控制启动器主窗口的 ALT+F4，关闭按钮与系统退出不受影响。")]
    public bool EnableAltF4Close { get; set; } = true;

    [ExperiencePreference("窗口与快捷键", "自定义关闭快捷键", Description = "点击输入框后按下组合键（例如 Ctrl+Shift+Q），Backspace 或 Delete 清除。仅在启动器主窗口获得焦点时生效，沿用关闭按钮的确认与托盘设置。F3/F10/F11/F12、ALT+F4 保留给原有功能。")]
    public string LauncherCloseShortcut { get; set; } = "";

    [ExperiencePreference("窗口与快捷键", "启动器强制置顶", Description = "默认开启；仅在“启动器始终置顶”或 F3 临时置顶启用时生效。定期将自身提升到其他普通置顶窗口之前，不抢键盘焦点。关闭此项仍保留普通置顶。")]
    public bool LauncherForceTopmost { get; set; } = true;

    [ExperiencePreference("窗口与快捷键", "性能悬浮窗强制置顶", Description = "默认开启；与悬浮窗的“始终置顶”分开控制。关闭强制置顶后仍按原设置普通置顶。")]
    public bool PerformanceOverlayForceTopmost { get; set; } = true;
}
