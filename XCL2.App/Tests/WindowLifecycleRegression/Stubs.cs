namespace XCL2.App.Services;

// The harness exercises real WPF HWNDs while isolating application configuration.
public enum AnimationKind { WindowOpen, WindowMinimize, WindowRestore, WindowFullscreen, WindowClose }
public static class AnimationSettingsService
{
    public static readonly HashSet<AnimationKind> Disabled = new();
    public static readonly List<(AnimationKind Kind, bool Enabled)> Requests = new();
    public static bool IsEnabled(AnimationKind kind)
    {
        var enabled = !Disabled.Contains(kind);
        Requests.Add((kind, enabled));
        return enabled;
    }
}
