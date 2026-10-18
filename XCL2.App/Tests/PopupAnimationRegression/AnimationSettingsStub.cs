namespace XCL2.App.Services;

public enum AnimationKind
{
    WindowOpen, WindowMinimize, WindowRestore, WindowFullscreen, WindowClose,
    Page, Popup, Sidebar
}

public static class AnimationSettingsService
{
    public static double Strength { get; set; } = 1;
    public static double SpeedRatio => 0.75;
    public static event Action? Changed;
    public static double Intensity(AnimationKind kind) => Strength;
    public static TimeSpan Duration(int milliseconds, AnimationKind kind) => TimeSpan.FromMilliseconds(milliseconds);
    public static void NotifyChanged() => Changed?.Invoke();
}
