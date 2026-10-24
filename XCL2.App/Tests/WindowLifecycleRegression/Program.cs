using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using XCL2.App.Services;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("BLOCKED: requires a Windows interactive desktop; no native checks ran.");
            return 2;
        }
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var content = new Border { Background = Brushes.White, Child = new TextBlock { Text = "Native window lifecycle regression" } };
        var window = new Window
        {
            Title = "Window lifecycle regression", Width = 640, Height = 400,
            MinWidth = 200, MinHeight = 150, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.SingleBorderWindow, ResizeMode = ResizeMode.CanResize,
            AllowsTransparency = false, Content = content
        };
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 32, GlassFrameThickness = new Thickness(1), CornerRadius = new CornerRadius(0),
            ResizeBorderThickness = new Thickness(6), UseAeroCaptionButtons = false
        });
        Application.Current.MainWindow = window;
        window.SourceInitialized += (_, _) =>
        {
            WindowChromeService.EnableWorkAreaAwareMaximize(window);
            WindowChromeService.ApplyNativeTransitionPreference(window, AnimationKind.WindowOpen);
        };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            PumpUntil(() => window.IsLoaded, "First frame did not load.");
            Assert(content.Opacity == 1, "First-frame content must not wait in a transparent state.");
            var hwnd = new WindowInteropHelper(window).Handle;
            var style = unchecked((uint)GetWindowLong(hwnd, -16));
            const uint requiredStyles = 0x00C00000 | 0x00080000 | 0x00040000 | 0x00020000 | 0x00010000;
            Assert((style & requiredStyles) == requiredStyles, "Custom WindowChrome lost native caption/resize/system-command styles.");
            AssertNoCustomRegion(hwnd);

            AnimationSettingsService.Disabled.Add(AnimationKind.WindowMinimize);
            SystemCommands.MaximizeWindow(window);
            PumpUntil(() => window.WindowState == WindowState.Maximized, "Maximize command did not complete.");
            Assert(LastRequest(AnimationKind.WindowFullscreen), "Disabling minimize must not disable maximize.");
            Assert(content.Opacity == 1 && content.RenderTransform.Value.IsIdentity, "Native resize must keep content geometry and opacity intact.");
            AssertInsideWorkArea(hwnd);
            AssertNoCustomRegion(hwnd);

            SystemCommands.MinimizeWindow(window);
            PumpUntil(() => window.WindowState == WindowState.Minimized, "Minimize command did not complete.");
            Assert(!LastRequest(AnimationKind.WindowMinimize), "Minimize must use its own motion preference.");
            SystemCommands.RestoreWindow(window);
            PumpUntil(() => window.WindowState != WindowState.Minimized, "Taskbar-style restore did not complete.");
            Assert(window.WindowState == WindowState.Maximized, "A maximized window must restore to maximized after minimization.");
            Assert(LastRequest(AnimationKind.WindowRestore), "Minimized restore must use the restore preference.");
            SystemCommands.RestoreWindow(window);
            PumpUntil(() => window.WindowState == WindowState.Normal, "Restore from maximized did not complete.");
            Assert(LastRequest(AnimationKind.WindowFullscreen), "Restore from maximized belongs to the maximize/fullscreen category.");

            var normalBounds = new Rect(window.Left, window.Top, window.Width, window.Height);
            WindowChromeService.ToggleFullScreen(window);
            Assert(WindowChromeService.IsFullScreen && window.WindowState == WindowState.Maximized, "F11 entry failed.");
            WindowChromeService.ToggleFullScreen(window);
            Assert(!WindowChromeService.IsFullScreen && window.WindowStyle == WindowStyle.SingleBorderWindow,
                "F11 exit must restore the native window style.");
            AssertNoCustomRegion(hwnd);
            Assert(window.WindowState == WindowState.Normal && Math.Abs(window.Width - normalBounds.Width) < 1
                && Math.Abs(window.Height - normalBounds.Height) < 1, "F11 exit must preserve normal bounds.");

            var cancelClose = true;
            var closingCalls = 0;
            window.Closing += (_, e) =>
            {
                ++closingCalls;
                e.Cancel = cancelClose;
                if (!e.Cancel) WindowChromeService.ApplyNativeTransitionPreference(window, AnimationKind.WindowClose);
            };
            AnimationSettingsService.Disabled.Add(AnimationKind.WindowClose);
            SystemCommands.CloseWindow(window);
            PumpUntil(() => closingCalls == 1, "System close did not reach Closing.");
            Assert(!closed && window.IsVisible && content.Opacity == 1, "Canceled close must leave a visible, usable window.");
            cancelClose = false;
            SystemCommands.CloseWindow(window);
            PumpUntil(() => closed, "Accepted close with animations disabled did not complete.");
            Assert(closingCalls == 2 && !LastRequest(AnimationKind.WindowClose), "Accepted close must run once and honor reduced motion.");
            Console.WriteLine("PASS: native caption styles/no custom region, per-action preferences, maximize/work area, minimize/restore state, F11 restore, native canceled/accepted close.");
            Console.WriteLine("Visual smoothness/timing and the full launcher startup/confirmation paths still require manual Windows acceptance.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (!closed) window.Close();
            Application.Current.Shutdown();
        }
    }

    private static bool LastRequest(AnimationKind kind) => AnimationSettingsService.Requests.Last(item => item.Kind == kind).Enabled;
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void PumpUntil(Func<bool> condition, string failure)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(4))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        Assert(condition(), failure);
    }
    private static void AssertInsideWorkArea(IntPtr hwnd)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Assert(GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info) && GetWindowRect(hwnd, out var bounds), "Cannot query native monitor bounds.");
        // Invisible resize borders may extend a few physical pixels beyond rcWork.
        Assert(GetWindowRect(hwnd, out bounds) && bounds.Left >= info.Work.Left - 16 && bounds.Top >= info.Work.Top - 16
            && bounds.Right <= info.Work.Right + 16 && bounds.Bottom <= info.Work.Bottom + 16,
            "Maximize must respect the monitor work area and taskbar.");
    }
    private static void AssertNoCustomRegion(IntPtr hwnd)
    {
        if (DwmIsCompositionEnabled(out var compositionEnabled) != 0 || !compositionEnabled)
        {
            Console.WriteLine("SKIP: DWM is unavailable; custom-region assertion requires desktop composition.");
            return;
        }
        var region = CreateRectRgn(0, 0, 0, 0);
        Assert(region != IntPtr.Zero, "Cannot allocate a region for native inspection.");
        try
        {
            // GetWindowRgn returns 0 when no application-defined region exists.
            // DWM clipping/rounded corners do not count as an application-defined region.
            Assert(GetWindowRgn(hwnd, region) == 0,
                "A custom HWND region disables DWM rounding; preserve nonzero GlassFrameThickness.");
        }
        finally { DeleteObject(region); }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RectI Monitor, Work; public int Flags; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectI rect);
}
