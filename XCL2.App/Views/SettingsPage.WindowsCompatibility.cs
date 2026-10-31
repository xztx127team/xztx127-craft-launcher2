using System;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class SettingsPage
{
    private WindowsCompatibilityMode _savedWindowsCompatibilityMode;

    private void InitializeWindowsCompatibilitySettings(AppConfig config)
    {
        _savedWindowsCompatibilityMode = config.WindowsCompatibilityMode;
        SelectComboByTag(WindowsCompatibilityModeCombo, config.WindowsCompatibilityMode.ToString());
        if (WindowsCompatibilityModeCombo.SelectedItem == null)
            WindowsCompatibilityModeCombo.SelectedIndex = 0;
        RefreshWindowsCompatibilityStatus();
    }

    private WindowsCompatibilityMode SelectedWindowsCompatibilityMode
        => Enum.TryParse<WindowsCompatibilityMode>(
            (WindowsCompatibilityModeCombo.SelectedItem as ComboBoxItem)?.Tag as string, out var mode)
            ? mode : WindowsCompatibilityMode.Auto;

    private void WindowsCompatibilityModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowsCompatibilityStatusText == null) return;
        RefreshWindowsCompatibilityStatus();
        if (_suppressDirtyTracking) return;

        var warning = WindowsCompatibilitySelectionWarning(SelectedWindowsCompatibilityMode);
        if (warning != null) ToastService.Show(warning, ToastService.ToastKind.Warning, 8);
        // 全页统一的脏值跟踪/自动保存接管本次编辑；选择下拉项本身不改运行时或配置。
    }

    private static string? WindowsCompatibilitySelectionWarning(WindowsCompatibilityMode mode)
    {
        var current = WindowsCompatibilityService.Current;
        if (current.IsLegacyWindows && mode == WindowsCompatibilityMode.Disabled)
            return "关闭兼容模式后可能会导致部分功能不可用。建议在 Windows 8.1 及更早版本保持自动或开启。";
        if (current.IsWindows && current.OsVersion.Major >= 10 && mode == WindowsCompatibilityMode.Enabled)
            return "在 Windows 10 及以上开启兼容模式后，一些功能运行速度会减慢，效率会降低。";
        return null;
    }

    private void RefreshWindowsCompatibilityStatus()
    {
        if (WindowsCompatibilityStatusText == null) return;
        var current = WindowsCompatibilityService.Current;
        var selected = SelectedWindowsCompatibilityMode;
        var preview = WindowsCompatibilityService.Evaluate(current.OsVersion, selected, current.IsWindows);
        var state = preview.IsEnabled ? preview.DisplayName : "兼容模式关闭";
        var warning = WindowsCompatibilitySelectionWarning(selected);
        WindowsCompatibilityStatusText.Text = $"当前系统：Windows {current.OsVersion}；保存后：{state}。"
            + (warning == null ? "" : "\n" + warning);
    }

    private void SaveWindowsCompatibilitySettings(AppConfig config)
        => config.WindowsCompatibilityMode = SelectedWindowsCompatibilityMode;

    private void ApplyWindowsCompatibilitySettings(AppConfig config)
    {
        WindowsCompatibilityService.Configure(config.WindowsCompatibilityMode);
        if (_savedWindowsCompatibilityMode != config.WindowsCompatibilityMode)
        {
            _savedWindowsCompatibilityMode = config.WindowsCompatibilityMode;
            ToastService.ShowInfo("兼容模式设置已保存，后续操作使用新设置；重启可让已存在的窗口和连接重新应用。");
        }
        RefreshWindowsCompatibilityStatus();
    }
}
