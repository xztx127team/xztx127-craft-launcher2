using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>在普通 Topmost 之上维护 Z 序；只提升自己的窗口，绝不修改别的软件或抢焦点。</summary>
internal static class ForcedTopmostService
{
    private sealed record Entry(Window Window, Func<bool> ForceEnabled, int Priority);
    private static readonly List<Entry> Entries = new();
    private static readonly DispatcherTimer Timer = new(DispatcherPriority.Background)
    { Interval = TimeSpan.FromMilliseconds(250) };
    private static readonly DependencyPropertyDescriptor TopmostDescriptor =
        DependencyPropertyDescriptor.FromProperty(Window.TopmostProperty, typeof(Window));

    static ForcedTopmostService() => Timer.Tick += (_, _) => Refresh();

    public static void Attach(Window window, Func<bool> forceEnabled, int priority = 0)
    {
        if (Entries.Any(entry => ReferenceEquals(entry.Window, window))) return;
        var entry = new Entry(window, forceEnabled, priority);
        Entries.Add(entry);
        void Changed(object? sender, EventArgs args) => Refresh();
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Refresh();
        window.SourceInitialized += Changed;
        window.StateChanged += Changed;
        window.IsVisibleChanged += VisibilityChanged;
        TopmostDescriptor.AddValueChanged(window, Changed);
        window.Closed += (_, _) =>
        {
            Entries.Remove(entry);
            TopmostDescriptor.RemoveValueChanged(window, Changed);
            window.SourceInitialized -= Changed;
            window.StateChanged -= Changed;
            window.IsVisibleChanged -= VisibilityChanged;
            Refresh();
        };
        Refresh();
    }

    public static void Refresh()
    {
        var active = Entries.Where(entry => entry.Window.IsVisible && entry.Window.Topmost
            && entry.Window.WindowState != WindowState.Minimized && entry.ForceEnabled()).OrderBy(entry => entry.Priority).ToArray();
        if (active.Length == 0) { Timer.Stop(); return; }
        Timer.Start();
        foreach (var entry in active)
        {
            var hwnd = new WindowInteropHelper(entry.Window).Handle;
            if (hwnd == IntPtr.Zero || !HasForeignTopmostAbove(hwnd)) continue;
            // HWND_TOPMOST + NOACTIVATE: retain focus in the current game/app and retain our input styles.
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
        }
    }

    private static bool HasForeignTopmostAbove(IntPtr hwnd)
    {
        // Bounded walk prevents pathological external Z-order churn from blocking the dispatcher.
        var above = GetWindow(hwnd, 3);
        for (var remaining = 512; above != IntPtr.Zero && remaining-- > 0; above = GetWindow(above, 3))
        {
            if (!IsWindowVisible(above)) continue;
            GetWindowThreadProcessId(above, out var process);
            if (process == (uint)Environment.ProcessId) continue;
            return true;
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
