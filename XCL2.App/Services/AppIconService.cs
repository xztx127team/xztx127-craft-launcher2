using System.Windows;
using System.Windows.Media.Imaging;

namespace XCL2.App.Services;

public static class AppIconService
{
    private const string LightIconUri = "pack://application:,,,/Resources/app.ico";
    private const string DarkIconUri = "pack://application:,,,/Resources/app-dark.ico";

    // BitmapFrame 创建有磁盘 IO + 解码开销，而主题切换会遍历所有已打开窗口逐个赋值，
    // 缓存一份避免每次切换都重复解码。图标文件在程序生命周期内不会变，缓存是安全的。
    private static BitmapFrame? _lightCache;
    private static BitmapFrame? _darkCache;

    /// <summary>
    /// 取当前主题对应的图标。深色图标文件缺失时**自动回退到浅色图标**，
    /// 而不是抛异常——图标只是观感问题，绝不该因为少一个文件就让程序起不来。
    /// （比如用户只放了 app.ico、还没来得及放 app-dark.ico 就先编译了一次。）
    /// </summary>
    public static BitmapFrame? GetCurrentIcon()
    {
        var wantDark = ThemeService.CurrentIsDarkMode;

        if (wantDark)
        {
            _darkCache ??= TryLoad(DarkIconUri);
            if (_darkCache != null) return _darkCache;
            // 深色图标没放/损坏：回退浅色，不报错。
        }

        _lightCache ??= TryLoad(LightIconUri);
        return _lightCache;
    }

    /// <summary>给单个窗口应用当前主题对应的图标。窗口创建后调用一次即可。</summary>
    public static void ApplyTo(Window window)
    {
        try
        {
            var icon = GetCurrentIcon();
            if (icon != null) window.Icon = icon;
        }
        catch
        {
            // 设置图标失败不影响窗口本身能不能用，静默跳过。
        }
    }

    /// <summary>
    /// 给当前所有已打开的窗口刷新图标。由 ThemeService.RefreshOpenWindows 在每次
    /// 深浅色切换后调用，这样已经开着的窗口不用关掉重开就能看到新图标。
    /// 同时顺带刷新 MainWindow 自绘标题栏左上角那个 Image（如果当前打开的窗口
    /// 就是 MainWindow 的话），道理跟 Window.Icon 一样——深浅色模式要用两张不同的图。
    /// </summary>
    public static void ApplyToAllOpenWindows()
    {
        if (Application.Current == null) return;
        foreach (Window window in Application.Current.Windows)
        {
            // 触屏悬浮层没有标题栏、不进任务栏和 Alt-Tab，给它设图标没有意义，
            // 统一按全局豁免处理，保持"所有全局窗口处理都绕开它"这一条规则不出例外。
            if (WindowTreatmentPolicy.IsExempt(window)) continue;

            ApplyTo(window);
            if (window is Views.MainWindow main) ApplyToTitleBarImage(main, main.TitleBarIconImage);
        }
    }

    /// <summary>给自绘标题栏里那个 Image 控件赋当前主题对应的图标（跟 Window.Icon 是
    /// 同一张位图，共用同一份缓存，不会重复解码）。只有 MainWindow 用得到自绘标题栏，
    /// 其它 20 多个弹窗仍然是系统标题栏，不需要调用该方法。</summary>
    public static void ApplyToTitleBarImage(Window window, System.Windows.Controls.Image image)
    {
        try
        {
            var icon = GetCurrentIcon();
            if (icon != null) image.Source = icon;
        }
        catch
        {
            // 同 ApplyTo：图标只是观感问题，失败了静默跳过，不影响窗口本身。
        }
    }

    private static BitmapFrame? TryLoad(string uri)
    {
        try
        {
            return BitmapFrame.Create(
                new Uri(uri, UriKind.Absolute),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad); // OnLoad：立刻读完并释放文件流，避免占用资源句柄
        }
        catch
        {
            return null;
        }
    }
}
