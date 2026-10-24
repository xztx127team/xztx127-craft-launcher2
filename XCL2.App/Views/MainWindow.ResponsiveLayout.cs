using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private bool _responsiveLayoutReady;
    private bool _compactWindowLayout;
    private bool _shortWindowLayout;
    private bool _wideSidebarCollapsed;

    private void InitializeResponsiveLayout()
    {
        _responsiveLayoutReady = true;
        _wideSidebarCollapsed = _sidebarCollapsed;
        AppBodyGrid.SizeChanged += (_, _) => ApplyResponsiveLayout();
        Loaded += (_, _) => ApplyResponsiveLayout();
        ContentViewport.SizeChanged += (_, _) => RefreshCompactContentViewport();
        ContentAreaBorder.PreviewMouseDown += (_, _) =>
        {
            if (_compactWindowLayout && !_sidebarCollapsed) ApplySidebarCollapsedState(true);
        };
        NavButtonsPanel.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) =>
        {
            if (_compactWindowLayout && !_sidebarCollapsed) ApplySidebarCollapsedState(true);
        }), handledEventsToo: true);
        var contentProperty = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl));
        EventHandler contentChanged = (_, _) =>
        {
            RefreshCompactContentViewport();
            ContentViewport.ScrollToHome();
        };
        contentProperty?.AddValueChanged(MainContent, contentChanged);
        Closed += (_, _) => contentProperty?.RemoveValueChanged(MainContent, contentChanged);
        ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        if (!_responsiveLayoutReady) return;
        var width = AppBodyGrid.ActualWidth > 0 ? AppBodyGrid.ActualWidth : Width;
        var height = AppBodyGrid.ActualHeight > 0 ? AppBodyGrid.ActualHeight : Math.Max(0, Height - 32);
        var compact = width < 760;
        _shortWindowLayout = height < 440;
        if (compact != _compactWindowLayout)
        {
            if (compact) _wideSidebarCollapsed = _sidebarCollapsed;
            _compactWindowLayout = compact;
            ApplySidebarCollapsedState(compact || _wideSidebarCollapsed);
        }
        ApplyResponsiveSidebarMetrics();
        ContentAreaBorder.Margin = new Thickness(compact || _shortWindowLayout ? 8 : IsNativePreviewOpen ? 24 : 16);
        PreviewCategoryBar.Padding = new Thickness(compact ? 8 : 20, compact ? 4 : 8, compact ? 8 : 20, compact ? 4 : 8);
        PreviewCategoryBrand.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        OverlayCard.MaxWidth = Math.Max(80, width - 16);
        OverlayCard.MaxHeight = Math.Max(80, height - 16);
        RefreshCompactContentViewport();
    }

    private void ApplyResponsiveSidebarMetrics()
    {
        if (!_responsiveLayoutReady) return;
        var drawerOpen = _compactWindowLayout && !_sidebarCollapsed;
        Grid.SetColumnSpan(SidebarAreaBorder, drawerOpen ? 2 : 1);
        SidebarAreaBorder.HorizontalAlignment = drawerOpen ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        SidebarAreaBorder.Width = drawerOpen ? (IsNativePreviewOpen ? Math.Max(192, SidebarExpandedWidth) : SidebarExpandedWidth) : double.NaN;
        SidebarColumn.Width = new GridLength(_compactWindowLayout || _sidebarCollapsed
            ? SidebarCollapsedWidth : IsNativePreviewOpen ? Math.Max(192, SidebarExpandedWidth) : SidebarExpandedWidth);
        SidebarPanel.Margin = _shortWindowLayout
            ? new Thickness(_sidebarCollapsed ? 4 : 8, 4, _sidebarCollapsed ? 4 : 8, 4)
            : _sidebarCollapsed ? new Thickness(4, 12, 4, 12) : new Thickness(12);
        SidebarHeader.Margin = new Thickness(0, 0, 0, _shortWindowLayout ? 4 : 16);
        // 状态保留在展开导航内；窄而矮的图标栏把空间留给导航与固定启动按钮。
        var showDetails = !_shortWindowLayout || !_sidebarCollapsed;
        CurrentAccountText.Visibility = CurrentVersionText.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        if (_shortWindowLayout)
        {
            CurrentAccountText.TextWrapping = CurrentVersionText.TextWrapping = TextWrapping.NoWrap;
            CurrentAccountText.TextTrimming = CurrentVersionText.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        else if (!_sidebarCollapsed)
        {
            CurrentAccountText.TextWrapping = CurrentVersionText.TextWrapping = TextWrapping.Wrap;
            CurrentAccountText.TextTrimming = CurrentVersionText.TextTrimming = TextTrimming.None;
        }
        foreach (var host in NavButtonsPanel.Children.OfType<Border>())
        {
            if (host.Child is not Button button) continue;
            button.Margin = new Thickness(0, _shortWindowLayout ? 4 : _sidebarCollapsed ? 10 : 4, 0, 0);
            button.Padding = _sidebarCollapsed ? new Thickness(0, _shortWindowLayout ? 7 : 10, 0, _shortWindowLayout ? 7 : 10)
                : new Thickness(10, _shortWindowLayout ? 7 : 10, 10, _shortWindowLayout ? 7 : 10);
        }
    }

    private void RefreshCompactContentViewport()
    {
        if (!_responsiveLayoutReady) return;
        // 百宝箱可动态创建 WebView2 HWND；它与首页都保留自己的有限视口和页内滚动。
        var legacyCompactPage = (_compactWindowLayout || _shortWindowLayout)
            && MainContent.Content != null && MainContent.Content is not (HomePage or ToolboxPage);
        ContentViewport.HorizontalScrollBarVisibility = legacyCompactPage ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        ContentViewport.VerticalScrollBarVisibility = legacyCompactPage ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        // 旧页面保留原字号与可用布局宽度，较小窗口通过滚动访问全部控件。
        // 显式有限视图尺寸避免外层 ScrollViewer 无限测量，保留内部列表虚拟化与页内滚动。
        MainContent.Width = legacyCompactPage ? Math.Max(640, ContentViewport.ActualWidth - 20) : double.NaN;
        MainContent.Height = legacyCompactPage ? Math.Max(420, ContentViewport.ActualHeight - 20) : double.NaN;
    }
}
