using System.Text.Json;
using Microsoft.Win32;

namespace XCL2.App.Services;

/// <summary>Per-user global setting, shared by launcher copies and independent of config imports.</summary>
internal static class LauncherUpdatePreferences
{
    private const string RegistryPath = @"SOFTWARE\XCL2\LauncherUpdates";
    private const string ReminderValue = "StableReminderPreference";
    private static LauncherReminderPreference? _cached;

    internal static LauncherReminderPreference Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            var json = key?.GetValue(ReminderValue) as string;
            if (!string.IsNullOrWhiteSpace(json))
                return _cached = JsonSerializer.Deserialize<LauncherReminderPreference>(json) ?? new();
        }
        catch (Exception ex) { LauncherLogService.AppendLine("[更新设置] 读取全局提醒设置失败：" + ex.Message); }
        return _cached ?? new();
    }

    internal static bool Save(LauncherReminderPreference preference)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            if (key == null) return false;
            key.SetValue(ReminderValue, JsonSerializer.Serialize(preference), RegistryValueKind.String);
            _cached = preference;
            return true;
        }
        catch (Exception ex)
        {
            LauncherLogService.AppendLine("[更新设置] 写入全局提醒设置失败：" + ex.Message);
            return false;
        }
    }
}
