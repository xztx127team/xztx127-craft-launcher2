using System.Windows;
using System.Windows.Media.Animation;

namespace XCL2.App.Services;

internal static class TransparencyAnimationService
{
    public static void Reset(UIElement element) => element.BeginAnimation(UIElement.OpacityProperty, null);

    public static void Play(UIElement element)
    {
        Reset(element);
        if (!AnimationSettingsService.IsEnabled(AnimationKind.Transparency)) return;
        // Animate only the transition. Never leave an editor invisible or difficult to read at 0%.
        var animation = new DoubleAnimation(AnimationSettingsService.Intensity(AnimationKind.Transparency), 1,
            new Duration(AnimationSettingsService.Duration(220, AnimationKind.Transparency)))
        { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
