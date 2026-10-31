using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace XCL2.App.Services;

/// <summary>Keep the native window outline and WPF content clip in agreement at every size.</summary>
public static class RoundedWindowService
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void ClipContent(FrameworkElement content, double radius = 12)
    {
        void Update() => content.Clip = new RectangleGeometry(
            new Rect(0, 0, Math.Max(0, content.ActualWidth), Math.Max(0, content.ActualHeight)), radius, radius);
        content.SizeChanged += (_, _) => Update();
        Update();
    }

    public static void Attach(Window window, FrameworkElement content, double radius = 12)
    {
        var lastPreference = -1;
        var lastHandle = IntPtr.Zero;
        void Update()
        {
            if (window.WindowState == WindowState.Minimized) return;
            var systemCorners = WindowsCompatibilityService.Current.SupportsNativeCorners;
            var rounded = window.WindowState == WindowState.Normal
                && !(window is XCL2.App.Views.MainWindow && WindowChromeService.IsFullScreen);
            var corner = rounded ? (systemCorners ? 8 : radius) : 0;
            content.Clip = new RectangleGeometry(new Rect(0, 0,
                Math.Max(0, content.ActualWidth), Math.Max(0, content.ActualHeight)), corner, corner);
            // Use the compositor on Windows 11; avoid overriding the compositor
            // outline with a custom region. WindowChrome supplies the Windows 10 region fallback.
            var chrome = WindowChrome.GetWindowChrome(window);
            var nativeCorner = systemCorners ? 0 : corner;
            if (chrome != null && chrome.CornerRadius != new CornerRadius(nativeCorner))
                chrome.CornerRadius = new CornerRadius(nativeCorner);
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero && systemCorners)
            {
                var preference = rounded ? 2 : 1;
                if (preference != lastPreference || hwnd != lastHandle)
                {
                    try { _ = DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int)); }
                    catch (EntryPointNotFoundException) { }
                    catch (DllNotFoundException) { }
                    lastPreference = preference;
                    lastHandle = hwnd;
                }
            }
        }
        content.SizeChanged += (_, _) => Update();
        window.StateChanged += (_, _) => Update();
        window.SourceInitialized += (_, _) => Update();
        window.Loaded += (_, _) => Update();
        Update();
    }
}
