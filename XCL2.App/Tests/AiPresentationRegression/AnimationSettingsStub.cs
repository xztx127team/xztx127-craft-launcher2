namespace XCL2.App.Services;

public enum AnimationKind { Popup }

public static class AnimationSettingsService
{
    private static Action? _changed;
    public static event Action? Changed { add => _changed += value; remove => _changed -= value; }
    public static int Subscribers => _changed?.GetInvocationList().Length ?? 0;
    public static bool Enabled { get; set; } = true;
    public static double Intensity(AnimationKind kind) => Enabled ? 1 : 0;
    public static TimeSpan Duration(int milliseconds, AnimationKind kind) => Enabled ? TimeSpan.FromMilliseconds(500) : TimeSpan.Zero;
    public static void NotifyChanged() => _changed?.Invoke();
}
