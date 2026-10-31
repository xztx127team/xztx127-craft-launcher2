using System.Windows;
using System.Windows.Input;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private void InitializeFeatureSearch()
    {
        LauncherFeatureSearch.Attach(this);
        LauncherFeatureSearch.LayoutEdited += (layout, complete) =>
        {
            _featureSearchPreviewLayout = layout;
            ApplyFeatureSearchVisibility();
            if (complete) FeatureSearchLayoutEdited?.Invoke(layout);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && LauncherFeatureSearch.CancelEditGesture()) { e.Handled = true; return; }
            if (e.Handled || Keyboard.Modifiers != ModifierKeys.Control || e.Key is not (Key.K or Key.F)) return;
            // Ctrl+F 在编辑框内仍交给文本/网页控件；Ctrl+K 始终是功能搜索快捷键。
            if (e.Key == Key.F && (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)) return;
            if (OverlayRoot.Visibility == Visibility.Visible) return;
            e.Handled = true;
            OpenFeatureSearch();
        };
        Deactivated += (_, _) => { LauncherFeatureSearch.Close(); LauncherFeatureSearch.CancelEditGesture(); };
        // 最大化/还原/缩放后：重新应用搜索框样式并刷新当前页（首页会重新匹配当前高度）。
        StateChanged += (_, _) => RefreshLayoutAfterResize();
        SizeChanged += (_, _) => RefreshLayoutAfterResize();
    }

    public void OpenFeatureSearch(string? query = null, string? pageKey = null)
    {
        if (OverlayRoot.Visibility == Visibility.Visible || _isWindowClosed || LauncherFeatureSearch.IsEditingLayout) return;
        LauncherFeatureSearch.Open(query, pageKey);
    }

    internal void ApplyFeatureSearchVisibility(bool? visible = null)
    {
        bool show = visible ?? _featureSearchPreviewVisible ?? ConfigService.Config.ShowFeatureSearch;
        if (!show) LauncherFeatureSearch.Close();
        // Keep the popup host alive so Ctrl+K can still search when its entry is hidden.
        LauncherFeatureSearch.SetEntryVisible(show);
        var floating = (_featureSearchPreviewStyle ?? ConfigService.Config.FeatureSearchStyle) != "Top";
        var layout = _featureSearchPreviewLayout ?? ReadFeatureSearchLayout();
        LauncherFeatureSearch.ApplyStyle(floating, layout.OpacityPercent);
        // 悬浮窗：盖在内容区左上角（不占行）；原样式：独占顶部一行、靠右。
        System.Windows.Controls.Grid.SetRowSpan(LauncherFeatureSearch, floating ? 2 : 1);
        // 跨两行时默认 Stretch 会让搜索入口在整个内容区垂直居中，必须固定贴顶。
        LauncherFeatureSearch.VerticalAlignment = VerticalAlignment.Top;
        LauncherFeatureSearch.HorizontalAlignment = HorizontalAlignment.Left;
        var host = LauncherFeatureSearch.Parent as FrameworkElement;
        var placement = FeatureSearchLayoutPolicy.Resolve(layout, floating, host?.ActualWidth ?? 0, host?.ActualHeight ?? 0);
        LauncherFeatureSearch.Width = placement.Width;
        LauncherFeatureSearch.ApplyEntryHeight(placement.Height);
        LauncherFeatureSearch.Margin = show ? new Thickness(placement.X, placement.Y, 0, floating ? 0 : 10) : new Thickness(0);
        LauncherFeatureSearch.UpdateLayoutGeometry(layout, placement, host, floating);
    }

    private FeatureSearchLayout? _featureSearchPreviewLayout;
    private string? _featureSearchPreviewStyle;
    private bool? _featureSearchPreviewVisible;
    internal event Action<FeatureSearchLayout>? FeatureSearchLayoutEdited;
    internal FeatureSearchLayout ReadFeatureSearchLayout()
    {
        var cfg = ConfigService.Config;
        return new FeatureSearchLayout(cfg.FeatureSearchWidth, cfg.FeatureSearchHeight, cfg.FeatureSearchOffsetX,
            cfg.FeatureSearchOffsetY, cfg.FeatureSearchOpacityPercent).Normalize();
    }
    internal void PreviewFeatureSearchAppearance(FeatureSearchLayout layout, string style, bool visible)
    {
        _featureSearchPreviewLayout = layout.Normalize();
        _featureSearchPreviewStyle = style;
        _featureSearchPreviewVisible = visible;
        ApplyFeatureSearchVisibility();
    }
    internal void SetFeatureSearchEditMode(bool enabled) => LauncherFeatureSearch.SetLayoutEditing(enabled);
    internal void EndFeatureSearchPreview()
    {
        LauncherFeatureSearch.SetLayoutEditing(false);
        _featureSearchPreviewLayout = null;
        _featureSearchPreviewStyle = null;
        _featureSearchPreviewVisible = null;
        ApplyFeatureSearchVisibility();
    }

    private bool _resizeRefreshQueued;
    internal void RefreshFeatureSearchForHostResize() => RefreshLayoutAfterResize();
    private void RefreshLayoutAfterResize()
    {
        if (_resizeRefreshQueued || _isWindowClosed) return;
        _resizeRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _resizeRefreshQueued = false;
            if (_isWindowClosed) return;
            LauncherFeatureSearch.CancelEditGesture();
            ApplyFeatureSearchVisibility();
            LauncherFeatureSearch.RefreshLayout();
            (MainContent?.Content as HomePage)?.RefreshForSizeChange();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
