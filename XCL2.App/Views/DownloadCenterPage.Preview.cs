using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class DownloadCenterPage
{
    public static readonly DependencyProperty IsNarrowPreviewProperty = DependencyProperty.RegisterAttached(
        "IsNarrowPreview", typeof(bool), typeof(DownloadCenterPage),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIsNarrowPreview(DependencyObject element) => (bool)element.GetValue(IsNarrowPreviewProperty);
    public static void SetIsNarrowPreview(DependencyObject element, bool value) => element.SetValue(IsNarrowPreviewProperty, value);

    private const double PreviewMinimumDownloadHeight = 600;
    private const double PreviewMinimumDownloadWidth = 320;
    private readonly PreviewLayoutOverrides _compactPreviewValues = new();
    private readonly PreviewLayoutOverrides _shortPreviewValues = new();
    private readonly PreviewLayoutOverrides _narrowPreviewValues = new();
    private readonly PreviewLayoutOverrides _minimumWidthPreviewValues = new();
    private bool _compactPreviewDownloads;
    private bool _narrowPreviewDownloads;
    private bool _shortPreviewDownloads;
    private bool _minimumWidthPreviewDownloads;
    private ScrollViewer? _previewDownloadScroll;
    private double _downloadLayoutHeightValueBeforePreview = double.NaN;
    private double _downloadLayoutWidthValueBeforePreview = double.NaN;

    private void InitializePreviewDownloads()
    {
        SizeChanged += (_, _) => ApplyPreviewDownloadLayout();
        Loaded += (_, _) =>
        {
            _owner.NativePreviewStateChanged -= ApplyPreviewDownloadLayout;
            _owner.NativePreviewStateChanged += ApplyPreviewDownloadLayout;
            ApplyPreviewDownloadLayout();
        };
        Unloaded += (_, _) => _owner.NativePreviewStateChanged -= ApplyPreviewDownloadLayout;
    }

    private void ApplyPreviewDownloadLayout()
    {
        ApplyPreviewDownloadViewport();
        ApplyNarrowPreviewDownloadLayout();
        // On a narrow preview window, give the results their full width. Every
        // category remains reachable in a horizontal scrolling row.
        bool compact = _owner.IsNativePreviewOpen && ActualWidth > 0 && ActualWidth < 720;
        if (_compactPreviewDownloads == compact) return;
        _compactPreviewDownloads = compact;
        if (!compact)
        {
            _compactPreviewValues.Restore();
            return;
        }
        SetCompactLayoutValue(DownloadCategoryColumn, ColumnDefinition.WidthProperty, new GridLength(0));
        SetCompactLayoutValue(DownloadCategoryGap, ColumnDefinition.WidthProperty, new GridLength(0));
        SetCompactLayoutValue(ListRootLeft, Grid.RowProperty, 0);
        SetCompactLayoutValue(ListRootLeft, Grid.ColumnSpanProperty, 3);
        SetCompactLayoutValue(ListRootRight, Grid.ColumnProperty, 0);
        SetCompactLayoutValue(ListRootRight, Grid.ColumnSpanProperty, 3);
        SetCompactLayoutValue(ListRootLeft, MarginProperty, new Thickness(0, 0, 0, 12));
        SetCompactLayoutValue(DownloadCategories, StackPanel.OrientationProperty, Orientation.Horizontal);
        SetCompactLayoutValue(DownloadCategoryHeading, VisibilityProperty, Visibility.Collapsed);
        SetCompactLayoutValue(DownloadCategoryScroll, ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        SetCompactLayoutValue(DownloadCategoryScroll, ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        foreach (var element in DownloadCategories.Children.OfType<FrameworkElement>())
            SetCompactLayoutValue(element, MarginProperty, new Thickness(0, 0, 8, 0));

        // Fixed columns become stacked rows in the narrow preview. Restoring the
        // captured local values also restores unset properties and any bindings.
        SetCompactLayoutValue(VersionFilterTypeColumn, ColumnDefinition.WidthProperty, new GridLength(0));
        SetCompactLayoutValue(VersionSearchBox, Grid.ColumnSpanProperty, 2);
        SetCompactLayoutValue(VersionSearchBox, MarginProperty, new Thickness(0, 0, 0, 10));
        SetCompactLayoutValue(VersionTypeCombo, Grid.RowProperty, 1);
        SetCompactLayoutValue(VersionTypeCombo, Grid.ColumnProperty, 0);
        SetCompactLayoutValue(VersionTypeCombo, Grid.ColumnSpanProperty, 2);
        SetCompactLayoutValue(MapFilterVersionColumn, ColumnDefinition.WidthProperty, new GridLength(12));
        SetCompactLayoutValue(MapSearchBox, Grid.ColumnSpanProperty, 3);
        SetCompactLayoutValue(MapSearchBox, MarginProperty, new Thickness(0, 0, 0, 10));
        SetCompactLayoutValue(MapGameVersionBox, Grid.RowProperty, 1);
        SetCompactLayoutValue(MapGameVersionBox, Grid.ColumnProperty, 0);
        SetCompactLayoutValue(MapGameVersionBox, MarginProperty, new Thickness(0));
        SetCompactLayoutValue(MapSearchActionButton, Grid.RowProperty, 1);
    }

    private void SetCompactLayoutValue(DependencyObject element, DependencyProperty property, object value)
        => _compactPreviewValues.TrySet(element, property, value);

    private void ApplyNarrowPreviewDownloadLayout()
    {
        bool narrow = _owner.IsNativePreviewOpen && ActualWidth > 0 && ActualWidth < 420;
        SetCurrentValue(IsNarrowPreviewProperty, narrow);
        if (_narrowPreviewDownloads == narrow) return;
        _narrowPreviewDownloads = narrow;
        if (!narrow) { _narrowPreviewValues.Restore(); return; }
        _narrowPreviewValues.TrySet(VersionDownloadTitle, Grid.ColumnSpanProperty, 3);
        _narrowPreviewValues.TrySet(VersionDownloadTitle, MarginProperty, new Thickness(0, 0, 0, 8));
        _narrowPreviewValues.TrySet(SourceCombo, Grid.RowProperty, 1);
        _narrowPreviewValues.TrySet(SourceCombo, Grid.ColumnProperty, 0);
        _narrowPreviewValues.TrySet(SourceCombo, Grid.ColumnSpanProperty, 2);
        _narrowPreviewValues.TrySet(SourceCombo, WidthProperty, double.NaN);
        _narrowPreviewValues.TrySet(VersionRefreshButton, Grid.RowProperty, 1);
        _narrowPreviewValues.TrySet(MapDownloadTitle, Grid.ColumnSpanProperty, 2);
        _narrowPreviewValues.TrySet(MapDataSourceLabel, Grid.RowProperty, 1);
        _narrowPreviewValues.TrySet(MapDataSourceLabel, Grid.ColumnProperty, 0);
        _narrowPreviewValues.TrySet(MapDataSourceLabel, Grid.ColumnSpanProperty, 2);
        _narrowPreviewValues.TrySet(MapDataSourceLabel, MarginProperty, new Thickness(0, 4, 0, 0));
    }

    private void ApplyPreviewDownloadViewport()
    {
        // Keep a finite height for virtualized result lists. A short window gets
        // one outer scroll surface, rather than filters reducing the list to zero.
        bool shortWindow = _owner.IsNativePreviewOpen && ActualHeight > 0 && ActualHeight < PreviewMinimumDownloadHeight;
        bool minimumWidth = _owner.IsNativePreviewOpen && ActualWidth > 0 && ActualWidth < PreviewMinimumDownloadWidth;
        if (shortWindow)
        {
            if (!_shortPreviewDownloads)
                _downloadLayoutHeightValueBeforePreview = DownloadLayout.Height;
            var height = double.IsFinite(_downloadLayoutHeightValueBeforePreview)
                ? Math.Max(PreviewMinimumDownloadHeight, _downloadLayoutHeightValueBeforePreview) : PreviewMinimumDownloadHeight;
            shortWindow = _shortPreviewValues.TrySet(DownloadLayout, HeightProperty, height);
        }
        else if (_shortPreviewDownloads)
        {
            _shortPreviewValues.Restore();
            _downloadLayoutHeightValueBeforePreview = double.NaN;
        }
        _shortPreviewDownloads = shortWindow;

        // A high UI scale can leave less than the minimum usable content width.
        // Only that case gets horizontal scrolling; ordinary preview widths are
        // measured against their real viewport without an outer width clamp.
        if (minimumWidth)
        {
            if (!_minimumWidthPreviewDownloads)
                _downloadLayoutWidthValueBeforePreview = DownloadLayout.Width;
            var width = double.IsFinite(_downloadLayoutWidthValueBeforePreview)
                ? Math.Max(PreviewMinimumDownloadWidth, _downloadLayoutWidthValueBeforePreview) : PreviewMinimumDownloadWidth;
            minimumWidth = _minimumWidthPreviewValues.TrySet(DownloadLayout, WidthProperty, width);
        }
        else if (_minimumWidthPreviewDownloads)
        {
            _minimumWidthPreviewValues.Restore();
            _downloadLayoutWidthValueBeforePreview = double.NaN;
        }
        _minimumWidthPreviewDownloads = minimumWidth;

        if (shortWindow || minimumWidth)
        {
            _previewDownloadScroll ??= new ScrollViewer { CanContentScroll = false };
            _previewDownloadScroll.VerticalScrollBarVisibility = shortWindow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            _previewDownloadScroll.HorizontalScrollBarVisibility = minimumWidth ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            if (!ReferenceEquals(Content, _previewDownloadScroll))
            {
                Content = null;
                _previewDownloadScroll.Content = DownloadLayout;
                Content = _previewDownloadScroll;
            }
        }
        else if (_previewDownloadScroll != null && ReferenceEquals(Content, _previewDownloadScroll))
        {
            _previewDownloadScroll.Content = null;
            Content = null;
            Content = DownloadLayout;
        }
    }
}
