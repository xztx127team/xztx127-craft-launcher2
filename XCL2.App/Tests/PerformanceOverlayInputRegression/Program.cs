using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;
using XCL2.App.Views;

internal static class Program
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources["PanelBrush"] = Brushes.Black;
        app.Resources["TextPrimaryBrush"] = Brushes.White;
        app.Resources["TextSecondaryBrush"] = Brushes.LightGray;
        app.Resources["DividerBrush"] = Brushes.Gray;
        var cfg = new AppConfig { PerformanceOverlayOpacityPercent = 90, PerformanceOverlayFontSize = 12 };
        Check(!cfg.PerformanceOverlayClickThrough, "Fresh configuration defaults to ordinary interactive display");
        Check(!JsonSerializer.Deserialize<AppConfig>("{}")!.PerformanceOverlayClickThrough,
            "Configurations without a stored preference use the new ordinary-display default");
        Check(JsonSerializer.Deserialize<AppConfig>("{\"PerformanceOverlayClickThrough\":true}")!.PerformanceOverlayClickThrough
            && !JsonSerializer.Deserialize<AppConfig>("{\"PerformanceOverlayClickThrough\":false}")!.PerformanceOverlayClickThrough,
            "Loading retains both explicit saved choices instead of resetting user preferences");
        PerformanceOverlayWindow? overlay = null;
        overlay = new PerformanceOverlayWindow(() => cfg, update => { update(cfg); overlay?.ApplySettings(); }, () => { },
            (left, top) => { cfg.PerformanceOverlayLeft = left; cfg.PerformanceOverlayTop = top; });
        overlay.Show(); Pump();
        var bodyHwnd = new WindowInteropHelper(overlay).Handle;
        var card = (Border)overlay.FindName("OverlayCard");
        var scroll = (ScrollViewer)overlay.FindName("OverlayScroll");
        Check((GetWindowLong(bodyHwnd, -20) & 0x20) == 0 && card.IsHitTestVisible,
            "Ordinary default permits body dragging and the original right-click menu");
        Check(!app.Windows.OfType<PerformanceOverlayHandleWindow>().Any() && scroll.Margin.Right == 0,
            "Ordinary mode has no settings HWND or reserved input/content gutter");
        var firstText = (TextBlock)overlay.FindName("LayoutText");
        Check(firstText.TranslatePoint(new Point(0, 0), overlay).Y <= 11,
            "Metrics begin at the normal top padding with no title row or blank line");
        cfg.PerformanceOverlayClickThrough = true; overlay.ApplySettings(); Pump();
        var handle = app.Windows.OfType<PerformanceOverlayHandleWindow>().Single();
        var handleHwnd = new WindowInteropHelper(handle).Handle;
        Check((GetWindowLong(bodyHwnd, -20) & 0x80020) == 0x80020,
            "Actual overlay body/header must keep layered+transparent for cross-process pass-through");
        Check((GetWindowLong(handleHwnd, -20) & 0x080000A0) == 0x08000080,
            "Settings HWND is a no-activate tool window without WS_EX_TRANSPARENT");
        Check(SendMessage(handleHwnd, 0x0021, IntPtr.Zero, IntPtr.Zero).ToInt32() == 3,
            "Settings handle declines mouse activation");
        GetWindowRect(handleHwnd, out var rect);
        var dpi = VisualTreeHelper.GetDpi(overlay);
        Check(rect.Right - rect.Left == (int)Math.Ceiling(14 * dpi.DpiScaleX)
            && rect.Bottom - rect.Top == (int)Math.Ceiling(14 * dpi.DpiScaleY), "Input island must be only 14 DIPs, half the previous 28-DIP size");
        var gearPoint = overlay.PointToScreen(new Point(overlay.ActualWidth - 20, 6));
        Check(Math.Abs(rect.Left - gearPoint.X) <= 1 && Math.Abs(rect.Top - gearPoint.Y) <= 1,
            "Handle tracks the actual screen coordinates of the overlay corner");
        Check(scroll.Margin.Right >= 14, "Passthrough reserves horizontal space without inserting a header row");
        handle.FontFamily = new FontFamily("Arial");
        Check(((Button)handle.Content).Content is System.Windows.Shapes.Path { Data: not null },
            "The settings cog is font-independent vector geometry rather than a missing glyph");
        // Native style rewrites simulate WPF visibility/topmost transitions after initial creation.
        SetWindowLong(bodyHwnd, -20, GetWindowLong(bodyHwnd, -20) & ~0x20);
        Check((GetWindowLong(bodyHwnd, -20) & 0x20) != 0, "A style rewrite cannot silently disable body pass-through");
        SetWindowLong(handleHwnd, -20, GetWindowLong(handleHwnd, -20) | 0x20);
        Check((GetWindowLong(handleHwnd, -20) & 0x20) == 0, "A style rewrite cannot make settings unreachable");
        cfg.PerformanceOverlayAlwaysOnTop = false; overlay.ApplySettings(); Pump();
        cfg.PerformanceOverlayAlwaysOnTop = true; overlay.ApplySettings(); Pump();
        Check((GetWindowLong(bodyHwnd, -20) & 0x20) != 0, "Topmost toggles preserve pass-through");
        var memoryMenu = new ContextMenu();
        MemoryOptimizationMenu.AddMenu(memoryMenu, cfg, update => update(cfg));
        Check(memoryMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "内存优化")), "Immediate memory cleanup is available");
        var schedule = memoryMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "定时执行内存优化"));
        Check(cfg.MemoryOptimizationIntervalMinutes == 0, "Scheduled memory cleanup is off by default");
        var everyThirty = schedule.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "每 30 分钟"));
        everyThirty.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(cfg.MemoryOptimizationIntervalMinutes == 30, "A schedule persists the selected interval");
        var disable = schedule.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "关闭"));
        disable.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(cfg.MemoryOptimizationIntervalMinutes == 0, "The schedule can be disabled");
        var menu = card.ContextMenu;
        ((Button)handle.Content).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
        Check(menu.IsOpen && ReferenceEquals(menu.PlacementTarget, handle.Content), "Gear opens the overlay's original shared menu");
        var dismissal = (MenuOutsideClickDismissal)typeof(PerformanceOverlayWindow)
            .GetField("_menuDismissal", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(overlay)!;
        Check(dismissal.IsListening, "Outside-click listening is enabled only while the menu is open");
        dismissal.ProcessPointerDown(menu.PointToScreen(new Point(8, 8))); Pump();
        Check(menu.IsOpen, "Clicking the settings menu preserves its controls");
        var styleMenu = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "显示样式"));
        styleMenu.IsSubmenuOpen = true; Pump();
        var submenu = (Popup)styleMenu.Template.FindName("PART_Popup", styleMenu);
        var child = (FrameworkElement)submenu.Child;
        dismissal.ProcessPointerDown(child.PointToScreen(new Point(8, 8))); Pump();
        Check(menu.IsOpen && styleMenu.IsSubmenuOpen, "Submenu clicks are inside the settings surface");
        dismissal.ProcessPointerDown(new Point(-100000, -100000)); Pump();
        Check(!menu.IsOpen && !dismissal.IsListening, "Outside click dismisses without a focus change and releases listeners");
        ((Button)handle.Content).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
        dismissal.ProcessPointerDown(new Point(-100000, -100000));
        menu.IsOpen = false;
        ((Button)handle.Content).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
        Check(menu.IsOpen, "A queued click from a previous opening cannot close the newly opened menu");
        var toggle = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "鼠标穿透"));
        toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
        Check(!cfg.PerformanceOverlayClickThrough && (GetWindowLong(bodyHwnd, -20) & 0x20) == 0,
            "The shared menu disables pass-through on the real HWND");
        Check(card.IsHitTestVisible, "Disabling passthrough permits content dragging/scrolling");
        Check(!IsWindow(handleHwnd) && !app.Windows.OfType<PerformanceOverlayHandleWindow>().Any()
            && !menu.IsOpen && scroll.Margin.Right == 0,
            "Disabling passthrough destroys the gear HWND, closes its menu and releases its content gutter");
        var previousHandle = handle;
        cfg.PerformanceOverlayClickThrough = true; overlay.ApplySettings(); Pump();
        handle = app.Windows.OfType<PerformanceOverlayHandleWindow>().Single();
        handleHwnd = new WindowInteropHelper(handle).Handle;
        Check(!ReferenceEquals(previousHandle, handle), "Reenabling creates exactly one fresh reachable settings handle");
        overlay.Hide(); Pump();
        Check(!handle.IsVisible && !menu.IsOpen, "Hide dismisses menu and compact handle");
        overlay.Show(); Pump();
        Check(handle.IsVisible && (GetWindowLong(bodyHwnd, -20) & 0x20) != 0, "Show restores reachable gear and pass-through");
        overlay.Close(); Pump();
        Check(!IsWindow(handleHwnd) && !app.Windows.OfType<PerformanceOverlayHandleWindow>().Any(), "Close destroys the owned handle without an orphan");
        app.Shutdown();
        Console.WriteLine("Performance overlay native HWND/lifecycle checks passed. Real cross-process pointer/drag and mixed-DPI checks remain manual.");
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
}

