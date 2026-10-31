using System.Windows.Input;

namespace XCL2.App.Services;

internal static class LauncherCloseShortcut
{
    internal const string RecordingTag = "LauncherCloseShortcutRecording";

    internal static bool TryParse(string? text, out KeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        try
        {
            if (new KeyGestureConverter().ConvertFromInvariantString(text.Trim()) is not KeyGesture candidate
                || !IsAllowed(candidate.Key, candidate.Modifiers)) return false;
            gesture = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or FormatException) { return false; }
    }

    internal static bool IsAllowed(Key key, ModifierKeys modifiers)
    {
        if (key is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed or Key.Escape
            or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.F3 or Key.F10 or Key.F11 or Key.F12) return false;
        if ((modifiers & ModifierKeys.Windows) != 0 || (key == Key.F4 && modifiers.HasFlag(ModifierKeys.Alt))) return false;
        if (modifiers.HasFlag(ModifierKeys.Alt) && (key is Key.Tab or Key.Space)) return false;
        if (modifiers.HasFlag(ModifierKeys.Control) && modifiers.HasFlag(ModifierKeys.Alt) && key == Key.Delete) return false;
        return modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Alt)
            || ((int)key >= (int)Key.F1 && (int)key <= (int)Key.F24);
    }

    internal static string Format(Key key, ModifierKeys modifiers)
        => new KeyGestureConverter().ConvertToInvariantString(new KeyGesture(key, modifiers)) ?? "";
}
