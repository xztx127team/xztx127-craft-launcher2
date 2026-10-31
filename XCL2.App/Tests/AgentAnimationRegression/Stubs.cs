using XCL2.App.Models;

namespace XCL2.App.Models
{
    public enum AiWorkMode { Build = 0, Plan = 1, Agent = 2 }
    public sealed class AiAssistantConfig { public AiWorkMode WorkMode { get; set; } }
    public sealed class AppConfig
    {
        public AiAssistantConfig AiAssistant { get; set; } = new();
        public Dictionary<string, AnimationOption> AnimationSettings { get; set; } = new();
    }
}

namespace XCL2.App.Services
{
    // Only the UI preference host is stubbed. The production profile service and
    // AnimationOption are linked above; these counters expose preview/notification calls.
    public enum AnimationKind
    {
        WindowOpen, WindowMinimize, WindowRestore, WindowFullscreen, WindowClose,
        Page, Popup, Sidebar, ControlFeedback, Toast, Loading, SmoothScroll,
        SkinMotion, SkinAttack, SkinHurt, Transparency
    }
    public static class AnimationSettingsService
    {
        public static readonly IReadOnlyList<(AnimationKind Kind, string Label)> Options =
            Enum.GetValues<AnimationKind>().Select(kind => (kind, kind.ToString())).ToArray();
        public static int ClearPreviewCalls;
        public static int NotifyChangedCalls;
        public static AnimationOption GetOption(AppConfig config, AnimationKind kind)
            => config.AnimationSettings.TryGetValue(kind.ToString(), out var option)
                ? new AnimationOption { Enabled = option.Enabled, IntensityPercent = option.IntensityPercent }
                : new AnimationOption();
        public static void ClearPreview() => ClearPreviewCalls++;
        public static void NotifyChanged() => NotifyChangedCalls++;
    }
}