namespace XCL2.App.Models
{
    // Settings outside the production partials linked by this focused native harness.
    public sealed partial class AppConfig
    {
        public bool PerformanceOverlayEnabled { get; set; } = true;
        public bool PerformanceOverlayAlwaysOnTop { get; set; } = true;
        public bool PerformanceOverlayForceTopmost { get; set; } = true;
        public bool PerformanceOverlayLockPosition { get; set; }
        public double? PerformanceOverlayLeft { get; set; }
        public double? PerformanceOverlayTop { get; set; }
    }
}
namespace XCL2.App.Views
{
    // Dialog content is covered by hardware-detail tests; these native tests exercise its menu entry only.
    public sealed class PerformanceHardwareDetailsDialog : Window
    {
        public PerformanceHardwareDetailsDialog(AppConfig cfg, Action<Action<AppConfig>> update) { }
    }
}

namespace XCL2.App.Services
{
    internal static class MemoryOptimizerService
    {
        public sealed record PhysicalMemory(ulong TotalMb, ulong AvailableMb);
        public static PhysicalMemory? ReadPhysicalMemory() => null;
    }
    internal static class ToastService { public static void ShowSuccess(string message) { } }
    internal static class ErrorPresenter
    { public static void ShowFriendlyError(string summary, string detail, string title) { } }
}
