using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private bool _previewVisualMode;
    private bool _previewSwitching;
    private bool _updatingPreviewNavigation;
    private readonly PreviewNavigationState _previewNavigation = new();
    private readonly List<Action> _previewNavigationUnsubscribe = new();
    public event Action? NativePreviewStateChanged;
    // Public compatibility name now represents the full launcher UI mode.
    public bool IsNativePreviewOpen => _previewVisualMode;
    internal bool CanLaunchFromNativePreview => !_isCancelLaunchState && LaunchGameBtn.IsEnabled
        && DateTime.UtcNow - _lastLaunchClickAtUtc >= LaunchClickCooldown
        && DateTime.UtcNow - _lastLaunchStateSwitchAtUtc >= LaunchStateSwitchGuard;

    private void InitializePreviewVisualMode()
    {
        var contentDescriptor = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl));
        if (contentDescriptor != null)
        {
            EventHandler changed = (_, _) => UpdatePreviewNavigationSelection();
            contentDescriptor.AddValueChanged(MainContent, changed);
            Closed += (_, _) => contentDescriptor.RemoveValueChanged(MainContent, changed);
        }
        // The category filter lives on wrappers. Observe real feature/gate values without overwriting them.
        foreach (var (_, button) in PreviewBuiltInNavigation())
        {
            foreach (var property in new[] { UIElement.VisibilityProperty, UIElement.IsEnabledProperty })
            {
                var descriptor = DependencyPropertyDescriptor.FromProperty(property, typeof(Button));
                if (descriptor == null) continue;
                EventHandler changed = (_, _) => UpdatePreviewNavigationSelection();
                descriptor.AddValueChanged(button, changed);
                _previewNavigationUnsubscribe.Add(() => descriptor.RemoveValueChanged(button, changed));
            }
        }
        ConfigService.Saved += QueuePreviewNavigationRefresh;
        ConfigService.Saved += QueuePreviewAppearanceRefresh;
        ThemeService.AppearanceChanged += QueuePreviewAppearanceRefresh;
        PluginUiContributionRegistry.Changed += QueuePreviewNavigationRefresh;
        Closed += (_, _) =>
        {
            ConfigService.Saved -= QueuePreviewNavigationRefresh;
            ConfigService.Saved -= QueuePreviewAppearanceRefresh;
            ThemeService.AppearanceChanged -= QueuePreviewAppearanceRefresh;
            _previewPagePalette.Restore();
            PluginUiContributionRegistry.Changed -= QueuePreviewNavigationRefresh;
            foreach (var unsubscribe in _previewNavigationUnsubscribe) unsubscribe();
            _previewNavigationUnsubscribe.Clear();
        };
        if (ConfigService.Config.PreviewModeEnabled || App.StartupArgs.EnterPreviewMode) SetPreviewVisualMode(true, persist: false);
    }
    public bool OpenWinUiPreview() => SetPreviewVisualMode(true);
    public void CloseNativePreview() => SetPreviewVisualMode(false);

    private bool SetPreviewVisualMode(bool enabled, bool persist = true)
    {
        Dispatcher.VerifyAccess();
        if (_previewSwitching) return false;
        if (_previewVisualMode == enabled) return true;
        bool previous = _previewVisualMode;
        bool previousConfig = ConfigService.Config.PreviewModeEnabled;
        bool wasVisible = IsVisible;
        var focused = Keyboard.FocusedElement;
        _previewSwitching = true;
        try
        {
            DownloadQueuePopup.IsOpen = false;
            // Remove the old presentation before showing the new one.
            // Existing page objects retain handlers, drafts and running tasks.
            if (wasVisible) Hide();
            PreviewFluentTheme.SetEnabled(enabled, this);
            _previewVisualMode = enabled;
            ApplyPreviewLayout();
            ThemeService.ApplyWinUi3Typography(enabled || ConfigService.Config.EnableWinUi3Design);
            if (persist) { ConfigService.Config.PreviewModeEnabled = enabled; ConfigService.Save(); }
            RaisePreviewStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            _previewVisualMode = previous;
            ConfigService.Config.PreviewModeEnabled = previousConfig;
            try
            {
                PreviewFluentTheme.SetEnabled(previous, this);
                ApplyPreviewLayout();
                ThemeService.ApplyWinUi3Typography(previous || ConfigService.Config.EnableWinUi3Design);
                if (persist) ConfigService.Save();
            }
            catch (Exception restoreError) { LauncherLogService.AppendLine("[界面切换恢复] " + restoreError); }
            RaisePreviewStateChanged();
            LauncherLogService.AppendLine("[新界面预览] " + ex);
            if (wasVisible) Show();
            MessageBoxDialog.ShowWarning("新界面切换失败，已恢复原界面：" + ex.Message, "新界面预览");
            return false;
        }
        finally
        {
            _previewSwitching = false;
            if (wasVisible)
            {
                Show(); Activate();
                if (focused is UIElement { IsVisible: true } element) element.Focus();
            }
        }
    }
    private void ApplyPreviewLayout()
    {
        double height = WindowChromeService.IsFullScreen ? 0 : _previewVisualMode ? 40 : 32;
        TitleBarRow.Height = new GridLength(height);
        CustomTitleBar.Visibility = WindowChromeService.IsFullScreen ? Visibility.Collapsed : Visibility.Visible;
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            if (chrome.IsFrozen) { chrome = (WindowChrome)chrome.Clone(); WindowChrome.SetWindowChrome(this, chrome); }
            chrome.CaptionHeight = height;
        }
        ContentAreaBorder.Margin = new Thickness(_previewVisualMode ? 20 : 16);
        PreviewCategoryBar.Visibility = _previewVisualMode ? Visibility.Visible : Visibility.Collapsed;
        ApplySidebarCollapsedState(_sidebarCollapsed);
        MaximizeRestoreButton.Visibility = Visibility.Visible;
        if (!WindowChromeService.IsFullScreen) ResizeMode = ResizeMode.CanResize;
        UpdatePreviewNavigationSelection();
        ApplyResponsiveLayout();
        RefreshPreviewAppearance();
    }

    private IEnumerable<(string Page, Button Button)> PreviewBuiltInNavigation()
    {
        foreach (var host in NavButtonsPanel.Children.OfType<Border>())
            if (host.Tag is string page && host.Child is Button button) yield return (page, button);
    }

    private void AddPreviewPluginNavigationButton(Button button, string id)
    {
        button.CommandParameter = "plugin:" + id;
        NavButtonsPanel.Children.Add(new Border { Tag = "plugin:" + id, Child = button,
            Visibility = !_previewVisualMode || _previewNavigation.Category == "settings" ? Visibility.Visible : Visibility.Collapsed });
    }

    private void RemovePreviewPluginNavigationButton(Button button)
    {
        if (button.Parent is Border host && host.Parent == NavButtonsPanel) NavButtonsPanel.Children.Remove(host);
        else NavButtonsPanel.Children.Remove(button);
    }

    private void QueuePreviewNavigationRefresh()
    {
        if (_isWindowClosed) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!_isWindowClosed) UpdatePreviewNavigationSelection();
        }));
    }

    private string? CurrentPreviewPage() => MainContent.Content?.GetType().Name switch
    {
        "HomePage" => "home", "VersionSelectPage" => "instances",
        "DownloadCenterPage" or "ModDetailPage" => "downloads",
        "MultiplayerPage" => "multiplayer", "WorldArchivePage" => "worlds",
        "ModManagerPage" => "mods", "ServerManagerPage" => "servers",
        "ToolboxPage" => "toolbox", "BedrockPage" => "bedrock",
        "AboutHelpPage" => "about", "LoginPage" => "accounts",
        "SettingsPage" => "settings", "MorePage" => "more", "LogsPage" => "logs",
        "AiAssistantPanel" => "ai", "PluginApiDocumentationPage" => "api",
        null => null,
        _ => _activePluginPage == null ? null : "plugin:" + _activePluginPage
    };

    private void PreviewCategory_Click(object sender, RoutedEventArgs e)
    {
        if (!_previewVisualMode || sender is not Button { CommandParameter: string category }) return;
        var available = PreviewBuiltInNavigation().Where(item => item.Button.Visibility == Visibility.Visible && item.Button.IsEnabled).ToArray();
        var destination = _previewNavigation.SelectCategory(category, available.Select(item => item.Page));
        UpdatePreviewNavigationSelection(synchronizePage: false);
        var target = available.FirstOrDefault(item => item.Page == destination).Button;
        if (target != null && CurrentPreviewPage() != destination)
            target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // Original navigation may be cancelled by the unsaved-settings dialog or an agreement gate.
        // Observe the actual page after the handler; a pending async page keeps the selected category.
        UpdatePreviewNavigationSelection();
    }

    private void UpdatePreviewNavigationSelection(bool synchronizePage = true)
    {
        if (_updatingPreviewNavigation) return;
        _updatingPreviewNavigation = true;
        try
        {
            var active = CurrentPreviewPage();
            if (synchronizePage) _previewNavigation.ObservePage(active);
            var entries = PreviewBuiltInNavigation().ToArray();
            foreach (var host in NavButtonsPanel.Children.OfType<Border>())
                if (host.Tag is string pageKey)
                    host.Visibility = !_previewVisualMode || PreviewNavigationState.CategoryForPage(pageKey) == _previewNavigation.Category
                        ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (pageKey, button) in entries)
                button.Tag = _previewVisualMode && pageKey == active ? "PreviewSelected" : null;
            foreach (var categoryButton in PreviewCategoryTabs.Children.OfType<Button>())
            {
                if (categoryButton.CommandParameter is not string category) continue;
                categoryButton.Tag = category == _previewNavigation.Category ? "PreviewSelected" : null;
                categoryButton.IsEnabled = entries.Any(item => PreviewNavigationState.CategoryForPage(item.Page) == category &&
                    item.Button.Visibility == Visibility.Visible && item.Button.IsEnabled);
            }
            LogoText.Text = _previewVisualMode ? PreviewNavigationState.Categories.First(item => item.Id == _previewNavigation.Category).Title : "XCL2";
            if (MainContent.Content is DependencyObject page) PreviewFluentTheme.ApplyTo(page);
        }
        finally { _updatingPreviewNavigation = false; }
    }

    private void NavPlugins_Click(object sender, RoutedEventArgs e) => OpenPluginManager();
    private void RaisePreviewStateChanged()
    {
        if (NativePreviewStateChanged is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception ex) { LauncherLogService.AppendLine("[新界面状态同步] " + ex.Message); }
        }
    }
}
