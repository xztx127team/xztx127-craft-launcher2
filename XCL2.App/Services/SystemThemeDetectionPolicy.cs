namespace XCL2.App.Services;

/// <summary>系统主题读取失败必须与有效的浅色值区分，不能覆盖用户的手动选择。</summary>
public static class SystemThemeDetectionPolicy
{
    public const string UnsupportedMessage =
        "Windows 10 1809（内部版本 17763）以下不支持本启动器的系统深浅色自动检测。请使用手动模式或按时间自动循环；当前配色保持不变。";

    public static bool TryParseThemeValue(object? value, out bool isDarkMode)
    {
        isDarkMode = false;
        if (value is not int theme || (theme != 0 && theme != 1)) return false;
        isDarkMode = theme == 0;
        return true;
    }
}
