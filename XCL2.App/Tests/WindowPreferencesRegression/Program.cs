using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); _checks++; }

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("BLOCKED: requires an interactive Windows desktop."); return 2; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (args.Contains("--competitor"))
        {
            var competitor = new Window { Title = "XCL2 regression competitor", Width = 220, Height = 120, Topmost = true, ShowActivated = false };
            competitor.Show(); Console.WriteLine(new WindowInteropHelper(competitor).Handle.ToInt64());
            _ = Task.Run(() => { Console.ReadLine(); app.Dispatcher.BeginInvoke(new Action(() => app.Shutdown())); });
            return app.Run();
        }
        Process? child = null;
        Window? primary = null;
        try
        {
            var defaults = new AppConfig();
            Check(defaults.EnableAltF4Close && defaults.LauncherForceTopmost && defaults.PerformanceOverlayForceTopmost, "default switches are enabled");
            defaults.EnableAltF4Close = false; defaults.LauncherForceTopmost = false; defaults.LauncherCloseShortcut = "Ctrl+Shift+Q";
            var saved = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(defaults))!;
            Check(!saved.EnableAltF4Close && !saved.LauncherForceTopmost && saved.PerformanceOverlayForceTopmost
                && saved.LauncherCloseShortcut == "Ctrl+Shift+Q", "window preferences round-trip independently");
            Check(LauncherCloseShortcut.TryParse(saved.LauncherCloseShortcut, out var gesture)
                && gesture!.Key == Key.Q && gesture.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift), "custom close chord parses exactly");
            Check(LauncherCloseShortcut.TryParse("", out gesture) && gesture == null, "empty chord disables custom closing");
            foreach (var invalid in new[] { "Alt+F4", "F3", "F10", "F11", "F12", "Q", "Esc", "not-a-key" })
                Check(!LauncherCloseShortcut.TryParse(invalid, out _), "reserved or invalid close gesture rejected: " + invalid);

            var force = true;
            primary = new Window { Title = "XCL2 forced-topmost regression", Width = 300, Height = 150, Topmost = true, ShowActivated = false };
            ForcedTopmostService.Attach(primary, () => force);
            primary.Show(); var hwnd = new WindowInteropHelper(primary).Handle;
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--competitor");
            child = Process.Start(start)!;
            var childHandle = new IntPtr(long.Parse(child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult()!));
            var focus = GetForegroundWindow(); Pump(800);
            Check(IsAbove(hwnd, childHandle), "forced mode recovers above another process's topmost window");
            Check(GetForegroundWindow() == focus, "Z-order maintenance never steals keyboard focus");
            force = false; ForcedTopmostService.Refresh();
            SetWindowPos(childHandle, new IntPtr(-1), 0, 0, 0, 0, 0x0013); Pump(800);
            Check(primary.Topmost && IsAbove(childHandle, hwnd), "disabling forced mode retains ordinary topmost and stops competing");
            force = true; ForcedTopmostService.Refresh(); Pump(400);
            Check(IsAbove(hwnd, childHandle), "re-enabling forced mode takes effect without restarting");
            primary.Topmost = false; Pump(400);
            Check(!primary.Topmost && IsAbove(childHandle, hwnd), "disabling ordinary topmost also stops forced elevation");
            Console.WriteLine($"PASS: {_checks} window/shortcut regression checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            primary?.Close();
            if (child != null)
            {
                try { child.StandardInput.WriteLine("exit"); if (!child.WaitForExit(3000)) child.Kill(); } catch (InvalidOperationException) { }
                child.Dispose();
            }
            app.Shutdown();
        }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static bool IsAbove(IntPtr first, IntPtr second)
    {
        var current = GetWindow(second, 3);
        for (var remaining = 512; current != IntPtr.Zero && remaining-- > 0; current = GetWindow(current, 3))
            if (current == first) return true;
        return false;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
