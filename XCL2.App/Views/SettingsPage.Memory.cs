using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private bool _memorySettingsInitialized;

    private void InitializeMemorySettings(AppConfig cfg)
    {
        _memorySettingsInitialized = false;
        DynamicMemoryCheck.IsChecked = cfg.EnableMemoryOptimization;
        SelectComboByTag(MemoryAllocationModeCombo, cfg.MemoryAllocationMode.ToString());
        DynamicMinMbBox.Text = cfg.DynamicMemoryMinimumMb.ToString();
        DynamicMaxMbBox.Text = cfg.DynamicMemoryMaximumMb.ToString();
        DynamicMinPercentBox.Text = cfg.DynamicMemoryMinimumPercent.ToString(CultureInfo.CurrentCulture);
        DynamicMaxPercentBox.Text = cfg.DynamicMemoryMaximumPercent.ToString(CultureInfo.CurrentCulture);
        FixedMemoryMbBox.Text = cfg.FixedMemoryAllocationMb?.ToString() ?? "";
        SelectComboByTag(MemoryDisplayBasisCombo, cfg.MemoryDisplayBasis.ToString());
        _memorySettingsInitialized = true;
        RefreshMemorySettingsPreview();
    }

    private MemoryAllocationPolicy.Options ReadMemoryOptions()
    {
        var cfg = _owner.ConfigService.Config;
        var mode = Enum.TryParse<MemoryAllocationMode>(TagOf(MemoryAllocationModeCombo), out var selected)
            ? selected : cfg.MemoryAllocationMode;
        int ReadMb(TextBox input, int fallback) => InputExperiencePolicy.TryParseMemoryMb(input.Text, MemoryUnitsEnabled, out var value) ? value : fallback;
        double ReadPercent(TextBox input, double fallback)
            => InputExperiencePolicy.TryParseMemoryPercent(input.Text, out var value) ? value : fallback;
        var fixedText = FixedMemoryMbBox.Text?.Trim();
        var fixedMb = string.IsNullOrEmpty(fixedText) ? null :
            InputExperiencePolicy.TryParseMemoryMb(fixedText, MemoryUnitsEnabled, out var value) ? (int?)value : cfg.FixedMemoryAllocationMb;
        return MemoryAllocationPolicy.Normalize(new MemoryAllocationPolicy.Options(mode,
            ReadMb(DynamicMinMbBox, cfg.DynamicMemoryMinimumMb),
            ReadMb(DynamicMaxMbBox, cfg.DynamicMemoryMaximumMb),
            ReadPercent(DynamicMinPercentBox, cfg.DynamicMemoryMinimumPercent),
            ReadPercent(DynamicMaxPercentBox, cfg.DynamicMemoryMaximumPercent), fixedMb));
    }

    private bool MemoryUnitsEnabled => _experienceEditors.FirstOrDefault(editor => editor.Property.Name == nameof(AppConfig.Ux50InputMemoryUnits))?.Control is CheckBox check
        ? check.IsChecked == true : _owner.ConfigService.Config.Ux50InputMemoryUnits;

    private bool ValidateMemoryInputs()
    {
        var inputs = new[] { MinMemBox, MaxMemBox, DynamicMinMbBox, DynamicMaxMbBox, DynamicMinPercentBox, DynamicMaxPercentBox, FixedMemoryMbBox };
        var errors = SettingsInputValidation.ValidateMemory(DynamicMemoryCheck.IsChecked == true,
            TagOf(MemoryAllocationModeCombo) == nameof(MemoryAllocationMode.TotalPercentage), MemoryUnitsEnabled,
            MinMemBox.Text, MaxMemBox.Text, DynamicMinMbBox.Text, DynamicMaxMbBox.Text,
            DynamicMinPercentBox.Text, DynamicMaxPercentBox.Text, FixedMemoryMbBox.Text);
        foreach (var input in inputs) SetSettingError(input, errors.TryGetValue(input.Name, out var error) ? error : null);
        return errors.Count == 0;
    }

    private string MemoryConversionSummary()
    {
        if (!MemoryUnitsEnabled) return "";
        var boxes = DynamicMemoryCheck.IsChecked == true ? new[] { DynamicMinMbBox, DynamicMaxMbBox, FixedMemoryMbBox } : new[] { MinMemBox, MaxMemBox };
        var lines = boxes.Where(box => !string.IsNullOrWhiteSpace(box.Text)).Select(box => InputExperiencePolicy.TryParseMemoryMb(box.Text, true, out var mb) ? $"{box.Text} → {mb:N0} MB" : $"{box.Text}：格式待修正");
        return "\n输入换算：" + string.Join("；", lines);
    }

    private MemoryDisplayBasis ReadMemoryDisplayBasis()
        => Enum.TryParse<MemoryDisplayBasis>(TagOf(MemoryDisplayBasisCombo), out var basis)
            ? basis : MemoryDisplayBasis.Total;

    private void SaveMemorySettings(AppConfig cfg)
    {
        cfg.EnableMemoryOptimization = DynamicMemoryCheck.IsChecked == true;
        cfg.MemoryDisplayBasis = ReadMemoryDisplayBasis();
        if (!cfg.EnableMemoryOptimization) return;
        if (Enum.TryParse<MemoryAllocationMode>(TagOf(MemoryAllocationModeCombo), out var mode)) cfg.MemoryAllocationMode = mode;
        cfg.FixedMemoryAllocationMb = string.IsNullOrWhiteSpace(FixedMemoryMbBox.Text) ? null
            : InputExperiencePolicy.TryParseMemoryMb(FixedMemoryMbBox.Text, MemoryUnitsEnabled, out var fixedMb) ? fixedMb : cfg.FixedMemoryAllocationMb;
        if (cfg.FixedMemoryAllocationMb.HasValue) return;
        if (cfg.MemoryAllocationMode == MemoryAllocationMode.TotalPercentage)
        {
            InputExperiencePolicy.TryParseMemoryPercent(DynamicMinPercentBox.Text, out var min);
            InputExperiencePolicy.TryParseMemoryPercent(DynamicMaxPercentBox.Text, out var max);
            cfg.DynamicMemoryMinimumPercent = min;
            cfg.DynamicMemoryMaximumPercent = max;
        }
        else
        {
            InputExperiencePolicy.TryParseMemoryMb(DynamicMinMbBox.Text, MemoryUnitsEnabled, out var min);
            InputExperiencePolicy.TryParseMemoryMb(DynamicMaxMbBox.Text, MemoryUnitsEnabled, out var max);
            cfg.DynamicMemoryMinimumMb = min;
            cfg.DynamicMemoryMaximumMb = max;
        }
        // Keep the draft text and its units intact; inactive fields are neither validated nor rewritten.
    }

    private void MemoryOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (!_memorySettingsInitialized) return;
        RefreshMemorySettingsPreview();
    }

    private void RefreshMemoryPreview_Click(object sender, RoutedEventArgs e) => RefreshMemorySettingsPreview();

    private void RefreshMemorySettingsPreview()
    {
        if (!_memorySettingsInitialized) return;
        var enabled = DynamicMemoryCheck.IsChecked == true;
        DynamicMemoryPanel.IsEnabled = enabled;
        ManualMemoryPanel.IsEnabled = !enabled;
        var options = ReadMemoryOptions();
        MemoryRangePanel.Visibility = options.Mode == MemoryAllocationMode.FixedRange ? Visibility.Visible : Visibility.Collapsed;
        MemoryPercentPanel.Visibility = options.Mode == MemoryAllocationMode.TotalPercentage ? Visibility.Visible : Visibility.Collapsed;
        var invalid = SettingsInputValidation.ValidateMemory(enabled, options.Mode == MemoryAllocationMode.TotalPercentage, MemoryUnitsEnabled,
            MinMemBox.Text, MaxMemBox.Text, DynamicMinMbBox.Text, DynamicMaxMbBox.Text,
            DynamicMinPercentBox.Text, DynamicMaxPercentBox.Text, FixedMemoryMbBox.Text);
        if (invalid.Count > 0)
        {
            MemoryAllocationPreviewText.Text = "当前内存输入有误，尚未保存，也未自动修正。请按输入框下方的提示修改后查看预览。";
            return;
        }
        var memory = MemoryOptimizerService.ReadPhysicalMemory();
        if (memory == null)
        {
            MemoryAllocationPreviewText.Text = "暂时无法读取物理内存；启动时采样失败会保留手动内存设置。" + MemoryConversionSummary();
            return;
        }
        MemoryAllocationPreviewText.Text = enabled
            ? MemoryOptimizerService.Calculate(options, memory, ReadMemoryDisplayBasis()).Explanation
            : (ReadMemoryDisplayBasis() == MemoryDisplayBasis.Available
                ? $"当前剩余内存 {memory.AvailableMb}MB。" : $"系统总内存 {memory.TotalMb}MB。") +
                "动态分配已关闭，使用手动内存值；实例单独设置优先。";
        MemoryAllocationPreviewText.Text += MemoryConversionSummary();
    }
}
