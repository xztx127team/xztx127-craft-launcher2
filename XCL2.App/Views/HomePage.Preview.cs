using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;
using XCL2.Preview;

namespace XCL2.App.Views;

public partial class HomePage
{
    private Style? _defaultTileStyle;
    private Style? _defaultLanguageStyle;
    private readonly PreviewLayoutOverrides _previewHomeAlignment = new();
    private readonly DispatcherTimer _previewCardsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private PreviewCard[] _lastPreviewCards = Array.Empty<PreviewCard>();
    private PreviewAppearance? _lastPreviewAppearance;
    private bool _lastPreviewRestricted;

    private void InitializePreviewHome()
    {
        _defaultTileStyle = LaunchTile.Style;
        _defaultLanguageStyle = LanguageEntryButton.Style;
        _previewCardsTimer.Tick += (_, _) => RefreshPreviewCards();
        SyncNativePreview();
    }

    private void ApplyPreviewHomeStyles()
    {
        if (_owner.IsNativePreviewOpen)
        {
            _previewHomeAlignment.TrySet(HomeSurface, Border.CornerRadiusProperty, new CornerRadius(20));
            _previewHomeAlignment.TrySet(HomeContextHint, Border.CornerRadiusProperty, new CornerRadius(12));
            _previewHomeAlignment.TrySet(CapsuleBar, FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            _previewHomeAlignment.TrySet(CapsuleBar, RightAlignedWrapPanel.RowAlignmentProperty, HorizontalAlignment.Center);
            _previewHomeAlignment.TrySet(PreviewPluginCards, FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            _previewHomeAlignment.TrySet(PreviewPluginHeading, TextBlock.TextAlignmentProperty, TextAlignment.Left);
            foreach (var heading in HomeBodyPanel.Children.OfType<TextBlock>())
                _previewHomeAlignment.TrySet(heading, TextBlock.TextAlignmentProperty, TextAlignment.Left);

            foreach (var tile in new[] { LaunchTile, AccountsTile, DownloadTile, QuickStartTile, ServerManagerTile, SettingsTile })
            {
                // Align labels and badges to one reading edge; keep the full card
                // width available for translated descriptions and narrow layouts.
                _previewHomeAlignment.TrySet(tile, Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch);
                _previewHomeAlignment.TrySet(tile, Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
                if (tile.Content is not StackPanel content) continue;
                _previewHomeAlignment.TrySet(content, FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
                _previewHomeAlignment.TrySet(content, FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                foreach (var label in content.Children.OfType<TextBlock>())
                {
                    _previewHomeAlignment.TrySet(label, TextBlock.TextAlignmentProperty, TextAlignment.Left);
                    _previewHomeAlignment.TrySet(label, TextBlock.TextWrappingProperty, TextWrapping.Wrap);
                }
                foreach (var badge in content.Children.OfType<Border>())
                {
                    _previewHomeAlignment.TrySet(badge, FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
                    _previewHomeAlignment.TrySet(badge, Border.CornerRadiusProperty, new CornerRadius(12));
                    _previewHomeAlignment.TrySet(badge, FrameworkElement.WidthProperty, 44d);
                    _previewHomeAlignment.TrySet(badge, FrameworkElement.HeightProperty, 44d);
                }
            }
        }
        else _previewHomeAlignment.Restore();
        HomeSurface.Padding = new Thickness(_owner.IsNativePreviewOpen ? 24 : 30);
        // StaticResource 会保留本页的旧样式对象；全窗模式切换时显式切换这两类局部样式。
        var tileStyle = _owner.IsNativePreviewOpen
            ? Application.Current.TryFindResource("TileButton") as Style ?? _defaultTileStyle
            : _defaultTileStyle;
        var languageStyle = _owner.IsNativePreviewOpen
            ? Application.Current.TryFindResource("LanguageEntryButtonStyle") as Style ?? _defaultLanguageStyle
            : _defaultLanguageStyle;
        foreach (var tile in new[] { LaunchTile, AccountsTile, DownloadTile, QuickStartTile, ServerManagerTile, SettingsTile })
            tile.Style = tileStyle;
        LanguageEntryButton.Style = languageStyle;
        if (_owner.IsNativePreviewOpen)
        {
            HomeSurface.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
            HomeSurface.BorderThickness = new Thickness(1);
            LaunchTile.SetResourceReference(Button.BackgroundProperty, "GlowSoftBrush");
            LaunchTile.SetResourceReference(Button.BorderBrushProperty, "AccentBrush");
        }
        else
        {
            HomeSurface.ClearValue(Border.BorderBrushProperty);
            HomeSurface.ClearValue(Border.BorderThicknessProperty);
            LaunchTile.ClearValue(Button.BackgroundProperty);
            LaunchTile.ClearValue(Button.BorderBrushProperty);
        }
        RefreshForSizeChange();
    }

    private void RefreshPreviewCards()
    {
        if (!_owner.IsNativePreviewOpen)
        {
            PreviewPluginSection.Visibility = Visibility.Collapsed;
            PreviewPluginCards.Children.Clear();
            _lastPreviewCards = Array.Empty<PreviewCard>();
            _lastPreviewAppearance = null;
            return;
        }

        var snapshot = PluginPreviewService.Snapshot();
        var restricted = _owner.ConfigService.Config.RestrictedMode;
        PreviewPluginSection.Visibility = snapshot.Cards.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_lastPreviewAppearance == snapshot.Appearance && _lastPreviewRestricted == restricted
            && _lastPreviewCards.SequenceEqual(snapshot.Cards)) return;

        _lastPreviewCards = snapshot.Cards;
        _lastPreviewAppearance = snapshot.Appearance;
        _lastPreviewRestricted = restricted;
        PreviewPluginHeading.Text = snapshot.Appearance.Title == "XCL2"
            ? "插件扩展" : snapshot.Appearance.Title + " · 插件扩展";
        if (ColorConverter.ConvertFromString(snapshot.Appearance.Accent) is Color appearanceAccent)
            PreviewPluginHeading.Foreground = new SolidColorBrush(appearanceAccent);
        PreviewPluginCards.Children.Clear();
        foreach (var card in snapshot.Cards)
        {
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var source = new TextBlock { Text = card.Plugin, FontSize = 11, TextAlignment = TextAlignment.Left,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            source.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            content.Children.Add(source);
            var title = new TextBlock { Text = card.Title, FontSize = 15, FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Left, TextWrapping = TextWrapping.Wrap };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            content.Children.Add(title);
            var description = new TextBlock { Text = card.Description, FontSize = 12, TextAlignment = TextAlignment.Left,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            description.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            content.Children.Add(description);

            var button = new Button
            {
                Content = content,
                Style = Application.Current.TryFindResource("TileButton") as Style ?? _defaultTileStyle,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Width = snapshot.Appearance.Compact ? 220 : 260,
                Padding = snapshot.Appearance.Compact ? new Thickness(14) : new Thickness(18),
                Margin = new Thickness(0, 0, 10, 10),
                BorderBrush = ColorConverter.ConvertFromString(card.Accent) is Color accent
                    ? new SolidColorBrush(accent) : new SolidColorBrush(Colors.ForestGreen),
                IsEnabled = !restricted || card.Destination is "home" or "instances" or "ai",
                ToolTip = card.Description
            };
            button.Click += (_, _) => NavigatePreviewCard(card.Destination);
            PreviewPluginCards.Children.Add(button);
        }
    }

    private void NavigatePreviewCard(string destination)
    {
        // 使用真实页面入口，与原侧边栏、首页磁贴共享业务与权限设置。
        switch (destination)
        {
            case "home": _owner.NavigateToHome(); break;
            case "instances": _owner.NavigateToVersions(); break;
            case "worlds": _owner.NavigateToWorlds(); break;
            case "settings": _owner.NavigateToSettings(); break;
            case "ai": _owner.NavigateToAiAssistant(); break;
            case "downloads": _owner.NavigateToDownloadCenter(); break;
            case "plugins": _owner.OpenPluginManager(); break;
        }
    }
}
