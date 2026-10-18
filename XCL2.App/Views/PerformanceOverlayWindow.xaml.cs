using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>仅在自身窗口范围接收鼠标，不安装全局键鼠钩子，不激活游戏外的主窗口。</summary>
public partial class PerformanceOverlayWindow : Window, IGlobalWindowTreatmentExempt
{
    private readonly Func<AppConfig> _config;
    private readonly Action<Action<AppConfig>> _changeSettings;
    private readonly Action _openSettings;
    private readonly Action<double, double> _savePosition;
    private PerformanceSnapshot? _snapshot;
    private HwndSource? _source;

    public PerformanceOverlayWindow(Func<AppConfig> config, Action<Action<AppConfig>> changeSettings,
        Action openSettings, Action<double, double> savePosition)
    {
        _config = config;
        _changeSettings = changeSettings;
        _openSettings = openSettings;
        _savePosition = savePosition;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000 | 0x00000080);
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WindowHook);
        };
        Loaded += (_, _) => ResetPosition(useSavedPosition: true);
        SizeChanged += (_, _) => { if (IsLoaded) ResetPosition(useSavedPosition: false); };
        Closed += (_, _) => { _source?.RemoveHook(WindowHook); _source = null; };
        ApplySettings();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr window, int index, int value);

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE / MA_NOACTIVATE：交互不会抢走游戏焦点。
        {
            handled = true;
            return new IntPtr(3);
        }
        return IntPtr.Zero;
    }

    public void ApplySettings()
    {
        var cfg = _config();
        FontSize = PerformanceOverlaySettings.FontSize(cfg);
        Opacity = PerformanceOverlaySettings.Opacity(cfg);
        Topmost = cfg.PerformanceOverlayAlwaysOnTop;
        Width = PerformanceOverlaySettings.WindowWidth(cfg);
        switch (PerformanceOverlaySettings.ColorScheme(cfg))
        {
            case "Dark":
                Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(20, 27, 34));
                Resources["TextPrimaryBrush"] = Brushes.White;
                Resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(185, 199, 211));
                Resources["DividerBrush"] = new SolidColorBrush(Color.FromRgb(70, 86, 100));
                break;
            case "Light":
                Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(250, 252, 255));
                Resources["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(25, 35, 45));
                Resources["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(85, 103, 118));
                Resources["DividerBrush"] = new SolidColorBrush(Color.FromRgb(165, 182, 196));
                break;
            default:
                foreach (var key in new[] { "PanelBrush", "TextPrimaryBrush", "TextSecondaryBrush", "DividerBrush" })
                    Resources.Remove(key);
                break;
        }
        HeadingText.Text = cfg.PerformanceOverlayLockPosition ? "性能 · 位置已锁定 · 右键设置" : "性能 · 拖动移动 · 右键设置";
        if (_snapshot != null) Render(_snapshot);
        if (IsLoaded) ResetPosition(useSavedPosition: false);
    }

    public void Render(PerformanceSnapshot snapshot)
    {
        _snapshot = snapshot;
        var cfg = _config();
        var unit = PerformanceOverlaySettings.MemoryUnit(cfg);
        var detailed = PerformanceOverlaySettings.Detailed(cfg);
        var systemParts = new List<string>();
        if (cfg.PerformanceOverlayShowSystemCpu)
            systemParts.Add("系统 CPU " + PerformanceOverlaySettings.FormatCpu(snapshot.SystemCpuPercent));
        if (cfg.PerformanceOverlayShowSystemMemory)
        {
            var percent = PerformanceMetricsMath.MemoryUsedPercent(snapshot.UsedMemoryBytes, snapshot.TotalMemoryBytes);
            systemParts.Add("内存 " + PerformanceOverlaySettings.FormatMemory(snapshot.UsedMemoryBytes, unit) +
                " / " + PerformanceOverlaySettings.FormatMemory(snapshot.TotalMemoryBytes, unit) +
                (cfg.PerformanceOverlayShowMemoryPercent && percent.HasValue ? $"（{percent.Value:0.0}%）" : ""));
            if (detailed) systemParts.Add("可用 " + PerformanceOverlaySettings.FormatMemory(snapshot.AvailableMemoryBytes, unit));
        }
        SystemText.Text = string.Join(detailed ? "\n" : "  ·  ", systemParts);
        SystemText.Visibility = systemParts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SystemText.ToolTip = string.Join("\n", new[] { snapshot.SystemCpuUnavailableReason, snapshot.MemoryUnavailableReason }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        LauncherText.Visibility = cfg.PerformanceOverlayShowLauncher ? Visibility.Visible : Visibility.Collapsed;
        LauncherText.Text = "启动器  CPU " + PerformanceOverlaySettings.FormatCpu(snapshot.LauncherCpuPercent) +
            "  ·  " + PerformanceOverlaySettings.FormatMemory(snapshot.LauncherWorkingSetBytes, unit);
        LauncherText.ToolTip = string.Join("\n", new[] { snapshot.LauncherCpuUnavailableReason, snapshot.LauncherMemoryUnavailableReason }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

        GamesPanel.Children.Clear();
        var games = PerformanceOverlaySettings.SelectGames(snapshot, cfg);
        GamesPanel.Visibility = cfg.PerformanceOverlayShowGames ? Visibility.Visible : Visibility.Collapsed;
        GameTotalsText.Visibility = cfg.PerformanceOverlayShowGames && cfg.PerformanceOverlayShowGameTotals ? Visibility.Visible : Visibility.Collapsed;
        if (GameTotalsText.Visibility == Visibility.Visible)
        {
            var totals = PerformanceMetricsMath.Summarize(games);
            var values = new List<string> { $"游戏合计 · {totals.GameCount} 个进程" };
            if (cfg.PerformanceOverlayShowGameCpu) values.Add("CPU " + PerformanceOverlaySettings.FormatCpu(totals.CpuPercent));
            if (cfg.PerformanceOverlayShowGameMemory) values.Add(PerformanceOverlaySettings.FormatMemory(totals.WorkingSetBytes, unit));
            GameTotalsText.Text = string.Join("  ·  ", values);
            GameTotalsText.ToolTip = string.Join("\n", new[] { totals.CpuUnavailableReason, totals.MemoryUnavailableReason }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        if (games.Count == 0)
        {
            AddGameText(cfg.PerformanceOverlayAllInstances ? "暂无运行中的游戏实例" :
                "所选实例未运行：" + (string.IsNullOrWhiteSpace(cfg.PerformanceOverlayInstanceName) ? "请右键选择实例" : cfg.PerformanceOverlayInstanceName), secondary: true);
        }
        foreach (var game in games)
        {
            var name = game.DisplayName + (cfg.PerformanceOverlayShowProcessId ? $"  [PID {game.ProcessId}]" : "");
            var values = new List<string>();
            if (cfg.PerformanceOverlayShowGameCpu) values.Add("CPU " + PerformanceOverlaySettings.FormatCpu(game.CpuPercent));
            if (cfg.PerformanceOverlayShowGameMemory) values.Add(PerformanceOverlaySettings.FormatMemory(game.WorkingSetBytes, unit));
            var text = AddGameText(name + (values.Count > 0 ? (detailed ? "\n" : "  ") + string.Join("  ·  ", values) : ""));
            text.ToolTip = game.Edition + $" · PID {game.ProcessId}\n" + string.Join("\n",
                new[] { game.CpuUnavailableReason, game.MemoryUnavailableReason }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshot.DiscoveryWarning)) warnings.Add(snapshot.DiscoveryWarning);
        if (detailed) warnings.Add($"{snapshot.LogicalProcessorCount} 个逻辑处理器 · {snapshot.SampledAt.LocalDateTime:HH:mm:ss} 更新\nCPU 按整机 0–100% 计；游戏内存为进程工作集，非 JVM 堆上限。");
        StatusText.Text = string.Join("\n", warnings);
        StatusText.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowSamplingError()
    {
        _snapshot = null;
        // 清除旧读数，不能把上次成功数据继续显示为当前占用。
        SystemText.Text = "系统数据暂不可用";
        LauncherText.Text = "启动器数据暂不可用";
        GamesPanel.Children.Clear();
        GameTotalsText.Visibility = Visibility.Collapsed;
        AddGameText("游戏数据暂不可用", secondary: true);
        StatusText.Text = "读取已暂停，稍后将重试。";
        StatusText.Visibility = Visibility.Visible;
    }

    private TextBlock AddGameText(string text, bool secondary = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
        block.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "TextSecondaryBrush" : "TextPrimaryBrush");
        GamesPanel.Children.Add(block);
        return block;
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_config().PerformanceOverlayLockPosition || e.ChangedButton != MouseButton.Left) return;
        // 长列表滚动条的拖动不能被外层窗口拖动抢走。
        for (var element = e.OriginalSource as DependencyObject; element != null && !ReferenceEquals(element, OverlayCard);)
        {
            if (element is ScrollBar or ButtonBase) return;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        try
        {
            DragMove();
            ResetPosition(useSavedPosition: false);
            _savePosition(Left, Top);
        }
        catch (InvalidOperationException) { /* 鼠标已松开，不改变位置。 */ }
        e.Handled = true;
    }

    public void ResetPosition(bool useSavedPosition)
    {
        var cfg = _config();
        var handle = new WindowInteropHelper(this).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var start = transform.Transform(new Point(area.Left, area.Top));
        var end = transform.Transform(new Point(area.Right, area.Bottom));
        MaxWidth = Math.Max(1, end.X - start.X - 16);
        var visibleWidth = Math.Min(PerformanceOverlaySettings.WindowWidth(cfg), MaxWidth);
        MaxHeight = Math.Max(54, Math.Min(540, end.Y - start.Y - 16));
        var left = useSavedPosition ? cfg.PerformanceOverlayLeft ?? (end.X - visibleWidth - 18) : Left;
        var top = useSavedPosition ? cfg.PerformanceOverlayTop ?? (start.Y + 24) : Top;
        Left = Math.Clamp(double.IsFinite(left) ? left : start.X + 18, start.X, Math.Max(start.X, end.X - visibleWidth));
        Top = Math.Clamp(double.IsFinite(top) ? top : start.Y + 24, start.Y, Math.Max(start.Y, end.Y - Math.Max(54, ActualHeight)));
    }

    private void Overlay_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var cfg = _config();
        var menu = OverlayCard.ContextMenu;
        menu.Items.Clear();
        var style = new MenuItem { Header = "显示样式" };
        AddOption(style, "简洁", !PerformanceOverlaySettings.Detailed(cfg), () => Change(c => c.PerformanceOverlayStyle = "Compact"));
        AddOption(style, "详细", PerformanceOverlaySettings.Detailed(cfg), () => Change(c => c.PerformanceOverlayStyle = "Detailed"));
        menu.Items.Add(style);
        var appearance = new MenuItem { Header = "外观" };
        var colors = new MenuItem { Header = "配色" };
        foreach (var (value, label) in new[] { ("Theme", "跟随启动器"), ("Dark", "深色"), ("Light", "浅色") })
            AddOption(colors, label, PerformanceOverlaySettings.ColorScheme(cfg) == value, () => Change(c => c.PerformanceOverlayColorScheme = value));
        appearance.Items.Add(colors);
        var font = new MenuItem { Header = "字号" };
        foreach (var size in new[] { 10, 12, 14, 16, 20, 24, 26 })
            AddOption(font, size.ToString(), PerformanceOverlaySettings.FontSize(cfg) == size, () => Change(c => c.PerformanceOverlayFontSize = size));
        appearance.Items.Add(font);
        var opacity = new MenuItem { Header = "不透明度" };
        foreach (var percent in new[] { 100, 90, 75, 50, 25 })
            AddOption(opacity, percent + "%", Math.Abs(PerformanceOverlaySettings.Opacity(cfg) * 100 - percent) < 0.1,
                () => Change(c => c.PerformanceOverlayOpacityPercent = percent));
        appearance.Items.Add(opacity);
        var width = new MenuItem { Header = "窗口宽度" };
        AddOption(width, "随样式自动", cfg.PerformanceOverlayWindowWidth <= 0, () => Change(c => c.PerformanceOverlayWindowWidth = 0));
        foreach (var value in new[] { 280, 320, 420, 520, 600 })
            AddOption(width, value + "", cfg.PerformanceOverlayWindowWidth == value, () => Change(c => c.PerformanceOverlayWindowWidth = value));
        appearance.Items.Add(width);
        menu.Items.Add(appearance);
        var refresh = new MenuItem { Header = "刷新间隔" };
        foreach (var (value, label) in new[] { (500, "0.5 秒"), (1000, "1 秒"), (2000, "2 秒"), (5000, "5 秒"), (10000, "10 秒") })
            AddOption(refresh, label, PerformanceOverlaySettings.RefreshMilliseconds(cfg) == value,
                () => Change(c => c.PerformanceOverlayRefreshMilliseconds = value));
        menu.Items.Add(refresh);
        var units = new MenuItem { Header = "内存单位" };
        foreach (var (value, label) in new[] { ("Auto", "自动"), ("MB", "MB"), ("GB", "GB") })
            AddOption(units, label, PerformanceOverlaySettings.MemoryUnit(cfg) == value, () => Change(c => c.PerformanceOverlayMemoryUnit = value));
        menu.Items.Add(units);
        var scope = new MenuItem { Header = "显示游戏" };
        AddOption(scope, "所有运行中的游戏", cfg.PerformanceOverlayAllInstances, () => Change(c => c.PerformanceOverlayAllInstances = true));
        foreach (var group in (_snapshot?.Games ?? Array.Empty<PerformanceGameSample>()).GroupBy(game => game.InstanceId))
        {
            var first = group.First();
            var label = first.DisplayName + $" · PID {first.ProcessId}" + (group.Count() > 1 ? $" 等 {group.Count()} 个进程" : "");
            AddOption(scope, label, !cfg.PerformanceOverlayAllInstances && cfg.PerformanceOverlayInstanceId == first.InstanceId,
                () => Change(c => { c.PerformanceOverlayAllInstances = false; c.PerformanceOverlayInstanceId = first.InstanceId; c.PerformanceOverlayInstanceName = first.DisplayName; }));
        }
        if (_snapshot?.Games.Count is null or 0) scope.Items.Add(new MenuItem { Header = "暂无可选择的运行实例", IsEnabled = false });
        menu.Items.Add(scope);
        var sort = new MenuItem { Header = "游戏排序" };
        foreach (var (value, label) in new[] { ("Name", "按实例名称"), ("Cpu", "CPU 从高到低"), ("Memory", "内存从高到低") })
            AddOption(sort, label, PerformanceOverlaySettings.GameSort(cfg) == value, () => Change(c => c.PerformanceOverlayGameSort = value));
        menu.Items.Add(sort);
        var items = new MenuItem { Header = "显示项目" };
        AddOption(items, "系统 CPU", cfg.PerformanceOverlayShowSystemCpu, () => Change(c => c.PerformanceOverlayShowSystemCpu = !c.PerformanceOverlayShowSystemCpu));
        AddOption(items, "系统内存", cfg.PerformanceOverlayShowSystemMemory, () => Change(c => c.PerformanceOverlayShowSystemMemory = !c.PerformanceOverlayShowSystemMemory));
        AddOption(items, "内存使用百分比", cfg.PerformanceOverlayShowMemoryPercent, () => Change(c => c.PerformanceOverlayShowMemoryPercent = !c.PerformanceOverlayShowMemoryPercent));
        AddOption(items, "启动器 CPU / 内存", cfg.PerformanceOverlayShowLauncher, () => Change(c => c.PerformanceOverlayShowLauncher = !c.PerformanceOverlayShowLauncher));
        AddOption(items, "游戏列表", cfg.PerformanceOverlayShowGames, () => Change(c => c.PerformanceOverlayShowGames = !c.PerformanceOverlayShowGames));
        AddOption(items, "所选范围的游戏合计", cfg.PerformanceOverlayShowGameTotals, () => Change(c => c.PerformanceOverlayShowGameTotals = !c.PerformanceOverlayShowGameTotals));
        AddOption(items, "游戏 CPU", cfg.PerformanceOverlayShowGameCpu, () => Change(c => c.PerformanceOverlayShowGameCpu = !c.PerformanceOverlayShowGameCpu));
        AddOption(items, "游戏内存", cfg.PerformanceOverlayShowGameMemory, () => Change(c => c.PerformanceOverlayShowGameMemory = !c.PerformanceOverlayShowGameMemory));
        AddOption(items, "进程 PID", cfg.PerformanceOverlayShowProcessId, () => Change(c => c.PerformanceOverlayShowProcessId = !c.PerformanceOverlayShowProcessId));
        menu.Items.Add(items);
        menu.Items.Add(new Separator());
        AddOption(menu, "固定在最上层", cfg.PerformanceOverlayAlwaysOnTop, () => Change(c => c.PerformanceOverlayAlwaysOnTop = !c.PerformanceOverlayAlwaysOnTop));
        AddOption(menu, "锁定位置", cfg.PerformanceOverlayLockPosition, () => Change(c => c.PerformanceOverlayLockPosition = !c.PerformanceOverlayLockPosition));
        AddAction(menu, "重置位置", () => { Change(c => { c.PerformanceOverlayLeft = null; c.PerformanceOverlayTop = null; }); ResetPosition(true); });
        AddAction(menu, "详细设置…", _openSettings);
        AddAction(menu, "关闭性能悬浮窗", () => Change(c => c.PerformanceOverlayEnabled = false));
    }

    private void Change(Action<AppConfig> update) => _changeSettings(update);
    private static void AddOption(ItemsControl menu, string label, bool selected, Action action)
    {
        var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = selected };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
    private static void AddAction(ItemsControl menu, string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}
