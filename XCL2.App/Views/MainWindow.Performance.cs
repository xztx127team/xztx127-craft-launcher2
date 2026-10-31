using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private MemoryOptimizationScheduler? _memoryOptimizationScheduler;
    /// <summary>鼠标穿透时从启动器恢复交互；保留设置按钮已有的右键项目。</summary>
    private void InitializeFloatingToolsContextMenu()
    {
        _memoryOptimizationScheduler ??= new MemoryOptimizationScheduler(this);
        var menu = NavSettingsButton.ContextMenu ??= new ContextMenu();
        const string tag = "PerformanceOverlayShortcuts";
        if (menu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, tag))) return;
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var performance = new MenuItem
        {
            Header = "性能悬浮窗", Tag = tag,
            ToolTip = "直接调整悬浮窗；开启鼠标穿透后，也可从这里关闭穿透并恢复拖动、右键操作。"
        };
        menu.Items.Add(performance);
        void Populate()
        {
            var cfg = ConfigService.Config;
            performance.Items.Clear();
            MemoryOptimizationMenu.AddMenu(performance, cfg, PerformanceOverlayService.UpdateSettings);
            performance.Items.Add(new Separator());
            AddToggle("显示性能悬浮窗", cfg.PerformanceOverlayEnabled,
                c => c.PerformanceOverlayEnabled = !c.PerformanceOverlayEnabled,
                "开启后显示系统与游戏性能；关闭后停止该悬浮窗的后台采样。");
            AddToggle("鼠标穿透", cfg.PerformanceOverlayClickThrough,
                c => c.PerformanceOverlayClickThrough = !c.PerformanceOverlayClickThrough,
                "默认关闭，可拖动性能窗并右键设置。开启后仅小齿轮接收鼠标：点击设置、按住拖动，其余区域交给后方窗口。");
            AddToggle("固定在最上层", cfg.PerformanceOverlayAlwaysOnTop,
                c => c.PerformanceOverlayAlwaysOnTop = !c.PerformanceOverlayAlwaysOnTop,
                "让性能窗保持在普通应用窗口上方。");
            AddToggle("强制置顶（优先于其他置顶窗口）", cfg.PerformanceOverlayForceTopmost,
                c => c.PerformanceOverlayForceTopmost = !c.PerformanceOverlayForceTopmost,
                "关闭此项仍保留普通置顶；只有上方固定在最上层开启时才会强制提升。");
            AddToggle("锁定位置", cfg.PerformanceOverlayLockPosition,
                c => c.PerformanceOverlayLockPosition = !c.PerformanceOverlayLockPosition,
                "防止误拖动。解锁后可拖动主体；开启穿透时可拖动小齿轮。");
            performance.Items.Add(new Separator());
            var reset = new MenuItem { Header = "重置悬浮窗位置", ToolTip = "将性能窗移回所在显示器的右上角，保留显示内容与颜色。" };
            reset.Click += (_, _) => PerformanceOverlayService.ResetWindowPosition();
            performance.Items.Add(reset);
            var details = new MenuItem { Header = "详细设置与字段模板…", ToolTip = "打开性能悬浮窗设置，可自定义文字、插入字段、选择配色和监测项目。" };
            details.Click += (_, _) => OpenPerformanceOverlaySettings();
            performance.Items.Add(details);
        }
        void AddToggle(string title, bool selected, Action<AppConfig> change, string hint)
        {
            var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = selected, ToolTip = hint };
            item.Click += (_, _) => PerformanceOverlayService.UpdateSettings(change);
            performance.Items.Add(item);
        }
        menu.Opened += (_, _) => Populate();
        Populate();
        NavSettingsButton.ToolTip = "打开启动器设置。右键可快速配置性能悬浮窗，包括关闭鼠标穿透、调整置顶或重置位置。";
        ToolTipService.SetShowDuration(NavSettingsButton, 12000);
    }

    /// <summary>悬浮窗右键的主动导航，沿用主窗口的未保存设置保护。</summary>
    public void OpenPerformanceOverlaySettings()
    {
        if (!NavSettingsButton.IsEnabled)
        {
            ToastService.ShowInfo("当前模式下未开放设置页面。");
            return;
        }
        RestoreFromTray();
        if (MainContent.Content is SettingsPage settings)
        {
            settings.RevealPerformanceSettings();
            return;
        }
        NavigateLazy(() => new SettingsPage(this), page => ((SettingsPage)page).RevealPerformanceSettings());
    }
}
