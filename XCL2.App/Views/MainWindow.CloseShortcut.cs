using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private HwndSource? _closeShortcutSource;

    private void InitializeCloseShortcut()
    {
        // Register before the other main-window shortcuts; recording fields receive the key themselves.
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.FocusedElement is TextBox { Tag: LauncherCloseShortcut.RecordingTag }) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                if (!ConfigService.Config.EnableAltF4Close) e.Handled = true;
                return;
            }
            if (!LauncherCloseShortcut.TryParse(ConfigService.Config.LauncherCloseShortcut, out var gesture)
                || gesture == null || gesture.Key != key || gesture.Modifiers != Keyboard.Modifiers) return;
            e.Handled = true;
            if (!e.IsRepeat && !_isWindowClosed && !_windowCloseCommitted) Close();
        };
        SourceInitialized += (_, _) =>
        {
            _closeShortcutSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _closeShortcutSource?.AddHook(CloseShortcutHook);
        };
        Closed += (_, _) => { _closeShortcutSource?.RemoveHook(CloseShortcutHook); _closeShortcutSource = null; };
    }

    private IntPtr CloseShortcutHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (ConfigService.Config.EnableAltF4Close) return IntPtr.Zero;
        // Stop the native Alt+F4 path too, without blocking mouse/system-menu close commands.
        if ((message == 0x0104 && wParam.ToInt64() == 0x73 && (lParam.ToInt64() & (1L << 29)) != 0)
            || (message == 0x0112 && (wParam.ToInt64() & 0xFFF0) == 0xF060
                && lParam == IntPtr.Zero && (GetAsyncKeyStateForClose(0x12) & 0x8000) != 0))
            handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short GetAsyncKeyStateForClose(int key);
}
