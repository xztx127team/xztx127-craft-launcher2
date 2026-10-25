using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class BedrockPage
{
    private readonly PreviewLayoutOverrides _previewBedrockValues = new();
    private bool _compactPreviewBedrock;

    private void InitializePreviewBedrock()
    {
        SizeChanged += (_, _) => ApplyPreviewBedrockLayout();
        Loaded += (_, _) =>
        {
            _owner.NativePreviewStateChanged -= ApplyPreviewBedrockLayout;
            _owner.NativePreviewStateChanged += ApplyPreviewBedrockLayout;
            ApplyPreviewBedrockLayout();
        };
        Unloaded += (_, _) => _owner.NativePreviewStateChanged -= ApplyPreviewBedrockLayout;
    }

    private void ApplyPreviewBedrockLayout()
    {
        bool compact = _owner.IsNativePreviewOpen && ActualWidth > 0 && ActualWidth < 620;
        if (_compactPreviewBedrock == compact) return;
        _compactPreviewBedrock = compact;
        if (!compact) { _previewBedrockValues.Restore(); return; }

        _previewBedrockValues.TrySet(BedrockPageHeader, StackPanel.OrientationProperty, Orientation.Vertical);
        _previewBedrockValues.TrySet(BedrockPageSubtitle, MarginProperty, new Thickness(0, 6, 0, 0));
        _previewBedrockValues.TrySet(BedrockPageSubtitle, TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        foreach (var row in new[] { BedrockProcessActions, BedrockClientDirectoryRow, BedrockClientVersionRow,
                     BedrockServerDirectoryRow, BedrockServerChannelRow })
        {
            _previewBedrockValues.TrySet(row, StackPanel.OrientationProperty, Orientation.Vertical);
            foreach (var child in row.Children.OfType<FrameworkElement>())
            {
                _previewBedrockValues.TrySet(child, MarginProperty, new Thickness(0, 0, 0, 8));
                _previewBedrockValues.TrySet(child, HorizontalAlignmentProperty,
                    child is Button ? HorizontalAlignment.Left : HorizontalAlignment.Stretch);
                if (child is TextBox or ComboBox)
                    _previewBedrockValues.TrySet(child, WidthProperty, double.NaN);
            }
        }
        foreach (var combo in new[] { ClientFavoriteCombo, ServerFavoriteCombo, BedrockServerVersionCombo, BedrockClientChannelCombo })
        {
            _previewBedrockValues.TrySet(combo, WidthProperty, double.NaN);
            _previewBedrockValues.TrySet(combo, MinWidthProperty, 0d);
            _previewBedrockValues.TrySet(combo, HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        }
        foreach (var button in new[] { ClientMarketplaceButton, ServerMarketplaceButton })
        {
            _previewBedrockValues.TrySet(button, DockPanel.DockProperty, Dock.Bottom);
            _previewBedrockValues.TrySet(button, HorizontalAlignmentProperty, HorizontalAlignment.Left);
            _previewBedrockValues.TrySet(button, MarginProperty, new Thickness(0, 8, 0, 0));
        }
    }
}
