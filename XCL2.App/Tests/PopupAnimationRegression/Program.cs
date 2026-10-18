using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var card = new Border { Width = 160, Height = 80, Background = Brushes.White,
            Child = new TextBlock { Text = "Popup animation regression", Margin = new Thickness(8) } };
        var window = new Window { Width = 300, Height = 200, Content = card, ShowInTaskbar = false };
        try
        {
            window.Show();
            PopupKeepsVisibleFirstFrameDuringColdLayout(card);
            PopupIntensityUsesSameStartForPreparationAndAnimation(card);
            DisabledPopupIsImmediatelyOpaque(card);
            AnimationPreferenceChangeStillNotifiesHost(card);
            DismissCancelsPendingEntrance(card);
            ReopenCancelsOldDismissCallback(card);
            OverlayScaleWaitsForFirstRender(card);
            OverlayScaleRespectsIntensity(card);
            WindowRestoreActuallyAnimatesScale(card);
            DisabledOverlayRestoresItsScale(card);
            CanceledEntranceDoesNotNotifyHost(card);
            HiddenEntranceRestoresItsScale(card);
            Console.WriteLine("PASS: 12 popup animation regression checks.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            UiAnimationHelper.Reset(card);
            window.Close();
            Application.Current.Shutdown();
        }
    }

    private static void PopupKeepsVisibleFirstFrameDuringColdLayout(FrameworkElement card)
    {
        AnimationSettingsService.Strength = 1;
        UiAnimationHelper.PopupFadeIn(card);
        AssertNear(card.Opacity, 0.15, "The preparing popup must be visible on its first frame.");
        // Simulate first layout/Loaded work taking longer than the entrance duration.
        Thread.Sleep(350);
        AssertNear(card.Opacity, 0.15, "The entrance clock must not elapse before the first render.");
        PumpUntil(() => card.Opacity > 0.2 && card.Opacity < 0.95,
            "The popup must animate after the cold layout finishes.");
        PumpUntil(() => card.Opacity >= 0.999, "The entrance must finish opaque.");
    }

    private static void PopupIntensityUsesSameStartForPreparationAndAnimation(FrameworkElement card)
    {
        AnimationSettingsService.Strength = 0.25;
        UiAnimationHelper.PopupFadeIn(card);
        AssertNear(card.Opacity, 0.7875, "Low intensity must reduce the opacity change.");
        PumpUntil(() => card.Opacity > 0.80 && card.Opacity < 0.99,
            "The prepared intensity must continue into the actual animation.");
        PumpUntil(() => card.Opacity >= 0.999, "The low intensity entrance must settle.");
    }

    private static void DisabledPopupIsImmediatelyOpaque(FrameworkElement card)
    {
        AnimationSettingsService.Strength = 0;
        UiAnimationHelper.PopupFadeIn(card);
        AssertNear(card.Opacity, 1, "Disabled animations must not wait for a render.");
    }

    private static void DismissCancelsPendingEntrance(FrameworkElement card)
    {
        AnimationSettingsService.Strength = 1;
        UiAnimationHelper.PopupFadeIn(card);
        var closed = false;
        UiAnimationHelper.FadeOut(card, () => closed = true, card.Opacity);
        PumpUntil(() => closed, "A popup dismissed before its first frame must close.");
        PumpFor(300);
        AssertNear(card.Opacity, 0, "A pending entrance must not reopen a dismissed popup.");
    }

    private static void AnimationPreferenceChangeStillNotifiesHost(FrameworkElement card)
    {
        AnimationSettingsService.Strength = 1;
        var notified = false;
        UiAnimationHelper.PopupFadeIn(card, () => notified = true);
        AnimationSettingsService.Strength = 0;
        AnimationSettingsService.NotifyChanged();
        PumpUntil(() => notified, "Disabling animations during preparation must still notify the host.");
        AssertNear(card.Opacity, 1, "A settings change must leave the popup fully visible.");
    }

    private static void ReopenCancelsOldDismissCallback(FrameworkElement card)
    {
        var staleClosed = false;
        UiAnimationHelper.FadeOut(card, () => staleClosed = true);
        UiAnimationHelper.PopupFadeIn(card);
        PumpUntil(() => card.Opacity >= 0.999, "A replacement entrance must settle.");
        Assert(!staleClosed, "The old dismiss callback must not close the replacement popup.");
    }

    private static void OverlayScaleWaitsForFirstRender(FrameworkElement card)
    {
        AnimationSettingsService.Strength = 1;
        var scale = new ScaleTransform();
        card.RenderTransform = scale;
        var shown = false;
        UiAnimationHelper.PopupFadeIn(card, () => shown = true, scale);
        AssertNear(scale.ScaleX, 0.94, "Overlay preparation must include the visible scale start.");
        Thread.Sleep(350);
        AssertNear(scale.ScaleX, 0.94, "Cold layout must not consume the scale animation.");
        PumpUntil(() => scale.ScaleX > 0.95 && scale.ScaleX < 0.999,
            "An overlay must actually animate its supplied scale transform.");
        PumpUntil(() => shown, "The host must be notified after the entrance completes.");
        AssertNear(scale.ScaleX, 1, "The overlay must settle at its original size.");
        AssertNear(scale.ScaleY, 1, "Both scale axes must settle.");
        Assert(!scale.HasAnimatedProperties, "Finished scale animations must remove their clocks.");
    }

    private static void OverlayScaleRespectsIntensity(FrameworkElement card)
    {
        AnimationSettingsService.Strength = 0.25;
        var scale = new ScaleTransform();
        card.RenderTransform = scale;
        var shown = false;
        UiAnimationHelper.PopupFadeIn(card, () => shown = true, scale);
        AssertNear(scale.ScaleX, 0.985, "Popup intensity must scale down the size change.");
        PumpUntil(() => shown, "The low intensity scale animation must finish.");
        AssertNear(scale.ScaleX, 1, "Low intensity must not change the final size.");
    }

    private static void WindowRestoreActuallyAnimatesScale(FrameworkElement card)
    {
        AnimationSettingsService.Strength = 1;
        var scale = new ScaleTransform();
        card.RenderTransform = scale;
        var finished = false;
        UiAnimationHelper.SettleScale(card, scale, () => finished = true);
        PumpUntil(() => scale.ScaleX > 0.96 && scale.ScaleX < 0.999,
            "Window restore must use the scale passed to SettleScale.");
        PumpUntil(() => finished, "The window restore must finish.");
        AssertNear(scale.ScaleX, 1, "Window restore must return to its natural size.");
    }

    private static void DisabledOverlayRestoresItsScale(FrameworkElement card)
    {
        AnimationSettingsService.Strength = 0;
        var scale = new ScaleTransform(0.5, 0.5);
        card.RenderTransform = scale;
        var shown = false;
        UiAnimationHelper.PopupFadeIn(card, () => shown = true, scale);
        Assert(shown, "Disabled overlay entrance must notify synchronously.");
        AssertNear(card.Opacity, 1, "Disabled overlay entrance must be fully opaque.");
        AssertNear(scale.ScaleX, 1, "Disabled entrance must reset a retained external scale.");
    }

    private static void CanceledEntranceDoesNotNotifyHost(FrameworkElement card)
    {
        AnimationSettingsService.Strength = 1;
        var shown = false;
        UiAnimationHelper.PopupFadeIn(card, () => shown = true);
        UiAnimationHelper.FadeOut(card);
        PumpUntil(() => card.Opacity < 0.001, "The replacement dismissal must finish.");
        PumpFor(100);
        Assert(!shown, "A canceled entrance must not publish stale dialog-open callbacks.");
    }

    private static void HiddenEntranceRestoresItsScale(FrameworkElement card)
    {
        AnimationSettingsService.Strength = 1;
        var scale = new ScaleTransform();
        card.RenderTransform = scale;
        UiAnimationHelper.PopupFadeIn(card, scale: scale);
        card.Visibility = Visibility.Collapsed;
        PumpFor(300);
        AssertNear(scale.ScaleX, 1, "A hidden preparation must not retain a shrunken transform.");
        AssertNear(card.Opacity, 1, "A hidden preparation must restore its base opacity.");
        card.Visibility = Visibility.Visible;
        PumpFor(20);
    }

    private static void PumpUntil(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline) PumpFor(10);
        Assert(condition(), failure);
    }

    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void AssertNear(double actual, double expected, string failure)
        => Assert(Math.Abs(actual - expected) < 0.001, failure + $" Expected {expected}, got {actual}.");

    private static void Assert(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }
}
