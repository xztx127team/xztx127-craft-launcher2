using System.Linq;
using System.Windows;
using System.Windows.Media;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

public static class FontService
{
    /// <summary>本机已安装的系统字体家族名列表，按显示名称排序、去重，供设置页三个
    /// "分区字体"下拉框展示。取 Windows 当前 UI 文化下的本地化名字（比如"微软雅黑"
    /// 而不是"Microsoft YaHei"），用户更容易认得。</summary>
    public static IReadOnlyList<string> GetInstalledFontFamilyNames()
    {
        return Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>启动时 / 设置页保存后统一调用一次，把 AppConfig 里三个分区字段应用到
    /// MainWindow 对应的三个容器上。传入 null/空字符串表示该分区不覆盖，跟随全局字体。</summary>
    public static void ApplyScopedFonts(MainWindow window, AppConfig cfg)
    {
        ApplyToContainer(window.CustomTitleBar, cfg.AppFontFamily_TitleBar);
        ApplyToContainer(window.SidebarAreaBorder, cfg.AppFontFamily_Sidebar);
        ApplyToContainer(window.ContentAreaBorder, cfg.AppFontFamily_Content);
    }

    private static void ApplyToContainer(FrameworkElement container, string? fontFamilyName)
    {
        const string key = "AppFontFamily";
        if (string.IsNullOrWhiteSpace(fontFamilyName))
        {
            // "跟随全局"：该区块自己的 Resources 里不应该留一份旧的覆盖值，
            // 否则即使用户改回"跟随全局"也不会生效（旧值仍然会被优先找到）。
            if (container.Resources.Contains(key))
            {
                container.Resources.Remove(key);
            }
            return;
        }

        container.Resources[key] = new FontFamily(fontFamilyName);
    }
}
