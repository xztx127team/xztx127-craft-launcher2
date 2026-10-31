using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private sealed record ExperienceEditor(PropertyInfo Property, ExperiencePreferenceAttribute Preference, FrameworkElement Control);
    private readonly List<ExperienceEditor> _experienceEditors = new();
    private readonly List<(Slider Slider, TextBox Editor)> _preciseSliderEditors = new();

    private void InitializeExperienceSettings(AppConfig cfg)
    {
        var section = new StackPanel();
        var note = new TextBlock { Text = "低频功能在对应页面菜单中使用；这里仅设置行为。新设置遵循本页的保存、自动保存与撤销规则。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), FontSize = 12 };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        section.Children.Add(note);
        var properties = typeof(AppConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => (Property: property, Preference: property.GetCustomAttribute<ExperiencePreferenceAttribute>()))
            .Where(item => item.Preference != null && item.Property.CanRead && item.Property.CanWrite && IsExperienceType(item.Property.PropertyType))
            .OrderBy(item => item.Preference!.Group, StringComparer.Ordinal).ThenBy(item => item.Property.MetadataToken);
        foreach (var group in properties.GroupBy(item => item.Preference!.Group))
        {
            var body = new StackPanel { Margin = new Thickness(8, 8, 8, 4) };
            foreach (var (property, preference) in group)
            {
                var attribute = preference!;
                var control = CreateExperienceControl(property, attribute, property.GetValue(cfg));
                control.Name = "Experience_" + property.Name;
                control.ToolTip = attribute.Description.Length == 0 ? attribute.Label : attribute.Description;
                if (control is CheckBox) body.Children.Add(control);
                else
                {
                    body.Children.Add(new TextBlock { Text = attribute.Label + (attribute.Unit.Length == 0 ? "" : $"（{attribute.Unit}）"), Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap });
                    body.Children.Add(control);
                }
                if (attribute.Description.Length > 0)
                {
                    var description = new TextBlock { Text = attribute.Description, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 10) };
                    description.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    body.Children.Add(description);
                }
                if (property.Name == nameof(AppConfig.EnableAprilFoolsEasterEggs) && control is CheckBox aprilFools)
                    InitializeAprilFoolsCloseControls(aprilFools, body);
                _experienceEditors.Add(new ExperienceEditor(property, attribute, control));
                if (property.Name == nameof(AppConfig.Ux50InputMemoryUnits) && control is CheckBox units)
                { units.Checked += MemoryOptions_Changed; units.Unchecked += MemoryOptions_Changed; }
                if (property.Name == nameof(AppConfig.Ux50InputPreciseSliders) && control is CheckBox precise)
                {
                    precise.Checked += (_, _) => RefreshPreciseSliderEditors(validateDraft: true);
                    precise.Unchecked += (_, _) => RefreshPreciseSliderEditors(validateDraft: true);
                }
            }
            section.Children.Add(new Expander { Header = group.Key, IsExpanded = false, Content = body, Margin = new Thickness(0, 0, 0, 8) });
        }
        var clear = new Button { Content = "清除文件选择目录记录", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 4, 0, 0) };
        clear.Click += (_, _) =>
        {
            FilePickerExperience.Clear();
            if (_preAutoSaveSnapshotJson != null)
            {
                var original = JsonSerializer.Deserialize<AppConfig>(_preAutoSaveSnapshotJson);
                if (original != null) { original.Ux50PickerDirectories?.Clear(); _preAutoSaveSnapshotJson = JsonSerializer.Serialize(original); }
            }
            clear.Content = "目录记录已清除";
        };
        section.Children.Add(clear);
        var groupRoot = new Expander { Name = "ExperienceDetailsGroup", Header = "体验细节", IsExpanded = false, Content = section, Margin = new Thickness(0, 0, 0, 16) };
        var help = new MenuItem { Header = "设置离线帮助" };
        help.Click += (_, _) => OfflineHelpService.Show("settings");
        groupRoot.ContextMenu = new ContextMenu();
        groupRoot.ContextMenu.Items.Add(help);
        // Keep the existing save area at the bottom; all optional preferences remain collapsed.
        var index = Math.Max(0, SettingsRootPanel.Children.IndexOf(UxChangeSummaryButton) - 1);
        SettingsRootPanel.Children.Insert(index, groupRoot);
        InstallPreciseSliderEditors();
        RefreshPreciseSliderEditors();
        foreach (var input in new[] { MinMemBox, MaxMemBox, DynamicMinMbBox, DynamicMaxMbBox, FixedMemoryMbBox })
            InputExperienceService.SetRole(input, ExperienceInputRole.Memory);
        MinMemBox.TextChanged += MemoryOptions_Changed;
        MaxMemBox.TextChanged += MemoryOptions_Changed;
    }

    private static bool IsExperienceType(Type type) => type == typeof(bool) || type == typeof(int) || type == typeof(double) || type == typeof(string) || type.IsEnum;

    private static FrameworkElement CreateExperienceControl(PropertyInfo property, ExperiencePreferenceAttribute preference, object? value)
    {
        if (property.PropertyType == typeof(bool)) return new CheckBox { Content = preference.Label, IsChecked = value is true, Margin = new Thickness(0, 0, 0, 8) };
        if (property.PropertyType.IsEnum)
        {
            var combo = new ComboBox { MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 180, Margin = new Thickness(0, 0, 0, 10) };
            foreach (var option in Enum.GetValues(property.PropertyType)) combo.Items.Add(new ComboBoxItem { Content = ExperienceEnumLabel(option), Tag = option });
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value));
            return combo;
        }
        var text = new TextBox { Text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "", MaxWidth = property.PropertyType == typeof(string) ? 420 : 160, MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 10) };
        InputExperienceService.SetRole(text, ExperienceInputRole.PlainText);
        if (property.Name == nameof(AppConfig.LauncherCloseShortcut))
        {
            text.IsReadOnly = true;
            text.Tag = LauncherCloseShortcut.RecordingTag;
            text.PreviewKeyDown += (_, e) =>
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Tab) return;
                e.Handled = true;
                if ((key is Key.Back or Key.Delete) && Keyboard.Modifiers == ModifierKeys.None) { text.Text = ""; return; }
                if (LauncherCloseShortcut.IsAllowed(key, Keyboard.Modifiers))
                    text.Text = LauncherCloseShortcut.Format(key, Keyboard.Modifiers);
            };
        }
        return text;
    }

    private static string ExperienceEnumLabel(object option)
    {
        var member = option.GetType().GetMember(option.ToString() ?? "").FirstOrDefault();
        return member?.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? option.ToString() ?? "";
    }

    private bool TryReadExperienceValue(ExperienceEditor editor, out object? value)
    {
        value = null;
        var type = editor.Property.PropertyType;
        if (editor.Control is CheckBox check) { value = check.IsChecked == true; return true; }
        if (editor.Control is ComboBox combo && combo.SelectedItem is ComboBoxItem { Tag: { } selected } && type.IsInstanceOfType(selected)) { value = selected; return true; }
        if (editor.Control is not TextBox text) return false;
        if (type == typeof(string))
        {
            if (editor.Property.Name is "Ux50DownloadQuietStart" or "Ux50DownloadQuietEnd")
            {
                if (!SettingsInputValidation.TryClock(text.Text, out var clock)) return false;
                value = clock; // Use the same valid format accepted by DownloadNotificationPolicy.
                return true;
            }
            value = text.Text;
            if (editor.Property.Name == nameof(AppConfig.LauncherCloseShortcut))
                return LauncherCloseShortcut.TryParse(text.Text, out _);
            return text.Text.Length <= 1024;
        }
        if (!double.TryParse(text.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) && !double.TryParse(text.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return false;
        if (!double.IsFinite(number) || (!double.IsNaN(editor.Preference.Minimum) && number < editor.Preference.Minimum) || (!double.IsNaN(editor.Preference.Maximum) && number > editor.Preference.Maximum)) return false;
        if (type == typeof(int))
        {
            if (number < int.MinValue || number > int.MaxValue || Math.Truncate(number) != number) return false;
            value = (int)number;
        }
        else value = number;
        return true;
    }

    private bool IsExperienceEditorActive(ExperienceEditor editor)
    {
        if (editor.Property.Name is "Ux50DownloadQuietStart" or "Ux50DownloadQuietEnd")
            return _experienceEditors.FirstOrDefault(item => item.Property.Name == nameof(AppConfig.Ux50DownloadQuietHours))?.Control is CheckBox { IsChecked: true };
        return true;
    }

    private bool ValidateExperienceSettings()
    {
        var valid = true;
        foreach (var editor in _experienceEditors)
        {
            if (!IsExperienceEditorActive(editor) || TryReadExperienceValue(editor, out _)) { SetSettingError(editor.Control, null); continue; }
            var expected = editor.Property.PropertyType == typeof(string)
                ? (editor.Property.Name == nameof(AppConfig.LauncherCloseShortcut) ? "请点击输入框录入有效组合键，或按 Backspace 清除。"
                    : editor.Property.Name is "Ux50DownloadQuietStart" or "Ux50DownloadQuietEnd" ? "请输入 24 小时时刻，例如 8:30 或 08:30。" : "文字长度不能超过 1024 个字符。")
                : "请输入有效值" + (double.IsNaN(editor.Preference.Minimum) ? "" : $"，最小 {editor.Preference.Minimum}")
                    + (double.IsNaN(editor.Preference.Maximum) ? "" : $"，最大 {editor.Preference.Maximum}") + "。";
            SetSettingError(editor.Control, $"{editor.Preference.Label}：{expected}");
            valid = false;
        }
        return ValidateMemoryInputs() && valid;
    }

    private void SaveExperienceSettings(AppConfig cfg)
    {
        foreach (var editor in _experienceEditors) if (IsExperienceEditorActive(editor) && TryReadExperienceValue(editor, out var value)) editor.Property.SetValue(cfg, value);
        RefreshPreciseSliderEditors();
        foreach (Window window in Application.Current.Windows) InputExperienceService.RefreshSecretControls(window);
    }

    private object MeaningfulInputValue(TextBox input)
    {
        if (InputExperienceService.GetRole(input) == ExperienceInputRole.Memory && InputExperiencePolicy.TryParseMemoryMb(input.Text, MemoryUnitsEnabled, out var mb)) return mb;
        var editor = _experienceEditors.FirstOrDefault(item => ReferenceEquals(item.Control, input));
        return editor != null && editor.Property.PropertyType != typeof(string) && TryReadExperienceValue(editor, out var value) ? value ?? input.Text : input.Text;
    }

    private void RegisterExperienceDefaults(AppConfig defaults)
    {
        foreach (var editor in _experienceEditors)
            RegisterUxReset(editor.Control, () =>
            {
                var value = editor.Property.GetValue(defaults);
                if (editor.Control is ComboBox combo) combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value));
                else ApplyUxDefault(editor.Control, value);
            });
    }

    private bool PreciseSliderInputsEnabled
        => _experienceEditors.FirstOrDefault(item => item.Property.Name == nameof(AppConfig.Ux50InputPreciseSliders))?.Control is CheckBox check
            ? check.IsChecked == true : _owner.ConfigService.Config.Ux50InputPreciseSliders;

    private void RefreshPreciseSliderEditors(bool validateDraft = false)
    {
        bool enabled = PreciseSliderInputsEnabled;
        foreach (var pair in _preciseSliderEditors)
        {
            pair.Editor.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled) SetSettingError(pair.Editor, null);
        }
        if (validateDraft && !_suppressDirtyTracking && _preciseSliderEditors.Count > 0)
        {
            ValidateSettingsDraft();
            OnSettingsEdited();
        }
    }

    private void InstallPreciseSliderEditors()
    {
        // A small set of normal scalar controls; color composition and geometry editing keep their own UI.
        foreach (var slider in new[] { AnimationSpeedSlider, UiZoomSlider, MouseWheelSensitivitySlider, WindowOpacitySlider, GlobalWindowOpacitySlider, TextOpacitySlider, PerformanceOverlayFontSlider, PerformanceOverlayOpacitySlider, ModSnapshotCountSlider, ModSnapshotSpaceSlider })
        {
            if (slider.Parent is not Panel parent) continue;
            var index = parent.Children.IndexOf(slider);
            var margin = slider.Margin;
            var row = new Grid { Margin = margin, HorizontalAlignment = HorizontalAlignment.Left };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var editor = new TextBox { Width = 76, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Text = slider.Value.ToString("0.###", CultureInfo.CurrentCulture), Visibility = _owner.ConfigService.Config.Ux50InputPreciseSliders ? Visibility.Visible : Visibility.Collapsed, ToolTip = $"范围 {slider.Minimum}–{slider.Maximum}；Enter 应用，Esc 撤销本次输入。" };
            InputExperienceService.SetIsAuxiliary(editor, true);
            var syncing = false;
            void Sync() { syncing = true; editor.Text = slider.Value.ToString("0.###", CultureInfo.CurrentCulture); syncing = false; }
            void Commit()
            {
                if (syncing) return;
                if (!IsPreciseSettingActive(slider, editor)) { SetSettingError(editor, null); return; }
                if (!SettingsInputValidation.TryNumber(editor.Text, slider.Minimum, slider.Maximum, false, out var value))
                {
                    SetSettingError(editor, $"请输入 {slider.Minimum}–{slider.Maximum} 之间的有效数值。");
                    NotifySettingErrors(focusFirst: false);
                    return; // Preserve the invalid draft even after scrolling or focus loss.
                }
                SetSettingError(editor, null);
                if (slider.IsSnapToTickEnabled && slider.TickFrequency > 0) value = slider.Minimum + Math.Round((value - slider.Minimum) / slider.TickFrequency) * slider.TickFrequency;
                slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
                Sync();
            }
            editor.LostKeyboardFocus += (_, _) => Commit();
            editor.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) { SetSettingError(editor, null); Sync(); e.Handled = true; } };
            slider.ValueChanged += (_, _) => { if (!editor.IsKeyboardFocusWithin && !_validationErrors.ContainsKey(editor)) Sync(); };
            parent.Children.RemoveAt(index);
            slider.Margin = new Thickness(0);
            row.Children.Add(slider);
            Grid.SetColumn(editor, 1);
            row.Children.Add(editor);
            parent.Children.Insert(index, row);
            _preciseSliderEditors.Add((slider, editor));
        }
    }
}
