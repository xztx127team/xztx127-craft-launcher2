using System.Diagnostics;
using XCL2.App.Models;

namespace XCL2.App.Services.Plugins;

/// <summary>Legacy UI hooks receive detached presentation data, never live credentials or launch state.</summary>
internal static class PluginLaunchPrivacy
{
    internal static LauncherService.LaunchOptions CopyOptions(LauncherService.LaunchOptions source) => new()
    {
        // Legacy hooks have only ui.components; that is not accounts.read,
        // instances.read or java.read. Do not disclose identity or personal paths.
        Account = new Account { Id = "", Username = "", Uuid = "" },
        MinMemoryMb = source.MinMemoryMb,
        MaxMemoryMb = source.MaxMemoryMb,
        WindowWidth = source.WindowWidth,
        WindowHeight = source.WindowHeight,
        UseHighPerformanceGpu = source.UseHighPerformanceGpu,
        ShowConsoleWindow = source.ShowConsoleWindow,
        UseTouchMode = source.UseTouchMode,
        IsolateVersion = source.IsolateVersion,
        GameLanguage = source.GameLanguage,
        GraphicsApiPreference = source.GraphicsApiPreference,
        LaunchFullscreen = source.LaunchFullscreen
        // Free-form commands, arguments, auth endpoints and prepared plans stay in
        // the host. They can carry arbitrary credentials outside the account store.
        // Changes to this snapshot are deliberately not copied back into the launch.
    };

    internal static GameProcessInfo CopyProcess(GameProcessInfo source)
    {
        // Legacy hooks are not allowed to inspect instance/process information.
        // Keep next()'s return contract without exposing the actual PID, account,
        // private paths, command line, launch environment or output buffer.
        var process = new Process { StartInfo = new ProcessStartInfo { UseShellExecute = false } };
        process.StartInfo.Environment.Clear();
        return new GameProcessInfo(process, "", "", "");
    }
}
