using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string description)
{
    checks++;
    if (!condition) throw new InvalidOperationException(description);
}

foreach (var version in new[] { new Version(6, 0, 6002), new Version(6, 1, 7601) })
{
    var auto = WindowsCompatibilityService.Evaluate(version, WindowsCompatibilityMode.Auto);
    Check(auto is { IsEnabled: true, IsAutomatic: true, Layer: WindowsCompatibilityLayer.Full }, "Vista/7 auto needs full layer.");
    Check(!auto.SupportsNativeToasts && !auto.SupportsPerMonitorDpi && !auto.SupportsPointerInput
        && !auto.SupportsCpuRateControl && !auto.SupportsWinUi && !auto.SupportsSystemTheme, "Vista/7 unsupported API gates.");
    Check(auto.UseClassicFolderPicker, "Vista/7 selects classic picker.");
    var disabled = WindowsCompatibilityService.Evaluate(version, WindowsCompatibilityMode.Disabled);
    Check(!disabled.IsEnabled && !disabled.SupportsNativeToasts && !disabled.SupportsPointerInput,
        "Opt-out must never expose missing native exports.");
}

foreach (var version in new[] { new Version(6, 2, 9200), new Version(6, 3, 9600), new Version(6, 3) })
{
    var auto = WindowsCompatibilityService.Evaluate(version, WindowsCompatibilityMode.Auto);
    Check(auto is { IsEnabled: true, Layer: WindowsCompatibilityLayer.Standard }, "Windows 8 and 8.1 use the standard layer.");
    Check(auto.SupportsPointerInput && auto.SupportsCpuRateControl && !auto.SupportsWinUi, "Windows 8 capability boundary.");
    Check(auto.SupportsPerMonitorDpi == (version.Minor >= 3), "Per-monitor DPI begins with Windows 8.1.");
}

foreach (var build in new[] { 10240, 14393, 17762, 17763, 19045, 22000, 22621, 26100 })
{
    var auto = WindowsCompatibilityService.Evaluate(new Version(10, 0, build), WindowsCompatibilityMode.Auto);
    Check(!auto.IsEnabled && auto.SupportsNativeToasts, "Windows 10+ defaults to native path.");
    Check(auto.SupportsSystemTheme == (build >= 17763), "Automatic theme 1809 boundary.");
    Check(auto.SupportsWinUi == (build >= 17763), "Native WinUI 1809 boundary.");
    Check(auto.SupportsNativeCorners == (build >= 22000), "Native corner build boundary.");
    Check(auto.SupportsSystemBackdrop == (build >= 22621), "System backdrop build boundary.");
    var manual = WindowsCompatibilityService.Evaluate(auto.OsVersion, WindowsCompatibilityMode.Enabled);
    Check(manual.Layer == WindowsCompatibilityLayer.Standard && !manual.IsAutomatic && manual.UseClassicFolderPicker,
        "Manual modern mode enables the ordinary layer.");
    Check(!manual.SupportsNativeToasts && !manual.SupportsWinUi && !manual.SupportsSystemBackdrop
        && !manual.SupportsNativeDarkTitleBar, "Manual mode uses WPF fallbacks.");
    Check(manual.SupportsSystemTheme == auto.SupportsSystemTheme, "Fallback mode must not lie about OS theme capability.");
}

var invalid = WindowsCompatibilityService.Evaluate(new Version(6, 1), (WindowsCompatibilityMode)900);
Check(invalid.Mode == WindowsCompatibilityMode.Auto && invalid.IsEnabled, "Corrupt enum values fall back to Auto.");
var notWindows = WindowsCompatibilityService.Evaluate(new Version(10, 0, 26100), WindowsCompatibilityMode.Enabled, false);
Check(!notWindows.IsEnabled && !notWindows.SupportsWinUi && !notWindows.SupportsPointerInput, "Non-Windows hosts do not select native Windows APIs.");
var config = JsonSerializer.Deserialize<AppConfig>("{}")!;
Check(config.WindowsCompatibilityMode == WindowsCompatibilityMode.Auto, "Missing old-config preference uses Auto.");
config.WindowsCompatibilityMode = WindowsCompatibilityMode.Disabled;
Check(JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!.WindowsCompatibilityMode == WindowsCompatibilityMode.Disabled,
    "Explicit opt-out survives serialization.");
Console.WriteLine($"PASS: {checks} Windows compatibility policy checks.");
