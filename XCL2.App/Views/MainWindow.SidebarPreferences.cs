using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private bool _sidebarPreferencesReady;
    private bool _sidebarPreferencesRefreshQueued;
    private string? _appliedSidebarPreferences;

    private void InitializeSidebarPreferences()
    {
        _sidebarPreferencesReady = true;
        ConfigService.Saved += QueueSidebarPreferencesRefresh;
        Closed += (_, _) => ConfigService.Saved -= QueueSidebarPreferencesRefresh;
        ApplySidebarCollapsedState(ConfigService.Config.SidebarStartCollapsed);
        // Narrow windows stay as an icon rail until the user opens the drawer.
        _wideSidebarCollapsed = ConfigService.Config.SidebarStartCollapsed;
        if (_compactWindowLayout) ApplySidebarCollapsedState(true);
        ApplySidebarPreferences();
    }

    private void QueueSidebarPreferencesRefresh()
    {
        if (_isWindowClosed || _sidebarPreferencesRefreshQueued) return;
        _sidebarPreferencesRefreshQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _sidebarPreferencesRefreshQueued = false;
            if (!_isWindowClosed) ApplySidebarPreferences();
        }));
    }

    public void ApplySidebarPreferences()
    {
        if (!_sidebarPreferencesReady) return;
        var config = ConfigService.Config;
        var fingerprint = JsonSerializer.Serialize(new
        {
            config.SidebarPlacement, config.SidebarExpandedWidth, config.SidebarLabelFontSize,
            config.SidebarShowNavigationIcons,
            Language = LocalizationService.CurrentLanguageCode,
            Labels = config.SidebarLabels.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToArray()
        });
        if (fingerprint == _appliedSidebarPreferences) return;
        foreach (var (key, button) in PreviewBuiltInNavigation())
        {
            if (button.Content is not Panel content) continue;
            var label = content.Children.OfType<TextBlock>().FirstOrDefault();
            if (label == null) continue;
            var definition = SidebarPreferences.Labels.FirstOrDefault(item => item.Key == key);
            if (definition.Key == null) continue;
            var custom = config.SidebarLabels.TryGetValue(key, out var value) ? SidebarPreferences.NormalizeLabel(value) : "";
            if (custom.Length > 0) label.Text = custom;
            else if (definition.ResourceKey != null) label.SetResourceReference(TextBlock.TextProperty, definition.ResourceKey);
            else label.Text = definition.DefaultText;
            // Tooltips retain the destination's original help; a renamed label is only presentation.
            label.ToolTip = label.Text;
            AutomationProperties.SetName(button, label.Text);
        }
        ApplySidebarCollapsedState(_sidebarCollapsed);
        ApplyResponsiveLayout();
        _appliedSidebarPreferences = fingerprint;
    }

    private void ApplySidebarPlacement(bool drawerOpen)
    {
        var contentColumn = SidebarPlacementLayout.Apply(AppBodyGrid, SidebarColumn, SidebarAreaBorder,
            ContentAreaBorder, ConfigService.Config.SidebarPlacement, drawerOpen,
            AiSideDrawerLayer, ReminderBadgeButton, ReminderPanel);
        if (_debugPanel != null) Grid.SetColumn(_debugPanel, contentColumn);
        ResizeDebugPanel();
    }

    private void UpdateSidebarLabelLayout()
    {
        var config = ConfigService.Config;
        foreach (var (_, button) in PreviewBuiltInNavigation())
        {
            if (button.Content is not Panel content) continue;
            var label = content.Children.OfType<TextBlock>().FirstOrDefault();
            var icon = content.Children.OfType<Border>().FirstOrDefault();
            if (label == null || icon == null) continue;
            var showIcon = _sidebarCollapsed || config.SidebarShowNavigationIcons;
            icon.Visibility = showIcon ? Visibility.Visible : Visibility.Collapsed;
            label.Margin = new Thickness(showIcon ? 8 : 0, 0, 0, 0);
            label.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            label.MaxWidth = Math.Max(32, SidebarExpandedWidth - SidebarPanel.Margin.Left - SidebarPanel.Margin.Right
                - button.Padding.Left - button.Padding.Right - (showIcon ? 28 : 0) - 4);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            if (config.SidebarLabelFontSize > 0) label.FontSize = config.SidebarLabelFontSize;
            else label.ClearValue(TextBlock.FontSizeProperty);
        }
    }

    public void NavigateToModDownloads(string? minecraftVersion, string? loader)
        => NavigateLazy(() => new DownloadCenterPage(this), page =>
        {
            if (page is DownloadCenterPage downloads) downloads.SelectModCategoryForInstance(minecraftVersion, loader);
        });
}
