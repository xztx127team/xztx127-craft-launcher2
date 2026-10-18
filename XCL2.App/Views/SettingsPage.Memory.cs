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
        var enabled = DynamicMemoryCheck.IsChecked == true;
        var percentageMode = enabled && TagOf(MemoryAllocationModeCombo) == nameof(MemoryAllocationMode.TotalPercentage);
        if (percentageMode && !InputExperiencePolicy.TryValidateMemoryPercentRange(DynamicMinPercentBox.Text, DynamicMaxPercentBox.Text, out _, out _))
        {
            DynamicMinPercentBox.ToolTip = DynamicMaxPercentBox.ToolTip = "请输入 0.1–100 的有效百分比，并确保最低值不超过最高值；当前值尚未保存。";
            StatusText.Text = "动态内存百分比无效，尚未保存。最低值必须小于或等于最高值，范围为 0.1–100%。";
            return false;
        }
        var inputs = enabled ? (percentageMode ? new[] { FixedMemoryMbBox } : new[] { DynamicMinMbBox, DynamicMaxMbBox, FixedMemoryMbBox }) : new[] { MinMemBox, MaxMemBox };
        foreach (var input in inputs)
        {
            if (ReferenceEquals(input, FixedMemoryMbBox) && string.IsNullOrWhiteSpace(input.Text)) continue;
            if (!InputExperiencePolicy.TryParseMemoryMb(input.Text, MemoryUnitsEnabled, out _))
            {
                input.ToolTip = "请输入有效内存，例如 2048MB、2GB（纯数字按 MB）。当前值尚未保存。";
                StatusText.Text = "内存输入无效，尚未保存。请查看内存输入框提示。";
                return false;
            }
        }
        if (!percentageMode)
        {
            var minimum = enabled ? DynamicMinMbBox : MinMemBox;
            var maximum = enabled ? DynamicMaxMbBox : MaxMemBox;
            if (InputExperiencePolicy.TryParseMemoryMb(minimum.Text, MemoryUnitsEnabled, out var min) &&
                InputExperiencePolicy.TryParseMemoryMb(maximum.Text, MemoryUnitsEnabled, out var max) && min > max)
            {
                minimum.ToolTip = maximum.ToolTip = "最低内存不能超过最高内存；当前值尚未保存。";
                StatusText.Text = "内存范围倒置，尚未保存。最低内存不能超过最高内存。";
                return false;
            }
        }
        return true;
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
        var options = ReadMemoryOptions();
        cfg.EnableMemoryOptimization = DynamicMemoryCheck.IsChecked == true;
        cfg.MemoryAllocationMode = options.Mode;
        cfg.DynamicMemoryMinimumMb = options.MinimumMb;
        cfg.DynamicMemoryMaximumMb = options.MaximumMb;
        cfg.DynamicMemoryMinimumPercent = options.MinimumPercent;
        cfg.DynamicMemoryMaximumPercent = options.MaximumPercent;
        cfg.FixedMemoryAllocationMb = options.FixedMb;
        cfg.MemoryDisplayBasis = ReadMemoryDisplayBasis();
        // 将动态输入回显为真实保存值；手动值保持原样，启动前由 JVM 参数诊断验证。
        var suppress = _suppressDirtyTracking;
        _suppressDirtyTracking = true;
        try
        {
            MinMemBox.Text = cfg.MinMemoryMb.ToString();
            MaxMemBox.Text = cfg.MaxMemoryMb.ToString();
            InitializeMemorySettings(cfg);
        }
        finally { _suppressDirtyTracking = suppress; }
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
