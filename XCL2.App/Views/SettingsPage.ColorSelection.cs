using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    // 只遍历真正的逻辑设置控件，不包含模板临时生成的输入框/滚动条/列表行。
    // 下拉打开、收起、主题重套和滚动虚拟化不会增删指纹中的“设置”。
    private static IEnumerable<T> FindSettingsControls<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in FindSettingsControls<T>(child)) yield return descendant;
        }
    }

    private bool IsBrowsingSettingOptions()
        => FindSettingsControls<ComboBox>(this).Any(combo => combo.IsDropDownOpen);

    private void CompletePresetThemeSelection()
    {
        if (UiSkinCombo.SelectedItem is ComboBoxItem { Tag: string skin } && skin != ThemeService.SkinCustom
            && !ThemeSelectionPolicy.IsSameChoice(_committedUiSkin, _committedCustomAccentColor, skin, _committedCustomAccentColor))
            CommitThemeChoice(skin);
    }

    private static string? NormalizeAccentColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value.Trim())!;
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        catch { return null; }
    }

    private static string NormalizeBackgroundTime(string? value, string fallback)
    {
        if (!TimeSpan.TryParse(value?.Trim(), out var time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            time = TimeSpan.TryParse(fallback, out var saved) && saved >= TimeSpan.Zero && saved < TimeSpan.FromDays(1) ? saved : TimeSpan.Zero;
        return $"{time.Hours:D2}:{time.Minutes:D2}";
    }

    private void CommitThemeChoice(string skin, Color? accent = null, bool showReplacement = true)
    {
        var nextColor = accent.HasValue ? $"#{accent.Value.R:X2}{accent.Value.G:X2}{accent.Value.B:X2}" : _committedCustomAccentColor;
        var changed = !ThemeSelectionPolicy.IsSameChoice(_committedUiSkin, _committedCustomAccentColor, skin, nextColor);
        if (changed && showReplacement && ParseRotationMode(UiSkinRotationModeCombo) == AppearanceRotationMode.Fixed)
        {
            var previous = string.Equals(_committedUiSkin, ThemeService.SkinCustom, StringComparison.Ordinal)
                ? $"自定义颜色（{_committedCustomAccentColor ?? "#4C9AFF"}）"
                : ThemeService.GetDisplayName(_committedUiSkin);
            ToastService.Show($"之前已经选择了一个颜色，将自动取消上一个颜色。你上一个颜色是：{previous}。",
                ToastService.ToastKind.Info, 6);
        }
        _committedUiSkin = skin;
        _committedCustomAccentColor = nextColor;
        _customThemeSelectionPending = false;

        if (ParseRotationMode(UiSkinRotationModeCombo) == AppearanceRotationMode.Fixed)
        {
            // 固定模式下列表与当前配色保持一致；Custom 只在调色区域中编辑。
            _pendingSkinCandidates = skin == ThemeService.SkinCustom ? new() : new() { skin };
            RunWithoutDirtyTracking(RefreshSkinCandidateUi);
        }
        if (accent.HasValue) ThemeService.ApplyCustomAccent(accent.Value);
        else
        {
            var cfg = _owner.ConfigService.Config;
            ThemeService.ApplyForCurrentState(cfg.GuestModeEnabled, skin, cfg.IsDarkMode, _committedCustomAccentColor);
        }
        if (changed && !UiSkinCombo.IsDropDownOpen) OnSettingsEdited();
    }

    private void CommitCustomAccent(Color color, bool showSuccess = false)
    {
        _accentApplyDebounceTimer?.Stop();
        _accentInputPending = false;
        RunWithoutDirtyTracking(() =>
        {
            SetAccentPickerColor(color);
            CustomAccentColorBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            SelectComboByTag(UiSkinCombo, ThemeService.SkinCustom);
        });
        UpdateCustomThemePanelVisibility();
        CommitThemeChoice(ThemeService.SkinCustom, color);
        if (showSuccess) ToastService.ShowSuccess("已预览自定义主题颜色，保存设置后会持久生效");
    }

    private void CommitPendingAccentInput()
    {
        _accentApplyDebounceTimer?.Stop();
        if (!_accentInputPending) return;
        if ((UiSkinCombo.SelectedItem as ComboBoxItem)?.Tag as string != ThemeService.SkinCustom) return;
        var hex = NormalizeAccentColor(CustomAccentColorBox.Text);
        if (hex != null) CommitCustomAccent((Color)ColorConverter.ConvertFromString(hex)!);
    }
}
