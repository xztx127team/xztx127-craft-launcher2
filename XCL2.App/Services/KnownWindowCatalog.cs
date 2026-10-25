using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace XCL2.App.Services;

public sealed record KnownWindowInfo(IntPtr Handle, int Pid, string Title, string ProcessName, bool IsForeground);

/// <summary>Visible top-level application windows; discovery never registers or controls a process.</summary>
public static class KnownWindowCatalog
{
    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder title, int count);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    public static IReadOnlyList<KnownWindowInfo> Read()
    {
        var result = new List<KnownWindowInfo>();
        var foreground = GetForegroundWindow();
        var processes = new Dictionary<int, string>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (IsCloaked(hwnd)) return true;
            var title = new StringBuilder(2048);
            if (GetWindowText(hwnd, title, title.Capacity) <= 0) return true;
            GetWindowThreadProcessId(hwnd, out var rawPid);
            if (rawPid == 0 || rawPid > int.MaxValue) return true;
            var pid = (int)rawPid;
            if (!processes.TryGetValue(pid, out var name))
            {
                try { using var process = Process.GetProcessById(pid); name = process.ProcessName; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { name = "进程已退出或无法读取"; }
                processes[pid] = name;
            }
            result.Add(new(hwnd, pid, title.ToString(), name, hwnd == foreground));
            return true;
        }, IntPtr.Zero);
        return result.OrderByDescending(w => w.IsForeground).ThenBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Title).ToList();
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        // DWMWA_CLOAKED begins with Windows 8. Older desktops use ordinary visibility.
        if (!WindowsCompatibilityService.Current.IsAtLeast(6, 2)) return false;
        try { return DwmGetWindowAttribute(hwnd, 14, out var value, sizeof(int)) == 0 && value != 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}
