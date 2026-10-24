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

/// <summary>默认可拖动和右键设置；开启鼠标穿透时提供小齿轮。不安装全局键鼠钩子。</summary>
public partial class PerformanceOverlayWindow : Window, IGlobalWindowTreatmentExempt
{
    private readonly Func<AppConfig> _config;
    private readonly Action<Action<AppConfig>> _changeSettings;
    private readonly Action _openSettings;
    private readonly Action<double, double> _savePosition;
    private PerformanceSnapshot? _snapshot;
    private AppConfig? _hardwarePreview;
    private HwndSource? _source;
    private bool _clickThrough;
    private bool _closed;
    private bool _movingFromHandle;
    private bool _resettingPosition;
    private bool _removingSettingsHandle;
    private PerformanceOverlayHandleWindow? _settingsHandle;

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
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WindowHook);
            ApplyMouseInputPolicy(_hardwarePreview ?? _config());
        };
        Loaded += (_, _) =>
        {
            // Reapply after WPF has installed its final HWND styles, including the whole header.
            ApplyMouseInputPolicy(_hardwarePreview ?? _config());
            ResetPosition(useSavedPosition: true);
            UpdateSettingsHandle();
        };
        SizeChanged += (_, _) => { if (IsLoaded) ResetPosition(useSavedPosition: false); };
        LocationChanged += (_, _) => PositionSettingsHandle();
        IsVisibleChanged += (_, _) => UpdateSettingsHandle();
        Closing += (_, _) => _closed = true;
        Closed += (_, _) =>
        {
            _closed = true;
            OverlayCard.ContextMenu.IsOpen = false;
            CloseSettingsHandle();
            _source?.RemoveHook(WindowHook);
            _source = null;
        };
        ApplySettings();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private void ApplyMouseInputPolicy(AppConfig cfg)
    {
        _clickThrough = cfg.PerformanceOverlayClickThrough;
        OverlayCard.IsHitTestVisible = !_clickThrough;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        // AllowsTransparency supplies WS_EX_LAYERED. With WS_EX_TRANSPARENT it passes input
        // to windows in other processes too; HTTRANSPARENT alone only covers the same thread.
        const int transparent = 0x00000020, noActivate = 0x08000000, toolWindow = 0x00000080;
        var style = GetWindowLong(handle, -20) | noActivate | toolWindow;
        style = _clickThrough ? style | transparent : style & ~transparent;
        SetWindowLong(handle, -20, style);
        // Refresh the style without changing placement, topmost state or keyboard focus.
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x007C && unchecked((int)wParam.ToInt64()) == -20 && lParam != IntPtr.Zero)
        {
            // Keep the input policy when WPF rewrites styles after visibility/topmost changes.
            var change = Marshal.PtrToStructure<NativeStyleChange>(lParam);
            change.NewStyle |= 0x00080000 | 0x08000000 | 0x00000080; // layered, noactivate, toolwindow
            change.NewStyle = _clickThrough ? change.NewStyle | 0x20 : change.NewStyle & ~0x20;
            Marshal.StructureToPtr(change, lParam, false);
        }
        if (message == 0x02E0 || message == 0x007E) // DPI / monitor configuration changed
            Dispatcher.BeginInvoke(new Action(() => { if (!_closed && IsLoaded) ResetPosition(false); }));
        if (_clickThrough && message == 0x0084) // WM_NCHITTEST / HTTRANSPARENT
        {
            handled = true;
            return new IntPtr(-1);
        }
        if (message == 0x0021) // WM_MOUSEACTIVATE / MA_NOACTIVATE：交互不会抢走游戏焦点。
        {
            handled = true;
            return new IntPtr(3);
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStyleChange { public int OldStyle; public int NewStyle; }

    private void UpdateSettingsHandle()
    {
        if (_closed || !IsLoaded) return;
        var cfg = _hardwarePreview ?? _config();
        if (!cfg.PerformanceOverlayClickThrough)
        {
            CloseSettingsHandle();
            return;
        }
        if (!IsVisible)
        {
            OverlayCard.ContextMenu.IsOpen = false;
            _settingsHandle?.Hide();
            return;
        }
        if (_settingsHandle == null)
        {
            _settingsHandle = new PerformanceOverlayHandleWindow(this,
            target => OpenSettingsMenu(target),
            () => !(_hardwarePreview ?? _config()).PerformanceOverlayLockPosition,
            moving =>
            {
                _movingFromHandle = moving;
                if (!moving && !_closed && !_removingSettingsHandle && IsVisible)
                {
                    ResetPosition(false);
                    _savePosition(Left, Top);
                }
            });
            var created = _settingsHandle;
            created.Closed += (_, _) => { if (ReferenceEquals(_settingsHandle, created)) _settingsHandle = null; };
        }
        _settingsHandle.ApplyAppearance(cfg.PerformanceOverlayAlwaysOnTop,
            cfg.PerformanceOverlayLockPosition, cfg.PerformanceOverlayClickThrough,
            (Brush)FindResource("PanelBrush"), (Brush)FindResource("TextPrimaryBrush"),
            (Brush)FindResource("DividerBrush"));
        if (!_settingsHandle.IsVisible)
        {
            // Put the HWND in place before showing, so it never flashes at the desktop origin.
            new WindowInteropHelper(_settingsHandle).EnsureHandle();
            PositionSettingsHandle();
            _settingsHandle.Show();
        }
        PositionSettingsHandle();
    }

    private void CloseSettingsHandle()
    {
        var handle = _settingsHandle;
        if (handle == null) return;
        // Detach first: releasing drag capture must not resave settings and recreate an island.
        _settingsHandle = null;
        _removingSettingsHandle = true;
        try
        {
            OverlayCard.ContextMenu.IsOpen = false;
            OverlayCard.ContextMenu.PlacementTarget = OverlayCard;
            handle.Close();
        }
        finally
        {
            _movingFromHandle = false;
            _removingSettingsHandle = false;
        }
    }

    private void PositionSettingsHandle()
    {
        if (_closed || !_clickThrough || _settingsHandle == null || !IsLoaded || !IsVisible) return;
        var point = PointToScreen(new Point(Math.Max(0, ActualWidth - PerformanceOverlayHandleWindow.HandleSize - 6), 6));
        _settingsHandle.PositionAt(point);
    }

    private void OpenSettingsMenu(FrameworkElement target)
    {
        if (_closed || !_clickThrough || _settingsHandle == null) return;
        var menu = BuildContextMenu();
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    public void ApplySettings()
    {
        var cfg = _hardwarePreview ?? _config();
        FontSize = PerformanceOverlaySettings.FontSize(cfg);
        Opacity = PerformanceOverlaySettings.Opacity(cfg);
        Topmost = cfg.PerformanceOverlayAlwaysOnTop;
        Width = PerformanceOverlaySettings.WindowWidth(cfg);
        ApplyMouseInputPolicy(cfg);
        OverlayScroll.Margin = new Thickness(0, 0, cfg.PerformanceOverlayClickThrough ? PerformanceOverlayHandleWindow.HandleSize : 0, 0);
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
        ApplyTextWrapping(cfg);
        OverlayCard.ToolTip = cfg.PerformanceOverlayClickThrough
            ? "鼠标穿透已开启；点击右上角齿轮可配置，拖动齿轮可移动窗口。其余区域的点击均交给后方窗口。"
            : cfg.PerformanceOverlayLockPosition
                ? "位置已锁定。右键可解锁、选择监测项目、调整颜色，或编辑带有自定义文字的字段模板。"
                : "按住左键拖动位置。右键可选择监测项目、调整颜色、锁定位置，或编辑带有自定义文字的字段模板。";
        ToolTipService.SetShowDuration(OverlayCard, 12000);
        if (_snapshot != null) Render(_snapshot);
        else
        {
            SystemText.Visibility = HardwareText.Visibility = LauncherText.Visibility = GameTotalsText.Visibility = GamesPanel.Visibility = Visibility.Collapsed;
            var result = PerformanceOverlayTemplateFormatter.Format(null, cfg);
            LayoutText.Visibility = Visibility.Visible;
            LayoutText.Text = string.IsNullOrWhiteSpace(result.Text) ? "未选择显示内容" : result.Text;
            LayoutText.ToolTip = result.ToolTip;
            StatusText.Text = result.Warning ?? "等待性能采样…";
            StatusText.Visibility = Visibility.Visible;
        }
        if (IsLoaded) ResetPosition(useSavedPosition: false);
        UpdateSettingsHandle();
    }

    public void Render(PerformanceSnapshot snapshot)
    {
        _snapshot = snapshot;
        var cfg = _hardwarePreview ?? _config();
        var unit = PerformanceOverlaySettings.MemoryUnit(cfg);
        var detailed = PerformanceOverlaySettings.Detailed(cfg);
        ApplyTextWrapping(cfg);
        if (PerformanceOverlayTemplateFormatter.UsesCustomText(cfg))
        {
            SystemText.Visibility = HardwareText.Visibility = LauncherText.Visibility = GameTotalsText.Visibility = GamesPanel.Visibility = Visibility.Collapsed;
            LayoutText.Visibility = Visibility.Visible;
            var result = PerformanceOverlayTemplateFormatter.Format(snapshot, cfg);
            LayoutText.Text = string.IsNullOrWhiteSpace(result.Text) ? "未选择显示内容" : result.Text;
            LayoutText.ToolTip = result.ToolTip;
            var notices = new[] { result.Warning, snapshot.DiscoveryWarning }.Where(value => !string.IsNullOrWhiteSpace(value));
            StatusText.Text = string.Join("\n", notices);
            StatusText.Visibility = StatusText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        LayoutText.Visibility = Visibility.Collapsed;
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
        RenderHardware(snapshot.Hardware, cfg, detailed);
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

    public void PreviewHardwareSettings(AppConfig? draft)
    {
        _hardwarePreview = draft;
        ApplySettings();
    }

    private void ApplyTextWrapping(AppConfig cfg)
    {
        var wrap = PerformanceOverlayTemplateFormatter.AutoWrap(cfg) ? TextWrapping.Wrap : TextWrapping.NoWrap;
        OverlayScroll.HorizontalScrollBarVisibility = wrap == TextWrapping.NoWrap ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        foreach (var text in new[] { LayoutText, SystemText, HardwareText, LauncherText, GameTotalsText, StatusText }) text.TextWrapping = wrap;
        foreach (var text in GamesPanel.Children.OfType<TextBlock>()) text.TextWrapping = wrap;
    }

    private void RenderHardware(PerformanceHardwareSnapshot? hardware, AppConfig cfg, bool detailed)
    {
        var lines = new List<string>();
        var reasons = new List<string>();
        foreach (var kind in new[] { "GPU", "NPU" })
        {
            var display = PerformanceOverlaySettings.HardwareDisplay(cfg, kind);
            if (!display.Show || (!display.ShowUtilization && !display.ShowDedicatedMemory && !display.ShowSharedMemory)) continue;
            var devices = hardware?.Devices.Where(item => item.Kind == kind).ToArray() ?? Array.Empty<PerformanceDeviceSample>();
            if (devices.Length == 0)
            {
                lines.Add(kind + " 暂不可用");
                reasons.Add(kind + "：" + ((kind == "GPU" ? hardware?.GpuUnavailableReason : hardware?.NpuUnavailableReason) ?? "尚未获得驱动提供的有效采样。"));
            }
            foreach (var device in devices)
            {
                var label = detailed ? kind + " · " + device.DisplayName : kind + (devices.Length > 1 ? " · " + device.DisplayName : "");
                lines.Add(label + (display.ShowUtilization ? "  " + PerformanceOverlaySettings.FormatHardwareUtilization(device.UtilizationPercent, display.UtilizationFormat) : ""));
                var memory = new List<string>();
                if (display.ShowDedicatedMemory) memory.Add((kind == "GPU" ? "专用显存 " : "专用内存 ") + PerformanceOverlaySettings.FormatHardwareMemory(device.DedicatedMemoryBytes, display.DedicatedMemoryUnit));
                if (display.ShowSharedMemory) memory.Add("共享内存 " + PerformanceOverlaySettings.FormatHardwareMemory(device.SharedMemoryBytes, display.SharedMemoryUnit));
                if (memory.Count > 0) lines.Add("  " + string.Join(" · ", memory));
                if (!string.IsNullOrWhiteSpace(device.UnavailableReason)) reasons.Add(device.DisplayName + "：" + device.UnavailableReason);
            }
        }
        if (cfg.PerformanceOverlayShowDisk) lines.Add("磁盘 读 " + Rate(hardware?.DiskReadBytesPerSecond) + " · 写 " + Rate(hardware?.DiskWriteBytesPerSecond));
        if (cfg.PerformanceOverlayShowNetwork) lines.Add("网卡合计 ↓ " + Rate(hardware?.NetworkReceivedBytesPerSecond) + " · ↑ " + Rate(hardware?.NetworkSentBytesPerSecond));
        if (!string.IsNullOrWhiteSpace(hardware?.IoUnavailableReason)) reasons.Add(hardware.IoUnavailableReason);
        reasons.Add("设备使用率为最繁忙引擎；本地内存由驱动报告，集成显卡/NPU 不一定拥有独立显存。网卡合计可能包含虚拟网卡。");
        var details = PerformanceHardwareDetails.Format(hardware, cfg);
        if (!string.IsNullOrWhiteSpace(details)) lines.Add(details);
        HardwareText.Text = string.Join("\n", lines);
        HardwareText.ToolTip = string.Join("\n", reasons);
        HardwareText.Visibility = lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Rate(double? bytes)
    {
        if (bytes is not { } value || !double.IsFinite(value) || value < 0) return "暂不可用";
        return value >= 1048576 ? $"{value / 1048576:0.0} MB/s" : $"{value / 1024:0.0} KB/s";
    }

    public void ShowSamplingError()
    {
        _snapshot = null;
        // 清除旧读数，不能把上次成功数据继续显示为当前占用。
        LayoutText.Text = "性能数据暂不可用";
        SystemText.Text = "系统数据暂不可用";
        HardwareText.Text = "硬件数据暂不可用";
        LauncherText.Text = "启动器数据暂不可用";
        GamesPanel.Children.Clear();
        GameTotalsText.Visibility = Visibility.Collapsed;
        AddGameText("游戏数据暂不可用", secondary: true);
        StatusText.Text = "读取已暂停，稍后将重试。";
        StatusText.Visibility = Visibility.Visible;
    }

    private TextBlock AddGameText(string text, bool secondary = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = PerformanceOverlayTemplateFormatter.AutoWrap(_hardwarePreview ?? _config()) ? TextWrapping.Wrap : TextWrapping.NoWrap, Margin = new Thickness(0, 3, 0, 3) };
        block.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "TextSecondaryBrush" : "TextPrimaryBrush");
        GamesPanel.Children.Add(block);
        return block;
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_clickThrough || (_hardwarePreview ?? _config()).PerformanceOverlayLockPosition || e.ChangedButton != MouseButton.Left) return;
        // 长列表滚动条的拖动不能被外层窗口拖动抢走。
        for (var element = e.OriginalSource as DependencyObject; element != null && !ReferenceEquals(element, OverlayCard);)
        {
            if (element is ScrollBar or ButtonBase) return;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        try
        {
            DragMove();
            if (!_closed && IsVisible)
            {
                ResetPosition(useSavedPosition: false);
                _savePosition(Left, Top);
            }
        }
        catch (InvalidOperationException) { /* 鼠标已松开，不改变位置。 */ }
        e.Handled = true;
    }

    public void ResetPosition(bool useSavedPosition)
    {
        if (_closed || _movingFromHandle || _resettingPosition) return;
        var cfg = _hardwarePreview ?? _config();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        _resettingPosition = true;
        try
        {
            // Let WPF restore its saved DIP coordinates first, so Windows selects the saved
            // monitor before clamping. Subsequent placement uses physical pixels throughout.
            var hasSavedPosition = cfg.PerformanceOverlayLeft is double savedLeft && double.IsFinite(savedLeft)
                && cfg.PerformanceOverlayTop is double savedTop && double.IsFinite(savedTop);
            if (useSavedPosition && hasSavedPosition)
            {
                Left = cfg.PerformanceOverlayLeft!.Value;
                Top = cfg.PerformanceOverlayTop!.Value;
            }
            var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            MaxWidth = Math.Max(1, area.Width / dpi.DpiScaleX - 16);
            var visibleWidth = Math.Min(PerformanceOverlaySettings.WindowWidth(cfg), MaxWidth);
            MaxHeight = Math.Max(54, Math.Min(540, area.Height / dpi.DpiScaleY - 16));
            if (!GetWindowRect(handle, out var rect)) return;
            var width = (int)Math.Ceiling(visibleWidth * dpi.DpiScaleX);
            var height = Math.Min(rect.Bottom - rect.Top, (int)Math.Ceiling(MaxHeight * dpi.DpiScaleY));
            var left = useSavedPosition && !hasSavedPosition ? area.Right - width - (int)(18 * dpi.DpiScaleX) : rect.Left;
            var top = useSavedPosition && !hasSavedPosition ? area.Top + (int)(24 * dpi.DpiScaleY) : rect.Top;
            left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - width));
            top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - height));
            if (left != rect.Left || top != rect.Top)
                SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, 0x0001 | 0x0004 | 0x0010);
            PositionSettingsHandle();
        }
        finally { _resettingPosition = false; }
    }

    private void Overlay_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Right-click and the passthrough settings handle use one menu and one settings path.
        var menu = BuildContextMenu();
        menu.PlacementTarget = OverlayCard;
        menu.Placement = PlacementMode.MousePoint;
    }

    private ContextMenu BuildContextMenu()
    {
        var cfg = _hardwarePreview ?? _config();
        var menu = OverlayCard.ContextMenu;
        menu.Items.Clear();
        var style = new MenuItem { Header = "显示样式" };
        AddOption(style, "简洁", !PerformanceOverlaySettings.Detailed(cfg), () => Change(c => c.PerformanceOverlayStyle = "Compact"));
        AddOption(style, "详细", PerformanceOverlaySettings.Detailed(cfg), () => Change(c => c.PerformanceOverlayStyle = "Detailed"));
        menu.Items.Add(style);
        var layout = new MenuItem { Header = "布局与换行", ToolTip = "字段模板可混合自己的文字与实时数据；详细设置提供字段说明与一键插入。" };
        AddOption(layout, "保留现有布局", PerformanceOverlayTemplateFormatter.LayoutMode(cfg) == "Default", () => Change(c => c.PerformanceOverlayLayoutMode = "Default"));
        AddOption(layout, "使用字段模板", PerformanceOverlayTemplateFormatter.LayoutMode(cfg) == "Template", () => Change(c => c.PerformanceOverlayLayoutMode = "Template"));
        layout.Items.Add(new Separator());
        foreach (var (value, label) in new[] { ("Preserve", "全部保留换行"), ("SingleLine", "全部取消换行"), ("EachField", "每项独立一行") })
            AddOption(layout, label, PerformanceOverlayTemplateFormatter.LineBreakMode(cfg) == value, () => Change(c => c.PerformanceOverlayLineBreakMode = value));
        AddOption(layout, "超出宽度自动换行", cfg.PerformanceOverlayAutoWrap, () => Change(c => c.PerformanceOverlayAutoWrap = !c.PerformanceOverlayAutoWrap));
        ((MenuItem)layout.Items[layout.Items.Count - 1]).IsEnabled = PerformanceOverlayTemplateFormatter.LineBreakMode(cfg) != "SingleLine";
        AddAction(layout, "编辑模板…", _openSettings, "打开性能设置，编辑自定义文字、查看字段帮助并一键插入数据字段。");
        menu.Items.Add(layout);
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
        var refresh = new MenuItem { Header = "刷新间隔", ToolTip = "较短间隔更及时，较长间隔可减少后台采样开销。" };
        foreach (var (value, label) in new[] { (500, "0.5 秒"), (1000, "1 秒"), (2000, "2 秒"), (5000, "5 秒"), (10000, "10 秒") })
            AddOption(refresh, label, PerformanceOverlaySettings.RefreshMilliseconds(cfg) == value,
                () => Change(c => c.PerformanceOverlayRefreshMilliseconds = value));
        menu.Items.Add(refresh);
        var units = new MenuItem { Header = "系统 / 进程内存单位" };
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
        AddHardwareMenu(menu, cfg, "GPU");
        AddHardwareMenu(menu, cfg, "NPU");
        AddOption(items, "磁盘读写速率", cfg.PerformanceOverlayShowDisk, () => Change(c => c.PerformanceOverlayShowDisk = !c.PerformanceOverlayShowDisk));
        AddOption(items, "网卡收发速率", cfg.PerformanceOverlayShowNetwork, () => Change(c => c.PerformanceOverlayShowNetwork = !c.PerformanceOverlayShowNetwork));
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
        AddOption(menu, "固定在最上层", cfg.PerformanceOverlayAlwaysOnTop, () => Change(c => c.PerformanceOverlayAlwaysOnTop = !c.PerformanceOverlayAlwaysOnTop), "让性能窗口显示在其他普通应用窗口上方。");
        AddOption(menu, "鼠标穿透", cfg.PerformanceOverlayClickThrough, () => Change(c => c.PerformanceOverlayClickThrough = !c.PerformanceOverlayClickThrough), "默认关闭，可直接拖动主体或右键设置。开启后显示小齿轮：点击设置、按住拖动，其余区域全部穿透。");
        AddOption(menu, "锁定位置", cfg.PerformanceOverlayLockPosition, () => Change(c => c.PerformanceOverlayLockPosition = !c.PerformanceOverlayLockPosition), "防止误拖动；不影响右键菜单和数据显示。");
        AddAction(menu, "重置位置", () => { Change(c => { c.PerformanceOverlayLeft = null; c.PerformanceOverlayTop = null; }); ResetPosition(true); }, "将性能窗口移回当前显示器的右上角。");
        AddAction(menu, "硬件厂商、速率与内存通道…", () =>
        {
            var dialog = new PerformanceHardwareDetailsDialog(_config(), _changeSettings);
            if (Application.Current?.MainWindow is { IsVisible: true } main) dialog.Owner = main;
            dialog.ShowDialog();
        }, "配置硬件厂商、默认与标准速率、当前速度、内存通道等显示信息。");
        AddAction(menu, "详细设置…", _openSettings, "设置字段模板、显示数据、透明度、刷新频率和监测范围。");
        AddAction(menu, "关闭性能悬浮窗", () => Change(c => c.PerformanceOverlayEnabled = false), "关闭显示并停止采样；可右键启动器的设置按钮重新开启。");
        return menu;
    }

    private void AddHardwareMenu(ItemsControl menu, AppConfig cfg, string kind)
    {
        var gpu = kind == "GPU";
        var display = PerformanceOverlaySettings.HardwareDisplay(cfg, kind);
        var hardware = new MenuItem { Header = kind + " 显示设置" };
        AddOption(hardware, "显示 " + kind, display.Show, () => Change(c => { if (gpu) c.PerformanceOverlayShowGpu = !c.PerformanceOverlayShowGpu; else c.PerformanceOverlayShowNpu = !c.PerformanceOverlayShowNpu; }));
        AddOption(hardware, "显示使用率", display.ShowUtilization, () => Change(c => { if (gpu) c.PerformanceOverlayGpuShowUtilization = !c.PerformanceOverlayGpuShowUtilization; else c.PerformanceOverlayNpuShowUtilization = !c.PerformanceOverlayNpuShowUtilization; }));
        var format = new MenuItem { Header = "使用率格式" };
        foreach (var (value, label) in new[] { ("Percent", "百分比 · 0.0%"), ("PercentInteger", "整数百分比 · 0%"), ("Ratio", "比例 · 0–1") })
            AddOption(format, label, display.UtilizationFormat == value, () => Change(c => { if (gpu) c.PerformanceOverlayGpuUtilizationFormat = value; else c.PerformanceOverlayNpuUtilizationFormat = value; }));
        hardware.Items.Add(format);
        AddOption(hardware, gpu ? "显示专用显存" : "显示专用内存", display.ShowDedicatedMemory,
            () => Change(c => { var value = !PerformanceOverlaySettings.HardwareDisplay(c, kind).ShowDedicatedMemory; if (gpu) c.PerformanceOverlayGpuShowDedicatedMemory = value; else c.PerformanceOverlayNpuShowDedicatedMemory = value; }));
        AddHardwareMemoryUnits(hardware, "专用内存单位", display.DedicatedMemoryUnit,
            value => Change(c => { if (gpu) c.PerformanceOverlayGpuDedicatedMemoryUnit = value; else c.PerformanceOverlayNpuDedicatedMemoryUnit = value; }));
        AddOption(hardware, "显示共享内存", display.ShowSharedMemory,
            () => Change(c => { var value = !PerformanceOverlaySettings.HardwareDisplay(c, kind).ShowSharedMemory; if (gpu) c.PerformanceOverlayGpuShowSharedMemory = value; else c.PerformanceOverlayNpuShowSharedMemory = value; }));
        AddHardwareMemoryUnits(hardware, "共享内存单位", display.SharedMemoryUnit,
            value => Change(c => { if (gpu) c.PerformanceOverlayGpuSharedMemoryUnit = value; else c.PerformanceOverlayNpuSharedMemoryUnit = value; }));
        menu.Items.Add(hardware);
    }
    private static void AddHardwareMemoryUnits(ItemsControl menu, string label, string selected, Action<string> change)
    {
        var units = new MenuItem { Header = label };
        foreach (var (value, title) in new[] { ("Auto", "自动"), ("B", "B"), ("KiB", "KiB"), ("MiB", "MiB"), ("GiB", "GiB") })
            AddOption(units, title, selected == value, () => change(value));
        menu.Items.Add(units);
    }

    private void Change(Action<AppConfig> update) => _changeSettings(update);
    private static void AddOption(ItemsControl menu, string label, bool selected, Action action, string? hint = null)
    {
        var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = selected, ToolTip = hint };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
    private static void AddAction(ItemsControl menu, string label, Action action, string? hint = null)
    {
        var item = new MenuItem { Header = label, ToolTip = hint };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}
