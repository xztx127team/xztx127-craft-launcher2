using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class ModDetailPage
{
    public static readonly DependencyProperty IsCompactPreviewProperty = DependencyProperty.RegisterAttached(
        "IsCompactPreview", typeof(bool), typeof(ModDetailPage),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIsCompactPreview(DependencyObject element) => (bool)element.GetValue(IsCompactPreviewProperty);
    public static void SetIsCompactPreview(DependencyObject element, bool value) => element.SetValue(IsCompactPreviewProperty, value);

    private readonly PreviewLayoutOverrides _compactDetailValues = new();
    private bool _compactPreviewDetail;

    private void InitializePreviewDetailLayout()
    {
        SizeChanged += (_, _) => ApplyPreviewDetailLayout();
        Loaded += (_, _) => ApplyPreviewDetailLayout();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Details are shared by the launcher, server manager and wizard. Observe
        // their inherited theme scope instead of depending on one window owner.
        if (e.Property == PreviewFluentTheme.IsActiveProperty && IsInitialized)
            ApplyPreviewDetailLayout();
    }

    private void ApplyPreviewDetailLayout()
    {
        bool compact = PreviewFluentTheme.GetIsActive(this) && ActualWidth > 0 && ActualWidth < 520;
        SetCurrentValue(IsCompactPreviewProperty, compact);
        if (_compactPreviewDetail == compact) return;
        _compactPreviewDetail = compact;
        if (!compact) { _compactDetailValues.Restore(); return; }

        _compactDetailValues.TrySet(DetailHeroText, Grid.RowProperty, 1);
        _compactDetailValues.TrySet(DetailHeroText, Grid.ColumnProperty, 0);
        _compactDetailValues.TrySet(DetailHeroText, Grid.ColumnSpanProperty, 2);
        _compactDetailValues.TrySet(DetailHeroText, MarginProperty, new Thickness(0, 14, 0, 0));
        _compactDetailValues.TrySet(NameText, TextBlock.FontSizeProperty, 20d);
        _compactDetailValues.TrySet(SaveNameRow, StackPanel.OrientationProperty, Orientation.Vertical);
        _compactDetailValues.TrySet(SaveNameLabel, MarginProperty, new Thickness(0, 0, 0, 8));
        _compactDetailValues.TrySet(SaveNameCombo, WidthProperty, double.NaN);
        _compactDetailValues.TrySet(SaveNameCombo, HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        _compactDetailValues.TrySet(DetailVersionFilterHeading, Grid.ColumnSpanProperty, 2);
        _compactDetailValues.TrySet(VersionFilterPanel, Grid.ColumnSpanProperty, 2);
        _compactDetailValues.TrySet(PreviewToggleButton, Grid.RowProperty, 2);
        _compactDetailValues.TrySet(PreviewToggleButton, Grid.ColumnProperty, 0);
        _compactDetailValues.TrySet(PreviewToggleButton, Grid.ColumnSpanProperty, 2);
        _compactDetailValues.TrySet(PreviewToggleButton, MarginProperty, new Thickness(0, 8, 0, 0));
        _compactDetailValues.TrySet(PreviewToggleButton, HorizontalAlignmentProperty, HorizontalAlignment.Left);
        _compactDetailValues.TrySet(NoResultText, TextBlock.TextWrappingProperty, TextWrapping.Wrap);
    }
}
