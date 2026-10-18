using System.IO;
using Microsoft.Win32;

namespace XCL2.App.Services;

/// <summary>Opt-in, purpose-specific picker history; never intercepts or opens a native dialog.</summary>
public static class FilePickerExperience
{
    public static void Prepare(FileDialog dialog, string purpose)
    {
        var cfg = ConfigService.Active?.Config;
        if (cfg?.Ux50InputRememberPickerDirectory != true || string.IsNullOrEmpty(purpose)) return;
        if (cfg.Ux50PickerDirectories == null) return;
        if (cfg.Ux50PickerDirectories.TryGetValue(purpose, out var directory) && Directory.Exists(directory)) dialog.InitialDirectory = directory;
    }

    public static void Remember(FileDialog dialog, string purpose)
    {
        var service = ConfigService.Active;
        if (service?.Config.Ux50InputRememberPickerDirectory != true || string.IsNullOrEmpty(purpose)) return;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(dialog.FileName));
            if (directory == null || !Directory.Exists(directory)) return;
            service.Config.Ux50PickerDirectories ??= new(StringComparer.Ordinal);
            if (service.Config.Ux50PickerDirectories.Count >= 32 && !service.Config.Ux50PickerDirectories.ContainsKey(purpose)) return;
            service.Config.Ux50PickerDirectories[purpose] = directory;
            service.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    public static void Clear()
    {
        var service = ConfigService.Active;
        if (service == null) return;
        service.Config.Ux50PickerDirectories?.Clear();
        service.Save();
    }
}
