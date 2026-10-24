using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private readonly LauncherDebugShortcutState _debugKeys = new();
    private Stopwatch? _debugUptime;
    private DispatcherTimer? _debugTimer;
    private PerformanceMetricsService? _debugMetrics;
    private CancellationTokenSource? _debugSamplingCancellation;
    private bool _debugSampling;
    private bool _debugPanelVisible;
    private bool _debugPaused;
    private LauncherDebugView _debugView;
    private Button? _debugPauseButton;
    private readonly Dictionary<LauncherDebugView, Button> _debugViewButtons = new();
    private Border? _debugPanel;
    private TextBlock? _debugRuntimeText;
    private TextBlock? _debugHelpText;
    private TextBlock? _debugFeedbackText;
    private PerformanceSnapshot? _debugLastMetrics;
    private LauncherDebugSnapshot? _debugLastSnapshot;

    private void InitializeDebugControls()
    {
        if (!App.StartupArgs.DebugMode) return;
        _debugUptime = Stopwatch.StartNew();
        _debugMetrics = new PerformanceMetricsService();
        _debugTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _debugTimer.Tick += DebugTimer_Tick;
        PreviewKeyDown += DebugPreviewKeyDown;
        PreviewKeyUp += DebugPreviewKeyUp;
        Activated += DebugWindow_Activated;
        Deactivated += DebugWindow_Deactivated;
        IsVisibleChanged += DebugWindow_IsVisibleChanged;
        AppBodyGrid.SizeChanged += DebugBody_SizeChanged;
        Closed += DebugWindow_Closed;
    }

    private bool CanUseDebugShortcuts()
    {
        if (!App.StartupArgs.DebugMode || !IsActive || !IsVisible || _isWindowClosed || _windowCloseCommitted ||
            OverlayDialogService.HasActiveOverlay) return false;
        DependencyObject? focused = Keyboard.FocusedElement as DependencyObject;
        while (focused != null)
        {
            if (focused is TextBoxBase or PasswordBox or HwndHost || focused is ComboBox { IsEditable: true }) return false;
            focused = focused is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(focused) : LogicalTreeHelper.GetParent(focused);
        }
        return true;
    }

    private static LauncherDebugKey DebugKey(KeyEventArgs e) => (e.Key == Key.System ? e.SystemKey : e.Key) switch
    {
        Key.F3 => LauncherDebugKey.F3, Key.H => LauncherDebugKey.H, Key.L => LauncherDebugKey.L,
        Key.D => LauncherDebugKey.D, Key.P => LauncherDebugKey.P, Key.C => LauncherDebugKey.C,
        Key.O => LauncherDebugKey.O, Key.S => LauncherDebugKey.S, Key.G => LauncherDebugKey.G,
        Key.R => LauncherDebugKey.R, Key.M => LauncherDebugKey.M,
        Key.Escape => LauncherDebugKey.Escape, _ => LauncherDebugKey.Other
    };

    private void DebugPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var decision = _debugKeys.KeyDown(DebugKey(e), App.StartupArgs.DebugMode, CanUseDebugShortcuts(), e.IsRepeat, Keyboard.Modifiers != ModifierKeys.None);
        if (decision.Handled) e.Handled = true;
        ExecuteDebugAction(decision.Action);
    }
    private void DebugPreviewKeyUp(object sender, KeyEventArgs e)
    {
        var decision = _debugKeys.KeyUp(DebugKey(e), App.StartupArgs.DebugMode, CanUseDebugShortcuts(), Keyboard.Modifiers != ModifierKeys.None);
        if (decision.Handled) e.Handled = true;
        ExecuteDebugAction(decision.Action);
    }

    private void ExecuteDebugAction(LauncherDebugAction action)
    {
        if (!App.StartupArgs.DebugMode || action == LauncherDebugAction.None) return;
        try
        {
            switch (action)
            {
                case LauncherDebugAction.TogglePanel: SetDebugPanelVisible(!_debugPanelVisible); break;
                case LauncherDebugAction.ShowHelp: SetDebugPanelVisible(true, showHelp: true); break;
                case LauncherDebugAction.ClosePanel: SetDebugPanelVisible(false); break;
                case LauncherDebugAction.ShowOverview: SelectDebugView(LauncherDebugView.Overview); break;
                case LauncherDebugAction.ShowSystem: SelectDebugView(LauncherDebugView.System); break;
                case LauncherDebugAction.ShowGames: SelectDebugView(LauncherDebugView.Games); break;
                case LauncherDebugAction.TogglePause: ToggleDebugPause(); break;
                case LauncherDebugAction.OpenPerformanceSettings: OpenPerformanceOverlaySettings(); break;
                case LauncherDebugAction.OpenLogs: DebugNavigate(NavLogsButton); break;
                case LauncherDebugAction.OpenSettings: DebugNavigate(NavSettingsButton); break;
                case LauncherDebugAction.OpenDownloads: DownloadQueueButton_Click(DownloadQueueButton, new RoutedEventArgs()); break;
                case LauncherDebugAction.CopySummary:
                    if (!_debugPaused || _debugLastSnapshot == null) _debugLastSnapshot = CaptureDebugSnapshot();
                    Clipboard.SetText(LauncherDebugSnapshotFormatter.Format(_debugLastSnapshot, _debugView));
                    ToastService.ShowInfo("已复制调试概况（不含账户、目录、启动参数或日志正文）");
                    break;
            }
        }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine("[调试快捷键] " + ex.GetType().Name);
            ToastService.ShowWarning("调试操作未完成，请稍后重试。");
        }
    }

    private static void DebugNavigate(Button button)
    {
        if (!button.IsEnabled) { ToastService.ShowInfo("当前模式下未开放此功能。"); return; }
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private void EnsureDebugPanel()
    {
        if (_debugPanel != null) return;
        var title = DebugText("XCL2 调试信息", 14); title.FontWeight = FontWeights.SemiBold;
        var close = new Button { Content = "×", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(12, 0, 0, 0), ToolTip = "关闭（F3 / F3 + Esc）" };
        close.SetResourceReference(StyleProperty, "SideNavButton");
        close.Click += (_, _) => SetDebugPanelVisible(false);
        var heading = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close); heading.Children.Add(title);
        var tabs = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        foreach (var (view, label, shortcut) in new[]
        {
            (LauncherDebugView.Overview, "总览", "F3 + O"),
            (LauncherDebugView.System, "系统", "F3 + S"),
            (LauncherDebugView.Games, "游戏", "F3 + G")
        })
        {
            var button = DebugTabButton(label, shortcut);
            button.Click += (_, _) => SelectDebugView(view);
            _debugViewButtons[view] = button;
            tabs.Children.Add(button);
        }
        _debugPauseButton = DebugTabButton("暂停", "F3 + R");
        _debugPauseButton.Click += (_, _) => ToggleDebugPause();
        tabs.Children.Add(_debugPauseButton);
        _debugRuntimeText = DebugText("正在采样…");
        _debugRuntimeText.FontFamily = new FontFamily("Consolas");
        _debugHelpText = DebugText(LauncherDebugShortcuts.HelpText);
        _debugHelpText.Margin = new Thickness(0, 14, 0, 0); _debugHelpText.Visibility = Visibility.Collapsed;
        _debugFeedbackText = DebugText("刷新：每秒 · 仅窗口活动时采样", 11);
        _debugFeedbackText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var body = new StackPanel(); body.Children.Add(heading); body.Children.Add(tabs); body.Children.Add(_debugRuntimeText); body.Children.Add(_debugHelpText); body.Children.Add(_debugFeedbackText);
        _debugPanel = new Border
        {
            Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            Margin = new Thickness(12), Padding = new Thickness(14), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed, MaxWidth = 560, MaxHeight = 460
        };
        _debugPanel.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _debugPanel.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        Grid.SetRow(_debugPanel, 1); Grid.SetColumn(_debugPanel, 1);
        // Keep modal scrims above the diagnostic panel; do not replace or reparent the current page.
        var overlayIndex = AppBodyGrid.Children.IndexOf(OverlayRoot);
        AppBodyGrid.Children.Insert(overlayIndex < 0 ? AppBodyGrid.Children.Count : overlayIndex, _debugPanel);
        ResizeDebugPanel();
    }

    private static Button DebugTabButton(string label, string shortcut)
    {
        var button = new Button { Content = label, ToolTip = shortcut, MinWidth = 54,
            Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 4) };
        button.SetResourceReference(StyleProperty, "SideNavButton");
        return button;
    }

    private void SelectDebugView(LauncherDebugView view)
    {
        _debugView = view;
        SetDebugPanelVisible(true);
        RefreshDebugText();
    }

    private void ToggleDebugPause()
    {
        if (!_debugPanelVisible) SetDebugPanelVisible(true);
        _debugLastSnapshot ??= CaptureDebugSnapshot();
        _debugPaused = !_debugPaused;
        UpdateDebugSamplingState();
        RefreshDebugText();
    }

    private static TextBlock DebugText(string text, double size = 12)
    {
        var block = new TextBlock { Text = text, FontSize = size, LineHeight = size + 6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return block;
    }

    private void SetDebugPanelVisible(bool visible, bool showHelp = false)
    {
        if (!App.StartupArgs.DebugMode) return;
        if (visible) EnsureDebugPanel();
        _debugPanelVisible = visible;
        if (_debugPanel != null) _debugPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_debugHelpText != null) _debugHelpText.Visibility = showHelp ? Visibility.Visible : Visibility.Collapsed;
        UpdateDebugSamplingState();
        if (visible) RefreshDebugText();
    }

    private void UpdateDebugSamplingState()
    {
        if (_debugPanelVisible && !_debugPaused && IsActive && IsVisible && WindowState != WindowState.Minimized && !_isWindowClosed)
        {
            if (_debugSamplingCancellation == null)
            {
                _debugSamplingCancellation = new CancellationTokenSource();
                _debugMetrics = new PerformanceMetricsService();
                _debugLastMetrics = null; _debugLastSnapshot = null;
                if (_debugFeedbackText != null) _debugFeedbackText.Text = "正在采样；CPU 首轮需要建立基线。";
            }
            _debugTimer?.Start();
            RefreshDebugText();
            _ = RefreshDebugMetricsAsync();
        }
        else
        {
            _debugTimer?.Stop();
            _debugSamplingCancellation?.Cancel(); _debugSamplingCancellation?.Dispose(); _debugSamplingCancellation = null;
            if (!_debugPaused || _isWindowClosed) { _debugLastMetrics = null; _debugLastSnapshot = null; }
            if (_debugFeedbackText != null) _debugFeedbackText.Text = _debugPaused
                ? "已冻结当前读数 · F3 + R 继续；性能悬浮窗不受影响。" : "窗口未活动，已暂停采样。";
        }
    }

    private async void DebugTimer_Tick(object? sender, EventArgs e) => await RefreshDebugMetricsAsync();
    private async Task RefreshDebugMetricsAsync()
    {
        if (_debugSampling || _debugMetrics == null || _debugSamplingCancellation is not { } cancellation ||
            !_debugPanelVisible || !IsActive || !IsVisible || _isWindowClosed || _debugPaused || OverlayDialogService.HasActiveOverlay) return;
        _debugSampling = true;
        try
        {
            // Reuse the independent performance window's fresh sample when it is already polling.
            var shared = PerformanceOverlayService.LatestSnapshot;
            PerformanceSnapshot sample;
            if (PerformanceOverlayService.IsPolling && shared != null &&
                DateTimeOffset.UtcNow - shared.SampledAt is var age && age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(2.5))
                sample = shared;
            else
            {
                var targets = PerformanceMetricsService.CreateJavaTargets(ProcessManager.Running.ToArray());
                sample = await _debugMetrics.SampleAsync(targets, cancellation.Token);
            }
            if (cancellation.IsCancellationRequested || !ReferenceEquals(cancellation, _debugSamplingCancellation) || !_debugPanelVisible) return;
            _debugLastMetrics = sample;
            if (_debugFeedbackText != null)
            {
                var notes = new List<string>();
                if (sample.SystemCpuPercent == null) notes.Add(sample.SystemCpuUnavailableReason?.StartsWith("首次采样", StringComparison.Ordinal) == true ? "系统 CPU 建立基线中" : "系统 CPU 暂不可用");
                if (sample.LauncherCpuPercent == null) notes.Add(sample.LauncherCpuUnavailableReason?.StartsWith("首次采样", StringComparison.Ordinal) == true ? "启动器 CPU 建立基线中" : "启动器 CPU 暂不可用");
                if (sample.TotalMemoryBytes == null || sample.AvailableMemoryBytes == null) notes.Add("系统内存暂不可用");
                _debugFeedbackText.Text = $"采样于 {sample.SampledAt.LocalDateTime:HH:mm:ss} · CPU 按整机总算力计算" +
                    (notes.Count == 0 ? "" : "\n" + string.Join("；", notes));
            }
            RefreshDebugText();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _debugLastMetrics = null;
            if (_debugFeedbackText != null) _debugFeedbackText.Text = "采样未完成，性能指标暂不可用。";
            LauncherLogService.AppendLine("[调试采样] " + ex.GetType().Name);
            if (_debugPanelVisible && IsActive && !_isWindowClosed) RefreshDebugText();
        }
        finally { _debugSampling = false; }
    }

    private void RefreshDebugText()
    {
        if (!_debugPaused || _debugLastSnapshot == null) _debugLastSnapshot = CaptureDebugSnapshot();
        if (_debugRuntimeText != null) _debugRuntimeText.Text = LauncherDebugSnapshotFormatter.Format(_debugLastSnapshot, _debugView);
        if (_debugPauseButton != null) _debugPauseButton.Content = _debugPaused ? "继续" : "暂停";
        foreach (var (view, button) in _debugViewButtons)
        {
            button.FontWeight = view == _debugView ? FontWeights.Bold : FontWeights.Normal;
            button.SetResourceReference(Control.ForegroundProperty, view == _debugView ? "AccentBrush" : "TextPrimaryBrush");
        }
    }

    private LauncherDebugSnapshot CaptureDebugSnapshot()
    {
        long? workingSet = null;
        int? threads = null;
        try { using var process = Process.GetCurrentProcess(); workingSet = process.WorkingSet64; threads = process.Threads.Count; } catch { }
        var queue = DownloadQueueService.Instance.Items;
        var windowMode = WindowChromeService.IsFullScreen ? LauncherDebugWindowMode.Fullscreen : WindowState switch
        { WindowState.Maximized => LauncherDebugWindowMode.Maximized, WindowState.Minimized => LauncherDebugWindowMode.Minimized, _ => LauncherDebugWindowMode.Normal };
        return new(typeof(App).Assembly.GetName().Version, DebugPage(), windowMode,
            (int)Math.Max(0, ActualWidth), (int)Math.Max(0, ActualHeight), (int)Math.Round(UiZoomTransform.ScaleX * 100),
            IsNativePreviewOpen, Topmost, ConfigService.Config.RestrictedMode,
            _debugUptime?.Elapsed ?? TimeSpan.Zero, _debugLastMetrics?.LauncherWorkingSetBytes ?? workingSet, GC.GetTotalMemory(false),
            _debugLastMetrics?.TotalMemoryBytes, _debugLastMetrics?.AvailableMemoryBytes, _debugLastMetrics?.SystemCpuPercent, _debugLastMetrics?.LauncherCpuPercent,
            _debugLastMetrics?.Games.Count ?? ProcessManager.Running.Count, queue.Count(item => item.IsRunning),
            queue.Count(item => item.Status == DownloadQueueItemStatus.Paused), queue.Count,
            _debugLastMetrics?.Games.Select(game => new LauncherDebugGameSnapshot(game.ProcessId, game.CpuPercent, game.WorkingSetBytes)).ToArray(),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            RenderCapability.Tier >> 16, (int)Math.Round(VisualTreeHelper.GetDpi(this).DpiScaleX * 100), threads);
    }

    private LauncherDebugPage DebugPage() => MainContent.Content?.GetType().Name switch
    {
        null => LauncherDebugPage.Loading, "HomePage" => LauncherDebugPage.Home, "VersionSelectPage" => LauncherDebugPage.Versions,
        "DownloadCenterPage" or "ModDetailPage" => LauncherDebugPage.Downloads, "MultiplayerPage" => LauncherDebugPage.Multiplayer,
        "WorldArchivePage" => LauncherDebugPage.Worlds, "ModManagerPage" => LauncherDebugPage.Mods, "ServerManagerPage" => LauncherDebugPage.Servers,
        "ToolboxPage" => LauncherDebugPage.Toolbox, "BedrockPage" => LauncherDebugPage.Bedrock, "AboutHelpPage" => LauncherDebugPage.About,
        "LoginPage" => LauncherDebugPage.Accounts, "SettingsPage" => LauncherDebugPage.Settings, "MorePage" => LauncherDebugPage.More,
        "LogsPage" => LauncherDebugPage.Logs, "AiAssistantPanel" => LauncherDebugPage.Ai, "PluginApiDocumentationPage" => LauncherDebugPage.Api,
        _ => _activePluginPage == null ? LauncherDebugPage.Other : LauncherDebugPage.Plugin
    };

    private void ResizeDebugPanel()
    {
        if (_debugPanel == null) return;
        _debugPanel.MaxWidth = Math.Max(1, Math.Min(560, AppBodyGrid.ColumnDefinitions[1].ActualWidth - 24));
        _debugPanel.MaxHeight = Math.Max(1, Math.Min(460, AppBodyGrid.RowDefinitions[1].ActualHeight - 24));
    }
    private void DebugBody_SizeChanged(object sender, SizeChangedEventArgs e) => ResizeDebugPanel();
    private void DebugWindow_Activated(object? sender, EventArgs e) => UpdateDebugSamplingState();
    private void DebugWindow_Deactivated(object? sender, EventArgs e) { _debugKeys.Reset(); UpdateDebugSamplingState(); }
    private void DebugWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!IsVisible) _debugKeys.Reset(); UpdateDebugSamplingState(); }
    private void DebugWindow_Closed(object? sender, EventArgs e)
    {
        _debugPanelVisible = false; _debugPaused = false; _debugKeys.Reset(); UpdateDebugSamplingState();
        if (_debugTimer != null) _debugTimer.Tick -= DebugTimer_Tick;
        PreviewKeyDown -= DebugPreviewKeyDown; PreviewKeyUp -= DebugPreviewKeyUp;
        Activated -= DebugWindow_Activated; Deactivated -= DebugWindow_Deactivated;
        IsVisibleChanged -= DebugWindow_IsVisibleChanged; AppBodyGrid.SizeChanged -= DebugBody_SizeChanged;
        if (_debugPanel != null) AppBodyGrid.Children.Remove(_debugPanel);
        _debugMetrics = null;
    }
}
