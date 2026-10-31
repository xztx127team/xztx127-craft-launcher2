using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private readonly PreviewPagePalette _previewPagePalette = new();
    private readonly PreviewLayoutOverrides _previewShellLayout = new();
    private bool _previewAppearanceRefreshQueued;

    private void QueuePreviewAppearanceRefresh()
    {
        if (_isWindowClosed || _previewAppearanceRefreshQueued) return;
        _previewAppearanceRefreshQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _previewAppearanceRefreshQueued = false;
            if (!_isWindowClosed) RefreshPreviewAppearance();
        }));
    }

    private void RefreshPreviewAppearance()
    {
        _previewPagePalette.Apply(this, _previewVisualMode, ConfigService.Config.PreviewBluePage, ThemeService.CurrentIsDarkMode);
        ApplyPreviewShellMetrics();
        if (!_previewVisualMode)
        {
            if (PreviewPageColorButton.ContextMenu is { } menu) menu.IsOpen = false;
            return;
        }
        PreviewPageColorButton.ToolTip = ConfigService.Config.PreviewBluePage
            ? "页面颜色：蓝色。点击切换为跟随原主题。"
            : "页面颜色：跟随原主题。点击可选择蓝色。";
        UpdatePreviewHeaderLayout(AppBodyGrid.ActualWidth > 0 ? AppBodyGrid.ActualWidth : Width);
    }

    private void ApplyPreviewShellMetrics()
    {
        if (!_previewVisualMode) { _previewShellLayout.Restore(); return; }
        var pageBrush = TryFindResource(ConfigService.Config.PreviewBluePage ? "PreviewPageBrush" : "SideBrush") as Brush
            ?? Brushes.Transparent;
        _previewShellLayout.TrySet(AppBodyGrid, Panel.BackgroundProperty, pageBrush);
        _previewShellLayout.TrySet(SidebarAreaBorder, Border.CornerRadiusProperty, new CornerRadius(20));
        _previewShellLayout.TrySet(SidebarAreaBorder, Border.BorderThicknessProperty, new Thickness(1));
        var outer = _sidebarCollapsed ? 6 : 12;
        var sidebarOnRight = SidebarPreferences.SidebarColumn(ConfigService.Config.SidebarPlacement) == 1;
        _previewShellLayout.TrySet(SidebarAreaBorder, MarginProperty,
            sidebarOnRight ? new Thickness(0, 12, outer, 12) : new Thickness(outer, 12, 0, 12));
        _previewShellLayout.TrySet(LaunchGameBtn, Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center);
        _previewShellLayout.TrySet(LaunchStatusText, TextBlock.TextAlignmentProperty, TextAlignment.Center);
    }

    private void UpdatePreviewHeaderLayout(double width)
    {
        if (!_previewVisualMode) return;
        var stacked = width < 920;
        Grid.SetRow(PreviewCategoryScroll, stacked ? 1 : 0);
        Grid.SetColumn(PreviewCategoryScroll, stacked ? 0 : 1);
        Grid.SetColumnSpan(PreviewCategoryScroll, stacked ? 3 : 1);
        PreviewCategoryScroll.Margin = new Thickness(0, stacked ? 8 : 0, 0, 0);
        var minimal = width < 360;
        PreviewBrandGroup.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(PreviewPageColorButton, minimal ? 0 : 2);
        Grid.SetColumnSpan(PreviewPageColorButton, minimal ? 3 : 1);
        PreviewPageColorButton.HorizontalAlignment = minimal ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        PreviewPageColorButton.Margin = new Thickness(minimal ? 0 : 12, 0, 0, 0);
        PreviewCategoryBar.Margin = width < 600 ? new Thickness(8, 6, 8, 0) : new Thickness(12, 8, 12, 0);
    }

    private void PreviewPageColor_Click(object sender, RoutedEventArgs e)
    {
        if (!_previewVisualMode || sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button };
        AddColor("蓝色", true);
        AddColor("跟随原主题", false);
        button.ContextMenu = menu;
        PreviewFluentTheme.ApplyTo(menu);
        menu.IsOpen = true;

        void AddColor(string label, bool blue)
        {
            var option = new MenuItem { Header = label, IsCheckable = true, IsChecked = ConfigService.Config.PreviewBluePage == blue };
            option.Click += (_, _) =>
            {
                var previous = ConfigService.Config.PreviewBluePage;
                try
                {
                    ConfigService.Config.PreviewBluePage = blue;
                    RefreshPreviewAppearance();
                    ConfigService.Save();
                }
                catch (Exception ex)
                {
                    ConfigService.Config.PreviewBluePage = previous;
                    RefreshPreviewAppearance();
                    MessageBoxDialog.ShowError("保存预览页面颜色失败：" + ex.Message);
                }
            };
            menu.Items.Add(option);
        }
    }
}
