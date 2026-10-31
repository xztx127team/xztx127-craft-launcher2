using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>Presentation only: labels never become navigation identifiers.</summary>
public static class SidebarPreferences
{
    public const int MaximumLabelLength = 32;

    public static IReadOnlyList<(string Key, string? ResourceKey, string DefaultText)> Labels { get; } =
        new (string, string?, string)[]
        {
            ("home", "Str_Nav_Home", "主页"),
            ("instances", "Str_Nav_Versions", "版本选择"),
            ("downloads", "Str_Nav_Download", "下载"),
            ("multiplayer", "Str_Nav_Multiplayer", "联机"),
            ("worlds", null, "存档"),
            ("mods", "Str_Nav_ModManager", "模组管理"),
            ("servers", "Str_Nav_ServerManager", "服务器管理"),
            ("toolbox", "Str_Nav_Toolbox", "百宝箱"),
            ("bedrock", "Str_Nav_Bedrock", "基岩版"),
            ("about", "Str_Nav_AboutHelp", "鸣谢与帮助"),
            ("accounts", "Str_Nav_Accounts", "账户管理"),
            ("plugins", null, "插件管理"),
            ("settings", "Str_Nav_Settings", "设置"),
            ("more", null, "更多"),
            ("logs", "Str_Nav_Logs", "日志"),
            ("ai", null, "AI助手"),
            ("experimental", "Str_Nav_Experimental", "实验性功能")
        };

    public static string NormalizeLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var builder = new StringBuilder();
        var length = 0;
        foreach (var rune in value.Trim().EnumerateRunes())
        {
            if (Rune.IsControl(rune)) continue;
            if (length++ == MaximumLabelLength) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString().Trim();
    }

    public static double ExpandedWidth(int configured, bool microsoftChinese, bool preview)
        => configured > 0 ? Math.Clamp(configured, 160, 360)
            : microsoftChinese ? 220 : preview ? 192 : 180;

    public static bool NeedsCompactLayout(double windowWidth, int configuredWidth, bool microsoftChinese, bool preview)
    {
        var originalWidth = ExpandedWidth(0, microsoftChinese, preview);
        var extraWidth = Math.Max(0, ExpandedWidth(configuredWidth, microsoftChinese, preview) - originalWidth);
        // A wider custom sidebar must not take space away from the legacy page's usable minimum.
        return windowWidth < 760 + extraWidth;
    }

    public static int SidebarColumn(SidebarPlacement placement) => placement == SidebarPlacement.Right ? 1 : 0;
}
