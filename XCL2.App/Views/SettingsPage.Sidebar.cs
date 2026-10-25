using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private readonly Dictionary<string, TextBox> _sidebarLabelControls = new(StringComparer.OrdinalIgnoreCase);

    private void InitializeSidebarSettings(AppConfig config)
    {
        SidebarPlacementCombo.SelectedIndex = config.SidebarPlacement == SidebarPlacement.Right ? 1 : 0;
        SidebarAutomaticWidthCheck.IsChecked = config.SidebarExpandedWidth == 0;
        SidebarWidthSlider.Value = config.SidebarExpandedWidth > 0 ? config.SidebarExpandedWidth : 180;
        SidebarAutomaticFontSizeCheck.IsChecked = config.SidebarLabelFontSize == 0;
        SidebarFontSizeSlider.Value = config.SidebarLabelFontSize > 0 ? config.SidebarLabelFontSize : 14;
        SidebarShowIconsCheck.IsChecked = config.SidebarShowNavigationIcons;
        SidebarStartCollapsedCheck.IsChecked = config.SidebarStartCollapsed;
        foreach (var (key, resourceKey, defaultText) in SidebarPreferences.Labels)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 80, MaxWidth = 160 });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MaxWidth = 260 });
            var label = new TextBlock
            {
                Text = defaultText, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0)
            };
            if (resourceKey != null) label.SetResourceReference(TextBlock.TextProperty, resourceKey);
            var input = new TextBox
            {
                Name = "SidebarLabel_" + key,
                Text = config.SidebarLabels.TryGetValue(key, out var custom) ? SidebarPreferences.NormalizeLabel(custom) : "",
                MaxLength = SidebarPreferences.MaximumLabelLength * 2,
                ToolTip = $"{defaultText}的显示文字；留空使用原名称（最多 {SidebarPreferences.MaximumLabelLength} 个字符）",
                Padding = new Thickness(8, 5, 8, 5)
            };
            AutomationProperties.SetName(input, defaultText + "的导航文字");
            Grid.SetColumn(input, 1);
            row.Children.Add(label);
            row.Children.Add(input);
            RegisterName(input.Name, input);
            _sidebarLabelControls[key] = input;
            SidebarLabelsPanel.Children.Add(row);
        }
        RefreshSidebarDimensionControls();
    }

    private void SaveSidebarSettings(AppConfig config)
    {
        config.SidebarPlacement = SidebarPlacementCombo.SelectedIndex == 1 ? SidebarPlacement.Right : SidebarPlacement.Left;
        config.SidebarExpandedWidth = SidebarAutomaticWidthCheck.IsChecked == true ? 0 : (int)Math.Round(SidebarWidthSlider.Value);
        config.SidebarLabelFontSize = SidebarAutomaticFontSizeCheck.IsChecked == true ? 0 : SidebarFontSizeSlider.Value;
        config.SidebarShowNavigationIcons = SidebarShowIconsCheck.IsChecked == true;
        config.SidebarStartCollapsed = SidebarStartCollapsedCheck.IsChecked == true;
        // Preserve future/plugin labels that this version does not expose in the editor.
        var labels = new Dictionary<string, string>(config.SidebarLabels, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, input) in _sidebarLabelControls)
        {
            var text = SidebarPreferences.NormalizeLabel(input.Text);
            if (input.Text != text) RunWithoutDirtyTracking(() => input.Text = text);
            if (text.Length == 0) labels.Remove(key);
            else labels[key] = text;
        }
        config.SidebarLabels = labels;
    }

    private void SidebarDimensionDefault_Changed(object sender, RoutedEventArgs e)
    {
        RefreshSidebarDimensionControls();
        if (!_suppressDirtyTracking) OnSettingsEdited();
    }

    private void RefreshSidebarDimensionControls()
    {
        if (SidebarWidthSlider == null || SidebarFontSizeSlider == null) return;
        SidebarWidthSlider.IsEnabled = SidebarAutomaticWidthCheck.IsChecked != true;
        SidebarFontSizeSlider.IsEnabled = SidebarAutomaticFontSizeCheck.IsChecked != true;
    }

    private void ResetSidebarPreferences_Click(object sender, RoutedEventArgs e)
    {
        RunWithoutDirtyTracking(() =>
        {
            SidebarPlacementCombo.SelectedIndex = 0;
            SidebarAutomaticWidthCheck.IsChecked = true;
            SidebarWidthSlider.Value = 180;
            SidebarAutomaticFontSizeCheck.IsChecked = true;
            SidebarFontSizeSlider.Value = 14;
            SidebarShowIconsCheck.IsChecked = true;
            SidebarStartCollapsedCheck.IsChecked = false;
            foreach (var input in _sidebarLabelControls.Values) input.Text = "";
        });
        RefreshSidebarDimensionControls();
        OnSettingsEdited();
    }
}
