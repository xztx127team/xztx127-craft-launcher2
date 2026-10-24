using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        if (!OperatingSystem.IsWindows()) return 2;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            var fresh = new ConfigService();
            var first = Open(fresh);
            Check(Near(first.Width, 500) && Near(first.Height, 320), "First launch must be 500x320.");
            first.Close();
            Check(fresh.SaveCalls == 1 && fresh.Config.LauncherWindowPlacement is { Maximized: false } firstSaved
                && Near(firstSaved.Width, 500) && Near(firstSaved.Height, 320),
                "Normal close must save the first-launch rectangle.");

            var config = SavedConfig();
            var restored = Open(config, expectSavedBeforeShow: true);
            Check(Near(restored.Width, 700) && Near(restored.Height, 450), "First Show must retain the saved geometry.");
            restored.Width = 630; restored.Height = 410;
            Pump();
            var cancel = true;
            restored.Closing += (_, e) => e.Cancel = cancel;
            restored.Close();
            Check(config.SaveCalls == 0 && restored.IsVisible, "Canceling close must not persist or close the window.");
            cancel = false;
            restored.Close();
            Check(config.SaveCalls == 1 && Near(config.Config.LauncherWindowPlacement!.Width, 630)
                && Near(config.Config.LauncherWindowPlacement.Height, 410), "A manually resized window must save its new normal dimensions.");

            var maximized = Open(config);
            maximized.WindowState = WindowState.Maximized;
            Pump();
            maximized.WindowState = WindowState.Minimized;
            Pump();
            maximized.Close();
            Check(config.Config.LauncherWindowPlacement is { Maximized: true }
                && Near(config.Config.LauncherWindowPlacement.Width, 630), "Close while minimized must preserve the prior maximized state and normal rectangle.");
            var reopened = Open(config);
            Check(reopened.WindowState == WindowState.Maximized, "Saved maximized state must restore on the next launch.");
            reopened.Close();

            foreach (var startMaximized in new[] { false, true })
            {
                var fullscreenConfig = SavedConfig(startMaximized);
                var window = Open(fullscreenConfig);
                var before = window.RestoreBounds;
                WindowChromeService.ToggleFullScreen(window);
                Check(WindowChromeService.GetPreFullScreenState(window) ==
                    (startMaximized ? WindowState.Maximized : WindowState.Normal), "F11 must retain its ordinary source state.");
                window.Close();
                var saved = fullscreenConfig.Config.LauncherWindowPlacement!;
                Check(saved.Maximized == startMaximized && Near(saved.Width, before.Width) && Near(saved.Height, before.Height),
                    "Closing in F11 must preserve the pre-fullscreen bounds/state.");
                Check(!WindowChromeService.IsFullScreen, "Closing a fullscreen window must clear its static session.");
            }

            var roundTripConfig = SavedConfig();
            var roundTrip = Open(roundTripConfig);
            WindowChromeService.ToggleFullScreen(roundTrip);
            WindowChromeService.ToggleFullScreen(roundTrip);
            Pump();
            roundTrip.Close();
            Check(roundTripConfig.Config.LauncherWindowPlacement is { Maximized: false }
                && Near(roundTripConfig.Config.LauncherWindowPlacement.Width, 700)
                && Near(roundTripConfig.Config.LauncherWindowPlacement.Height, 450), "F11 round trip must not pollute the ordinary restore rectangle.");

            foreach (var exclusion in new[] { "temporary", "guest", "disabled" })
            {
                var excluded = SavedConfig();
                var original = excluded.Config.LauncherWindowPlacement;
                excluded.Config.GuestModeEnabled = exclusion == "guest";
                excluded.Config.RememberWindowPlacement = exclusion != "disabled";
                var window = Open(excluded, temporary: exclusion == "temporary");
                if (exclusion != "guest") Check(Near(window.Width, 500), "Temporary/disabled placement must not restore saved geometry.");
                window.Width = 620; Pump(); window.Close();
                Check(excluded.SaveCalls == 0 && ReferenceEquals(original, excluded.Config.LauncherWindowPlacement),
                    $"{exclusion} session must not replace the saved placement.");
            }
            Console.WriteLine($"PASS {_checks} native window-placement checks.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            foreach (var window in app.Windows.Cast<Window>().ToArray()) window.Close();
            app.Shutdown();
        }
    }

    private static ConfigService SavedConfig(bool maximized = false)
    {
        var result = new ConfigService();
        result.Config.LauncherWindowPlacement = new() { Width = 700, Height = 450, Maximized = maximized };
        return result;
    }

    private static Window Open(ConfigService config, bool temporary = false, bool expectSavedBeforeShow = false)
    {
        var window = new Window
        {
            Title = "Window placement regression", Width = LauncherWindowPlacement.DefaultWidth,
            Height = LauncherWindowPlacement.DefaultHeight, MinWidth = 420, MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, WindowStyle = WindowStyle.SingleBorderWindow,
            Content = new TextBlock { Text = "Placement regression" }
        };
        Application.Current.MainWindow = window;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 32, GlassFrameThickness = new Thickness(1), ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = false
        });
        window.SourceInitialized += (_, _) => WindowChromeService.EnableWorkAreaAwareMaximize(window);
        _ = WindowPlacementService.Attach(window, config, temporary);
        if (expectSavedBeforeShow)
            window.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(window).Handle;
                Check(!IsWindowVisible(handle), "Placement must restore before native ShowWindow.");
                Check(GetWindowRect(handle, out var bounds), "The restored HWND must have a native rectangle.");
                var dpi = GetDpiForWindow(handle) / 96d;
                Check(dpi > 0 && Near((bounds.Right - bounds.Left) / dpi, 700)
                    && Near((bounds.Bottom - bounds.Top) / dpi, 450), "Saved 700x450 must be applied during SourceInitialized.");
                Check(window.WindowStartupLocation == WindowStartupLocation.Manual, "CenterScreen must not override saved coordinates.");
            };
        window.Show(); Pump();
        return window;
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static bool Near(double left, double right) => Math.Abs(left - right) < 1.1;
    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
        _checks++;
    }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
}
