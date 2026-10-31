using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private readonly Dictionary<FrameworkElement, TextBlock> _validationHints = new();
    private readonly Dictionary<FrameworkElement, string> _validationErrors = new();
    private string _lastValidationNotice = "";
    private bool _settingsWriteCommitted;

    private void SetSettingError(FrameworkElement input, string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            _validationErrors.Remove(input);
            if (_validationHints.TryGetValue(input, out var existing)) existing.Visibility = Visibility.Collapsed;
            AutomationProperties.SetHelpText(input, "");
            return;
        }
        _validationErrors[input] = message;
        AutomationProperties.SetHelpText(input, message);
        if (!_validationHints.TryGetValue(input, out var hint))
        {
            hint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 4), MaxWidth = 520 };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            if (input.Parent is Panel parent)
            {
                // The hint is real layout content, not an adorner that disappears on scroll/template rebuild.
                var index = parent.Children.IndexOf(input);
                var host = new StackPanel { Margin = input.Margin, HorizontalAlignment = input.HorizontalAlignment,
                    VerticalAlignment = input.VerticalAlignment };
                Grid.SetRow(host, Grid.GetRow(input)); Grid.SetColumn(host, Grid.GetColumn(input));
                Grid.SetRowSpan(host, Grid.GetRowSpan(input)); Grid.SetColumnSpan(host, Grid.GetColumnSpan(input));
                DockPanel.SetDock(host, DockPanel.GetDock(input));
                if (parent is StackPanel { Orientation: Orientation.Horizontal } || parent is WrapPanel)
                    host.MaxWidth = double.IsNaN(input.Width) ? Math.Max(160, input.ActualWidth) : Math.Max(120, input.Width);
                parent.Children.RemoveAt(index);
                input.Margin = new Thickness(0);
                host.Children.Add(input); host.Children.Add(hint); parent.Children.Insert(index, host);
            }
            else if (input.Parent is Decorator decorator && ReferenceEquals(decorator.Child, input))
            {
                decorator.Child = null;
                var host = new StackPanel(); host.Children.Add(input); host.Children.Add(hint); decorator.Child = host;
            }
            else if (input.Parent is ContentControl content && ReferenceEquals(content.Content, input))
            {
                content.Content = null;
                var host = new StackPanel(); host.Children.Add(input); host.Children.Add(hint); content.Content = host;
            }
            else
            {
                // Unusual templated hosts still get persistent red text beside the nearest settings frame.
                DependencyObject? ancestor = LogicalTreeHelper.GetParent(input);
                while (ancestor != null && ancestor is not Panel) ancestor = LogicalTreeHelper.GetParent(ancestor);
                (ancestor as Panel ?? SettingsRootPanel).Children.Add(hint);
            }
            _validationHints[input] = hint;
        }
        hint.Text = message + " 当前内容尚未保存。";
        hint.Visibility = Visibility.Visible;
    }

    private void LocateSettingError()
    {
        var target = _validationErrors.Keys.FirstOrDefault();
        if (target == null) return;
        // A user-hidden row can still contain an invalid active setting. Temporarily reveal
        // only through the existing F10 policy so “定位问题” always leads to an editable field.
        bool hiddenByPreference = false;
        for (DependencyObject? node = target; node != null; node = LogicalTreeHelper.GetParent(node))
            if (node is FrameworkElement element && _settingsHiddenElements.Contains(element)) hiddenByPreference = true;
        if (hiddenByPreference)
        {
            SettingsVisibilityService.TemporaryRevealActive = true;
            ApplySettingsItemVisibility();
            ToastService.ShowInfo("已临时显示包含问题的隐藏设置；修正后可按 F10 恢复隐藏。");
        }
        // Expand ancestors only. No value assignment, normalization, or validation runs during navigation.
        for (DependencyObject? node = target; node != null; node = LogicalTreeHelper.GetParent(node))
            if (node is Expander expander) expander.IsExpanded = true;
        Dispatcher.BeginInvoke(new Action(() => { target.BringIntoView(); target.Focus(); }), DispatcherPriority.Loaded);
    }

    private void NotifySettingErrors(bool focusFirst)
    {
        _hasUnsavedChanges = true;
        var message = $"有 {_validationErrors.Count} 项设置需要修正，尚未保存。";
        StatusText.Text = message + " 请查看输入框下方红字，或点击“定位问题”。";
        var signature = string.Join("\n", _validationErrors.Select(pair => pair.Key.Name + ":" + pair.Value));
        if (focusFirst || signature != _lastValidationNotice)
        {
            ToastService.ShowWarning(message + " 输入内容已保留，请按红字提示修改。");
            ToastService.ShowActionPrompt(message, "定位问题", LocateSettingError,
                hint: "输入内容会保留，不会在滚动或失去焦点时自动修改。", key: "settings-validation", dismissible: true);
            _lastValidationNotice = signature;
        }
        if (focusFirst) LocateSettingError();
    }

    private bool IsPreciseSettingActive(Slider slider, TextBox editor)
    {
        if (!PreciseSliderInputsEnabled || !slider.IsEnabled || !editor.IsEnabled) return false;
        for (DependencyObject? node = editor; node != null; node = LogicalTreeHelper.GetParent(node))
            if (node is UIElement element && element.Visibility != Visibility.Visible) return false;
        // A closed Expander has visible logical content and must retain validation of an edited value.
        return true;
    }

    private bool ValidateSettingsDraft(bool focusFirst = false)
    {
        var hadErrors = _validationErrors.Count > 0;
        foreach (var input in _validationErrors.Keys.ToArray()) SetSettingError(input, null);
        ValidateExperienceSettings();
        ValidateAdditionalSettings();
        foreach (var (slider, editor) in _preciseSliderEditors)
        {
            if (!IsPreciseSettingActive(slider, editor)) continue;
            if (!SettingsInputValidation.TryNumber(editor.Text, slider.Minimum, slider.Maximum, false, out _))
                SetSettingError(editor, $"请输入 {slider.Minimum}–{slider.Maximum} 之间的有效数值。");
        }
        if (_validationErrors.Count != 0) { NotifySettingErrors(focusFirst); return false; }
        _lastValidationNotice = "";
        if (hadErrors) StatusText.Text = "设置问题已修正，等待保存。";
        ToastService.DismissActionPrompt("settings-validation");
        return true;
    }

    private void ValidateAdditionalSettings()
    {
        void Number(TextBox box, string label, double min, double max, bool integer = true, bool active = true)
        {
            if (active && !SettingsInputValidation.TryNumber(box.Text, min, max, integer, out _))
                SetSettingError(box, $"{label}需为 {min}–{max} 之间的{(integer ? "整数" : "数值")}。");
        }
        Number(WidthBox, "游戏窗口宽度", 1, int.MaxValue);
        Number(HeightBox, "游戏窗口高度", 1, int.MaxValue);
        Number(ThreadCountBox, "下载线程数", 1, 64, active: MultiThreadDownloadCheck.IsChecked == true);
        Number(SpeedLimitBox, "下载速度限制", 0, int.MaxValue);
        Number(DownloadListBatchSizeBox, "每次加载条数", 10, 100);
        Number(AccountTokenGraceDaysBox, "账号宽限天数", 0, int.MaxValue);
        Number(FeatureSearchOffsetXBox, "搜索框水平偏移", -10000, 10000, false, ShowFeatureSearchCheck.IsChecked == true);
        Number(FeatureSearchOffsetYBox, "搜索框垂直偏移", -10000, 10000, false, ShowFeatureSearchCheck.IsChecked == true);
        Number(ScheduledBackupIntervalBox, "备份间隔（小时）", 1, 720, active: ScheduledBackupCheck.IsChecked == true);
        Number(ScheduledBackupRetentionBox, "备份保留数", 1, 100, active: ScheduledBackupCheck.IsChecked == true);
        Number(TouchScaleBox, "触控按钮缩放", 0.5, 2, false, TouchModeCheck.IsChecked == true);
        Number(TouchOpacityBox, "触控按钮不透明度", 20, 100, false, TouchModeCheck.IsChecked == true);
        Number(TouchSensitivityBox, "触控灵敏度", 0.4, 4, false, TouchModeCheck.IsChecked == true);
        var rotation = ParseRotationMode(BackgroundRotationModeCombo);
        if (BackgroundRotationModeCombo.IsEnabled && rotation == AppearanceRotationMode.CustomInterval)
            Number(BackgroundRotationIntervalBox, "背景轮换间隔（分钟）", 1, int.MaxValue);
        if (BackgroundRotationModeCombo.IsEnabled && rotation == AppearanceRotationMode.FixedTime
            && !SettingsInputValidation.TryClock(BackgroundRotationFixedTimeBox.Text, out _))
            SetSettingError(BackgroundRotationFixedTimeBox, "请输入 24 小时时刻，例如 08:30 或 23:59。");
        if (_accentInputPending && (UiSkinCombo.SelectedItem as ComboBoxItem)?.Tag as string == ThemeService.SkinCustom
            && NormalizeAccentColor(CustomAccentColorBox.Text) == null)
            SetSettingError(CustomAccentColorBox, "请输入有效颜色，例如 #4C9AFF。");
    }

    private void CommitValidatedPreciseSliders()
    {
        foreach (var (slider, editor) in _preciseSliderEditors)
        {
            if (!IsPreciseSettingActive(slider, editor) || !SettingsInputValidation.TryNumber(editor.Text, slider.Minimum, slider.Maximum, false, out var value)) continue;
            if (slider.IsSnapToTickEnabled && slider.TickFrequency > 0)
                value = slider.Minimum + Math.Round((value - slider.Minimum) / slider.TickFrequency) * slider.TickFrequency;
            slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
            editor.Text = slider.Value.ToString("0.###", CultureInfo.CurrentCulture);
        }
    }
}
