using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private FeatureSearchLayout _pendingFeatureSearchLayout = FeatureSearchLayout.Default;
    private bool _featureSearchControlsSync;

    private void InitializeFeatureSearchSettings(AppConfig cfg)
    {
        _pendingFeatureSearchLayout = _owner.ReadFeatureSearchLayout();
        SyncFeatureSearchControls();
        FeatureSearchEditCheck.IsChecked = false;
        FeatureSearchEditCheck.IsEnabled = cfg.ShowFeatureSearch;
        _owner.FeatureSearchLayoutEdited += FeatureSearchLayoutEdited;
        Unloaded += (_, _) =>
        {
            _owner.FeatureSearchLayoutEdited -= FeatureSearchLayoutEdited;
            _owner.EndFeatureSearchPreview();
        };
    }

    private void ShowFeatureSearchCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressDirtyTracking) return;
        FeatureSearchEditCheck.IsEnabled = ShowFeatureSearchCheck.IsChecked == true;
        if (ShowFeatureSearchCheck.IsChecked != true) FeatureSearchEditCheck.IsChecked = false;
        PreviewFeatureSearchSettings();
    }

    private void FeatureSearchEditCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressDirtyTracking) return;
        PreviewFeatureSearchSettings();
        _owner.SetFeatureSearchEditMode(FeatureSearchEditCheck.IsChecked == true && ShowFeatureSearchCheck.IsChecked == true);
    }

    private void PreviewFeatureSearchSettings()
    {
        var style = (FeatureSearchStyleCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? _owner.ConfigService.Config.FeatureSearchStyle;
        _owner.PreviewFeatureSearchAppearance(_pendingFeatureSearchLayout, style, ShowFeatureSearchCheck.IsChecked == true);
    }

    private void FeatureSearchLayoutEdited(FeatureSearchLayout layout)
    {
        layout = layout.Normalize();
        if (_pendingFeatureSearchLayout == layout) return;
        _pendingFeatureSearchLayout = layout;
        SyncFeatureSearchControls();
        OnSettingsEdited(); // 拖动中仅主窗口预览，松开或取消后才结算一次设置修改。
    }

    private void SyncFeatureSearchControls()
    {
        _featureSearchControlsSync = true;
        try
        {
            FeatureSearchWidthSlider.Value = _pendingFeatureSearchLayout.Width;
            FeatureSearchHeightSlider.Value = _pendingFeatureSearchLayout.Height;
            FeatureSearchOpacitySlider.Value = _pendingFeatureSearchLayout.OpacityPercent;
            FeatureSearchOffsetXBox.Text = _pendingFeatureSearchLayout.OffsetX.ToString("0.##", CultureInfo.InvariantCulture);
            FeatureSearchOffsetYBox.Text = _pendingFeatureSearchLayout.OffsetY.ToString("0.##", CultureInfo.InvariantCulture);
            UpdateFeatureSearchValueLabels();
        }
        finally { _featureSearchControlsSync = false; }
    }

    private void UpdateFeatureSearchValueLabels()
    {
        FeatureSearchWidthText.Text = $"{_pendingFeatureSearchLayout.Width:0.##} px";
        FeatureSearchHeightText.Text = $"{_pendingFeatureSearchLayout.Height:0.##} px";
        FeatureSearchOpacityText.Text = $"{_pendingFeatureSearchLayout.OpacityPercent:0}%";
    }

    private void FeatureSearchAppearanceSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressDirtyTracking || _featureSearchControlsSync) return;
        _pendingFeatureSearchLayout = (_pendingFeatureSearchLayout with
        {
            Width = FeatureSearchWidthSlider.Value, Height = FeatureSearchHeightSlider.Value,
            OpacityPercent = FeatureSearchOpacitySlider.Value
        }).Normalize();
        UpdateFeatureSearchValueLabels();
        PreviewFeatureSearchSettings();
    }

    private void FeatureSearchOffset_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppressDirtyTracking || _featureSearchControlsSync) return;
        if (!SettingsInputValidation.TryNumber(FeatureSearchOffsetXBox.Text, -10000, 10000, false, out var x)
            || !SettingsInputValidation.TryNumber(FeatureSearchOffsetYBox.Text, -10000, 10000, false, out var y)) return;
        // Invalid/out-of-range text stays in its field for the shared validator; never clamp it into a different saved value.
        _pendingFeatureSearchLayout = _pendingFeatureSearchLayout with { OffsetX = x, OffsetY = y };
        PreviewFeatureSearchSettings();
    }

    private void ResetFeatureSearchAppearance_Click(object sender, RoutedEventArgs e)
    {
        _owner.SetFeatureSearchEditMode(false);
        FeatureSearchEditCheck.IsChecked = false;
        _pendingFeatureSearchLayout = FeatureSearchLayout.Default;
        SyncFeatureSearchControls();
        PreviewFeatureSearchSettings();
        OnSettingsEdited();
    }

    private void SaveFeatureSearchSettings(AppConfig cfg)
    {
        // Read the same validated text that is visible to the user, instead of silently saving a stale preview.
        if (ShowFeatureSearchCheck.IsChecked == true)
        {
            SettingsInputValidation.TryNumber(FeatureSearchOffsetXBox.Text, -10000, 10000, false, out var x);
            SettingsInputValidation.TryNumber(FeatureSearchOffsetYBox.Text, -10000, 10000, false, out var y);
            _pendingFeatureSearchLayout = _pendingFeatureSearchLayout with { OffsetX = x, OffsetY = y };
        }
        else _pendingFeatureSearchLayout = _pendingFeatureSearchLayout with { OffsetX = cfg.FeatureSearchOffsetX, OffsetY = cfg.FeatureSearchOffsetY };
        _pendingFeatureSearchLayout = _pendingFeatureSearchLayout.Normalize();
        cfg.FeatureSearchStyle = (FeatureSearchStyleCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Floating";
        cfg.FeatureSearchWidth = _pendingFeatureSearchLayout.Width;
        cfg.FeatureSearchHeight = _pendingFeatureSearchLayout.Height;
        cfg.FeatureSearchOffsetX = _pendingFeatureSearchLayout.OffsetX;
        cfg.FeatureSearchOffsetY = _pendingFeatureSearchLayout.OffsetY;
        cfg.FeatureSearchOpacityPercent = _pendingFeatureSearchLayout.OpacityPercent;
        UpdateFeatureSearchValueLabels();
        PreviewFeatureSearchSettings();
    }
}
