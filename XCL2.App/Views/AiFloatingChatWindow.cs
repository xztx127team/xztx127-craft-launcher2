using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>关闭按钮只收起聊天窗口；悬浮球和该会话继续存在，待回答通知可重新唤起它。</summary>
internal sealed class AiFloatingChatWindow : Window, IGlobalWindowTreatmentExempt
{
    private readonly MainWindow _launcher;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private bool _shuttingDown;
    private bool _hidingChat;
    private bool _minimizing;
    private readonly System.Windows.Media.TranslateTransform _windowMove = new();
    private readonly Action _showBallSettings;
    internal AiAssistantPanel Panel { get; }
    private Grid _panelRoot = null!;
    private AiFloatingBallScope _scope;
    internal Action? LauncherRevealRequested { get; set; }
    internal Action? LauncherCollapseRequested { get; set; }

    internal AiFloatingChatWindow(MainWindow launcher, AiAssistantConfig config,
        Action<AiAssistantConfig> saveQuick, Action showBallSettings)
    {
        _launcher = launcher;
        _showBallSettings = showBallSettings;
        Title = "AI助手";
        Width = Math.Min(440, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(620, SystemParameters.WorkArea.Height - 48);
        MinWidth = Math.Min(360, SystemParameters.WorkArea.Width - 32);
        MinHeight = Math.Min(400, SystemParameters.WorkArea.Height - 48);
        ShowInTaskbar = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 48, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(12), UseAeroCaptionButtons = false });
        SourceInitialized += (_, _) =>
        {
            WindowChromeService.EnableWorkAreaAwareMaximize(this);
            WindowChromeService.ApplyNativeTransitionPreference(this);
        };
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var service = new AiAssistantService(config, App.DataDir, _http)
        {
            SharedAccountingConfigProvider = () => launcher.AiAssistantConfig,
            SaveSharedAccounting = () => launcher.ConfigService.Save()
        };
        if (string.IsNullOrWhiteSpace(config.LastSessionId) || service.LoadSession(config.LastSessionId) == null)
            config.LastSessionId = service.CreateNewSession("悬浮聊天").Id;
        Panel = new AiAssistantPanel { SuppressPrewarm = true, FloatingQuickConfigSaved = saveQuick,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        Panel.EnableSideDrawerView();
        Panel.Attach(service, config);
        Panel.CloseRequested += (_, _) => { if (_scope == AiFloatingBallScope.Launcher) LauncherCollapseRequested?.Invoke(); else HideWithTransition(); };
        Panel.SettingsRequested += (_, _) => OpenAiSettings();
        var root = _panelRoot = new Grid { RenderTransform = _windowMove };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var caption = new Grid { Margin = new Thickness(16, 6, 8, 0) };
        caption.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "PanelBrush");
        caption.ColumnDefinitions.Add(new ColumnDefinition());
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(AiAssistantPanel.CreateCompactTitle());
        var dock = new Button { Content = "回到启动器", FontSize = 11, Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4), ToolTip = "将当前对话移回启动器内，保留历史、选中模型和正在进行的请求。" };
        dock.SetResourceReference(StyleProperty, "QuietActionButton");
        WindowChrome.SetIsHitTestVisibleInChrome(dock, true);
        dock.Click += (_, _) => _launcher.DockFloatingAiChat();
        titleRow.Children.Add(dock);
        caption.Children.Add(titleRow);
        caption.ToolTip = "拖动标题移动窗口；右键可配置 AI、悬浮球外观和窗口置顶。";
        // Only blank caption space starts a native window drag. Buttons retain their
        // mouse capture until Click; right-click still opens the configuration menu.
        WindowChrome.SetIsHitTestVisibleInChrome(caption, true);
        caption.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount != 1 || Mouse.LeftButton != MouseButtonState.Pressed) return;
            for (var node = args.OriginalSource as DependencyObject; node != null && !ReferenceEquals(node, caption);)
            {
                if (node is System.Windows.Controls.Primitives.ButtonBase) return;
                node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }
            DragMove();
            args.Handled = true;
        };
        var configurationMenu = CreateConfigurationMenu();
        caption.ContextMenu = configurationMenu;
        var windowButtons = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(windowButtons, 1); caption.Children.Add(windowButtons);
        void CaptionButton(string icon, string name, string hint, Action action)
        {
            var button = AiAssistantPanel.CreateCompactIconButton(icon, name, hint);
            button.Width = 36;
            button.Margin = new Thickness(0, 0, 2, 0);
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            button.Click += (_, _) => action();
            windowButtons.Children.Add(button);
        }
        CaptionButton("IconWinMinimize", "最小化", "最小化到任务栏", MinimizeChat);
        CaptionButton("IconWinClose", "关闭", "关闭聊天窗口，保留当前对话和后台请求；点击悬浮球可重新打开。", HideWithTransition);
        root.Children.Add(caption);
        Grid.SetRow(Panel, 1); root.Children.Add(Panel);
        Content = root;
        RoundedWindowService.Attach(this, root);
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized && IsVisible && !_shuttingDown)
                UiAnimationHelper.SettleScale(root, new System.Windows.Media.ScaleTransform(), kind: AnimationKind.WindowRestore);
        };
        Closing += OnClosing;
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            HideWithTransition(); args.Handled = true;
        };
    }

    private ContextMenu CreateConfigurationMenu()
    {
        var menu = new ContextMenu();
        void Add(string text, string hint, Action action)
        {
            var item = new MenuItem { Header = text, ToolTip = hint };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("AI 设置…", "配置接口、模型、联网搜索、自动保存与助手权限。", OpenAiSettings);
        Add("悬浮球外观与位置…", "调整悬浮球颜色、样式、显示范围及独立置顶设置。", _showBallSettings);
        var topmost = new MenuItem { Header = "聊天窗口置顶", IsCheckable = true,
            ToolTip = "桌面聊天窗口保持在其他普通窗口上方；与悬浮球置顶分别保存。" };
        topmost.Click += (_, _) => _launcher.SetAiFloatingTopmost(ball: false, enabled: topmost.IsChecked);
        menu.Items.Add(topmost);
        menu.Opened += (_, _) => topmost.IsChecked = _launcher.ConfigService.Config.AiFloatingBeta.ChatTopmost;
        menu.Items.Add(new Separator());
        Add("新对话", "当前对话会保留在历史中。", Panel.NewFloatingConversation);
        Add("回到启动器", "将当前对话移回启动器内。", _launcher.DockFloatingAiChat);
        Add("关闭聊天窗口", "收起窗口，后台请求继续；点击悬浮球可回来。", HideWithTransition);
        return menu;
    }

    internal void ApplyScope(AiFloatingBallScope scope)
    {
        _scope = scope;
        Owner = scope == AiFloatingBallScope.Launcher ? _launcher : null;
        Topmost = scope == AiFloatingBallScope.Desktop && _launcher.ConfigService.Config.AiFloatingBeta.ChatTopmost;
        ShowInTaskbar = scope == AiFloatingBallScope.Desktop;
    }

    internal void Reveal()
    {
        if (_shuttingDown) return;
        _hidingChat = _minimizing = false;
        UiAnimationHelper.Reset(_panelRoot, _windowMove);
        if (_scope == AiFloatingBallScope.Launcher && LauncherRevealRequested != null)
        { LauncherRevealRequested(); return; }
        AttachPanelToWindow();
        if (Owner is { } owner)
        {
            if (!owner.IsVisible) owner.Show();
            if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
        }
        var opening = !IsVisible;
        if (opening)
        {
            _panelRoot.Opacity = 1 - .65 * AnimationSettingsService.Intensity(AnimationKind.WindowOpen);
            Show();
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
        if (opening) UiAnimationHelper.SettleScale(_panelRoot, new System.Windows.Media.ScaleTransform(), kind: AnimationKind.WindowOpen);
    }

    internal void DetachPanelFromWindow()
    {
        if (ReferenceEquals(Panel.Parent, _panelRoot)) _panelRoot.Children.Remove(Panel);
    }

    internal void AttachPanelToWindow()
    {
        if (ReferenceEquals(Panel.Parent, _panelRoot)) return;
        if (Panel.Parent is System.Windows.Controls.Panel oldPanel) oldPanel.Children.Remove(Panel);
        else if (Panel.Parent is ContentControl oldHost) oldHost.Content = null;
        Grid.SetRow(Panel, 1);
        _panelRoot.Children.Add(Panel);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shuttingDown) return;
        e.Cancel = true;
        HideWithTransition();
    }

    private void MinimizeChat()
    {
        if (_shuttingDown || _minimizing || WindowState == WindowState.Minimized) return;
        _hidingChat = false;
        _minimizing = true;
        UiAnimationHelper.BeforeMinimize(_panelRoot, _windowMove, () =>
        {
            _minimizing = false;
            if (!_shuttingDown) WindowState = WindowState.Minimized;
            UiAnimationHelper.Reset(_panelRoot, _windowMove);
        });
    }

    private void HideWithTransition()
    {
        if (_shuttingDown || _hidingChat || !IsVisible) return;
        _hidingChat = true;
        _minimizing = false;
        UiAnimationHelper.FadeOutMove(_panelRoot, _windowMove, 5, () =>
        {
            _hidingChat = false;
            Hide();
            UiAnimationHelper.Reset(_panelRoot, _windowMove);
        });
    }

    internal void OpenAiSettings()
    {
        var settings = new AiAssistantSettingsPanel(_launcher.AiAssistantConfig);
        var dialog = new Window { Owner = IsVisible ? this : _launcher, Title = "AI 助手设置", Width = Math.Min(900, SystemParameters.WorkArea.Width - 60),
            Height = Math.Min(760, SystemParameters.WorkArea.Height - 60), Content = settings,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var closeApproved = false;
        settings.RequestClose += (_, _) =>
        {
            closeApproved = true;
            _ = dialog.Dispatcher.BeginInvoke(new Action(dialog.Close));
        };
        var closeRequestPending = false;
        dialog.Closing += (_, e) =>
        {
            if (closeApproved) return;
            e.Cancel = true;
            if (closeRequestPending) return;
            closeRequestPending = true;
            // 完成当前 Closing 栈后再打开 owner-owned 确认，避免模态窗口关闭重入。
            dialog.Dispatcher.BeginInvoke(new Action(() =>
            {
                closeRequestPending = false;
                if (dialog.IsVisible) settings.RequestDismiss();
            }));
        };
        settings.UnsavedChangesPrompt = () => OwnedMessageDialog.Show(dialog, "AI 助手设置尚未保存，是否保存本次修改？", "保存 AI 助手设置",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => XclMessageResult.Yes,
            MessageBoxResult.No => XclMessageResult.No,
            _ => XclMessageResult.Cancel
        };
        settings.SettingsWarningPrompt = (message, title) => OwnedMessageDialog.Show(dialog, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        settings.Saved += (_, config) =>
        {
            _launcher.AiAssistantConfig = config;
            _launcher.AiAssistantService.UpdateConfig(config);
            _launcher.PersistAiAssistantConfig();
            _launcher.RefreshAiPanelsAfterFloatingSettings();
            // Refresh credentials/catalog while retaining the floating conversation's live selection.
            var local = _launcher.CreateFloatingAiConfig();
            local.WorkMode = Panel.FloatingConfig.WorkMode;
            local.RoutingMode = Panel.FloatingConfig.RoutingMode;
            local.SelectedModel = Panel.FloatingConfig.SelectedModel;
            local.LastSessionId = Panel.FloatingConfig.LastSessionId;
            Panel.ApplyConfig(local);
        };
        dialog.ShowDialog();
    }

    internal async Task ShutdownAsync()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try { await Panel.StopForOverlayCloseAsync(); }
        finally { _http.Dispose(); Close(); }
    }
}
