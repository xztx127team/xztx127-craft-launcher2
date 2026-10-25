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
            WindowRestoreKeepsNaturalSize(card);
            DisabledOverlayRestoresItsScale(card);
            CanceledEntranceDoesNotNotifyHost(card);
            HiddenEntranceRestoresItsScale(card);
            NestedPagesUseForwardAndBackwardDirections(card);
            ReplacedRenderTransformStillAnimates(card);
            CancelingNavigationResetsBothAxes(card);
            VisibleContentReplacementAnimates(window);
            WindowRestoreRunsAfterPresentation();
            RepeatedStandalonePopupAnimates(window);
            StartupConflictPromptAnimatesEveryShow();
            DeferredRestoreWaitsForContentVisibility();
            OwnedPromptsPreserveOwnerAndResults(window);
            OwnedPromptsPreserveEscapeAndCloseSemantics(window);
            OwnedPromptEndsWhenItsOwnerCloses();
            OwnedPromptsIgnoreUnavailableOwners();
            Console.WriteLine("PASS: 24 popup, nested page and window presentation regression checks.");
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
        AssertNear(scale.ScaleX, 0.985, "Overlay preparation must include the visible scale start.");
        Thread.Sleep(350);
        AssertNear(scale.ScaleX, 0.985, "Cold layout must not consume the scale animation.");
        PumpUntil(() => scale.ScaleX > 0.9855 && scale.ScaleX < 0.999,
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
        AssertNear(scale.ScaleX, 0.99625, "Popup intensity must scale down the size change.");
        PumpUntil(() => shown, "The low intensity scale animation must finish.");
        AssertNear(scale.ScaleX, 1, "Low intensity must not change the final size.");
    }

    private static void WindowRestoreKeepsNaturalSize(FrameworkElement card)
    {
        AnimationSettingsService.Strength = 1;
        var scale = new ScaleTransform();
        card.RenderTransform = scale;
        var finished = false;
        UiAnimationHelper.SettleScale(card, scale, () => finished = true);
        AssertNear(scale.ScaleX, 1, "Window restore must preserve natural geometry.");
        PumpUntil(() => card.Opacity > 0.92 && card.Opacity < 0.999,
            "Window restore uses a subtle fade without stretching content.");
        AssertNear(scale.ScaleX, 1, "Window restore must not shrink text while fading.");
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

    private static void VisibleContentReplacementAnimates(Window window)
    {
        AnimationSettingsService.Strength = 1;
        var host = new ContentControl { Content = new TextBlock { Text = "First detail page" } };
        PageTransitionBehavior.SetAnimateOnVisible(host, true);
        window.Content = host;
        PumpUntil(() => host.IsLoaded && host.Opacity >= 0.999, "The initial detail page must settle.");

        host.Content = new TextBlock { Text = "Second detail page" };
        PumpUntil(() => host.Opacity < 0.95, "Replacing a visible detail page must start a transition.");
        host.Content = new TextBlock { Text = "Third detail page" };
        PumpUntil(() => host.Opacity >= 0.999, "Rapid page replacement must settle on the latest page.");
        Assert(host.IsEnabled && host.IsHitTestVisible, "Page transitions must preserve pointer interaction.");

        PageTransitionBehavior.SetAnimateOnVisible(host, false);
        host.Content = new TextBlock { Text = "Animations disabled" };
        PumpFor(30);
        AssertNear(host.Opacity, 1, "Disabling the behavior must stop observing content replacements.");
    }

    private static void NestedPagesUseForwardAndBackwardDirections(FrameworkElement card)
    {
        AnimationSettingsService.Strength = 1;
        UiAnimationHelper.PrepareEntrance(card, depth: 3, direction: 1);
        AssertNear(card.RenderTransform.Value.OffsetX, 28, "Third-level content prepares a forward horizontal entrance.");
        Thread.Sleep(300);
        AssertNear(card.RenderTransform.Value.OffsetX, 28, "A cold third-level page must retain its prepared position before rendering.");
        UiAnimationHelper.StartPreparedEntrance(card);
        PumpUntil(() => card.RenderTransform.Value.OffsetX is > 1 and < 25, "Third-level forward movement must actually render.");
        PumpUntil(() => card.Opacity >= .999, "Third-level forward entrance settles.");
        UiAnimationHelper.PrepareEntrance(card, depth: 2, direction: -1);
        AssertNear(card.RenderTransform.Value.OffsetX, -18, "Back navigation reverses direction at the second level.");
        UiAnimationHelper.StartPreparedEntrance(card);
        PumpUntil(() => card.Opacity >= .999, "Back navigation settles.");
        AssertNear(card.RenderTransform.Value.OffsetX, 0, "Navigation must leave no horizontal offset.");
    }

    private static void ReplacedRenderTransformStillAnimates(FrameworkElement card)
    {
        var replacement = new ScaleTransform();
        card.RenderTransform = replacement;
        UiAnimationHelper.PrepareEntrance(card, depth: 3);
        AssertNear(card.RenderTransform.Value.OffsetX, 28, "Replacing a theme transform must not detach the cached page animation transform.");
        UiAnimationHelper.StartPreparedEntrance(card);
        PumpUntil(() => card.Opacity >= .999, "The page still animates after its transform was replaced.");
        AssertNear(replacement.ScaleX, 1, "Page transitions preserve an existing scale transform.");
    }

    private static void CancelingNavigationResetsBothAxes(FrameworkElement card)
    {
        UiAnimationHelper.PrepareEntrance(card, depth: 3, direction: -1);
        UiAnimationHelper.StartPreparedEntrance(card);
        UiAnimationHelper.Reset(card);
        PumpFor(300);
        AssertNear(card.Opacity, 1, "Canceled navigation must remain fully opaque.");
        AssertNear(card.RenderTransform.Value.OffsetX, 0, "Canceled navigation clears horizontal movement.");
        AssertNear(card.RenderTransform.Value.OffsetY, 0, "Canceled navigation clears vertical movement.");
    }

    private static void WindowRestoreRunsAfterPresentation()
    {
        AnimationSettingsService.Strength = 1;
        var scale = new ScaleTransform();
        var surface = new Border { Background = Brushes.White, RenderTransform = scale };
        var window = new Window { Width = 320, Height = 220, Content = surface, ShowInTaskbar = false };
        using var motion = new WindowPresentationMotion(window, surface, scale);
        var presented = false;
        window.ContentRendered += (_, _) => presented = true;
        try
        {
            window.Show();
            PumpUntil(() => presented, "The initial window frame must render.");
            AssertNear(surface.Opacity, 1, "First startup retains fully visible content.");
            window.Hide();
            window.Show();
            Assert(surface.Opacity < .999, "Showing a tray-hidden window must prepare a restore even when WindowState stays Normal.");
            Thread.Sleep(300);
            Assert(surface.Opacity < .999, "Restore duration must not elapse before layout/rendering resumes.");
            PumpUntil(() => surface.Opacity is > .80 and < .99, "The restored frame must visibly animate.");
            PumpUntil(() => surface.Opacity >= .999, "Restore finishes at full opacity.");
            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.Normal;
            PumpUntil(() => surface.Opacity >= .999, "Rapid maximize/restore must settle on the latest state.");
            AssertNear(scale.ScaleX, 1, "Native restore never stretches window text.");
            AnimationSettingsService.Strength = 0;
            window.Hide();
            window.Show();
            AssertNear(surface.Opacity, 1, "Disabled motion leaves tray restore immediately opaque.");
        }
        finally { window.Close(); AnimationSettingsService.Strength = 1; }
    }

    private static void RepeatedStandalonePopupAnimates(Window owner)
    {
        DrawerMotionBehavior.Register();
        AnimationSettingsService.Strength = 1;
        var content = new Border { Width = 150, Height = 70, Background = Brushes.White };
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = owner, Child = content, StaysOpen = true, AllowsTransparency = true
        };
        try
        {
            for (var cycle = 0; cycle < 2; cycle++)
            {
                popup.IsOpen = true;
                PumpUntil(() => content.IsLoaded && content.Opacity < .99, "Every standalone popup opening must prepare an entrance.");
                PumpUntil(() => content.Opacity >= .999, "The standalone popup entrance must settle.");
                popup.IsOpen = false;
                PumpFor(30);
                AssertNear(content.Opacity, 1, "Closing a popup clears its animation state.");
            }
        }
        finally { popup.IsOpen = false; }
    }

    private static void StartupConflictPromptAnimatesEveryShow()
    {
        Application.Current.Resources["SecondaryButton"] = new Style(typeof(Button));
        Application.Current.Resources["PrimaryButton"] = new Style(typeof(Button));
        Application.Current.Resources["PanelBrush"] = Brushes.White;
        Application.Current.Resources["BorderBrush2"] = Brushes.Gray;
        Application.Current.Resources["TextPrimaryBrush"] = Brushes.Black;
        Application.Current.Resources["TextSecondaryBrush"] = Brushes.Gray;
        AnimationSettingsService.Strength = .3;
        var prompt = new XCL2.App.Views.InstanceConflictDialog();
        try
        {
            var card = (Border)prompt.Content;
            for (var cycle = 0; cycle < 2; cycle++)
            {
                prompt.Show();
                Assert(card.Opacity < .999, "The pre-main-window conflict prompt prepares on every show.");
                PumpUntil(() => card.Opacity >= .999, "The standalone conflict prompt finishes its entrance.");
                prompt.Hide();
            }
            AnimationSettingsService.Strength = 0;
            prompt.Show();
            AssertNear(card.Opacity, 1, "Disabled motion also applies before the main window exists.");
            var buttons = ((StackPanel)card.Child).Children.OfType<Button>().ToArray();
            Assert(buttons.Length == 4 && buttons.All(button => button.HorizontalContentAlignment == HorizontalAlignment.Center
                && button.VerticalContentAlignment == VerticalAlignment.Center), "All four real conflict buttons center their labels.");
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(prompt.Result == SingleInstanceService.ConflictChoice.KeepBothRunning,
                "Motion must not change the conflict choice result.");
        }
        finally { if (prompt.IsVisible) prompt.Close(); AnimationSettingsService.Strength = 1; }
    }

    private static void DeferredRestoreWaitsForContentVisibility()
    {
        AnimationSettingsService.Strength = 1;
        var surface = new Border { Width = 180, Height = 100, Background = Brushes.White };
        var window = new Window { Width = 320, Height = 220, Content = surface, ShowInTaskbar = false };
        var completed = false;
        UiAnimationHelper.SettleScale(surface, new ScaleTransform(), () => completed = true);
        Assert(!surface.IsLoaded && surface.Opacity < .999, "A restore can prepare before the content Loaded event.");
        surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); // queued old Hide cycle
        Thread.Sleep(300);
        Assert(!completed && surface.Opacity < .999, "Deferred visibility must not consume the restore clock.");
        try
        {
            window.Show();
            PumpUntil(() => surface.Opacity is > .80 and < .99, "The pending restore starts after content becomes visible.");
            PumpUntil(() => completed, "The delayed restore must finish normally.");
        }
        finally { window.Close(); }
    }

    private static void OwnedPromptsPreserveOwnerAndResults(Window owner)
    {
        AnimationSettingsService.Strength = .3;
        var answer = InteractWithOwnedPrompt(owner, "Owned save prompt",
            () => XCL2.App.Views.OwnedMessageDialog.Show(owner, "AI 助手设置尚未保存，是否保存本次修改？", "Owned save prompt",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question),
            dialog =>
            {
                var buttons = ((Grid)dialog.Content).Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
                Assert(buttons.Select(button => (string)button.Content).SequenceEqual(new[] { "是", "否", "取消" }),
                    "Owned save prompts preserve native button texts and order.");
                Assert(buttons[0].IsDefault, "The original Yes default remains unchanged.");
                buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
        Assert(answer == MessageBoxResult.No, "Choosing No must not be confused with Cancel or Yes.");
        var canceled = InteractWithOwnedPrompt(owner, "Owned canceled prompt",
            () => XCL2.App.Views.OwnedMessageDialog.Show(owner, "Keep editing", "Owned canceled prompt", MessageBoxButton.YesNoCancel),
            dialog => dialog.Close());
        Assert(canceled == MessageBoxResult.Cancel, "Closing the independent confirmation maps to Cancel.");
        var custom = InteractWithOwnedPrompt(owner, "Owned custom prompt",
            () => XCL2.App.Views.OwnedMessageDialog.ConfirmCustom(owner, "Keep conversation history", "Owned custom prompt", "继续使用", "确认关闭")
                ? MessageBoxResult.Yes : MessageBoxResult.No,
            dialog =>
            {
                var buttons = ((Grid)dialog.Content).Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
                Assert((string)buttons[0].Content == "继续使用" && buttons[0].IsDefault,
                    "Terms revocation retains the safe custom default.");
                buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
        Assert(custom == MessageBoxResult.No, "The custom cancel action must remain negative.");
        AnimationSettingsService.Strength = 1;
    }

    private static MessageBoxResult InteractWithOwnedPrompt(Window owner, string title, Func<MessageBoxResult> show, Action<Window> act)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(10) };
        Exception? failure = null;
        var observedEntrance = false;
        var ownerHandle = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        timer.Tick += (_, _) =>
        {
            var dialog = owner.OwnedWindows.Cast<Window>().FirstOrDefault(candidate => candidate.Title == title);
            if (dialog == null) return;
            try
            {
                Assert(ReferenceEquals(dialog.Owner, owner) && !dialog.ShowInTaskbar,
                    "A floating prompt must remain owned by its invoking window without a second taskbar entry.");
                Assert(!IsWindowEnabled(ownerHandle), "The original settings owner remains modal-disabled while its prompt is open.");
                if (dialog.Content is FrameworkElement content && content.Opacity < .999) observedEntrance = true;
                if (dialog.Content is FrameworkElement { Opacity: < .999 } && DateTime.UtcNow < deadline) return;
                timer.Stop();
                act(dialog);
            }
            catch (Exception error)
            {
                failure = error;
                timer.Stop();
                // YesNo intentionally rejects Close. Dismiss through a real response so a failing
                // assertion cannot leave this regression runner trapped in its modal message pump.
                var response = (dialog.Content as Grid)?.Children.OfType<StackPanel>().SingleOrDefault()?.Children.OfType<Button>().LastOrDefault();
                if (response != null) response.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else owner.Close();
            }
        };
        timer.Start();
        MessageBoxResult answer;
        try { answer = show(); }
        finally { timer.Stop(); }
        if (failure != null) throw new InvalidOperationException("Owned prompt inspection failed.", failure);
        Assert(observedEntrance, "Every normal owner-owned prompt must visibly animate.");
        Assert(IsWindowEnabled(ownerHandle), "The invoking owner must be usable after the modal prompt closes.");
        return answer;
    }

    private static void OwnedPromptsPreserveEscapeAndCloseSemantics(Window owner)
    {
        static void PressEscape(Window dialog)
        {
            var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(dialog)!, Environment.TickCount, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            dialog.RaiseEvent(args);
        }
        var okay = InteractWithOwnedPrompt(owner, "Owned OK Escape",
            () => XCL2.App.Views.OwnedMessageDialog.Show(owner, "Information", "Owned OK Escape"), PressEscape);
        Assert(okay == MessageBoxResult.OK, "An OK-only prompt maps Escape to OK, matching MessageBox.");
        var canceled = InteractWithOwnedPrompt(owner, "Owned OKCancel Escape",
            () => XCL2.App.Views.OwnedMessageDialog.Show(owner, "Keep editing", "Owned OKCancel Escape", MessageBoxButton.OKCancel), PressEscape);
        Assert(canceled == MessageBoxResult.Cancel, "An OKCancel prompt maps Escape to Cancel.");
        var no = InteractWithOwnedPrompt(owner, "Owned YesNo",
            () => XCL2.App.Views.OwnedMessageDialog.Show(owner, "Choose an answer", "Owned YesNo", MessageBoxButton.YesNo),
            dialog =>
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
                var closeState = GetMenuState(GetSystemMenu(handle, false), 0xF060, 0);
                Assert(closeState != uint.MaxValue && (closeState & 0x3) != 0,
                    "YesNo must visibly disable the native Close command.");
                PressEscape(dialog);
                dialog.Close();
                Assert(dialog.IsVisible, "YesNo requires an explicit answer and cannot be canceled by Escape or Close.");
                ((Grid)dialog.Content).Children.OfType<StackPanel>().Single().Children.OfType<Button>().Last()
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
        Assert(no == MessageBoxResult.No, "The explicit No answer must remain negative after rejected cancellation.");
    }

    private static void OwnedPromptEndsWhenItsOwnerCloses()
    {
        var owner = new Window { Width = 320, Height = 220, Content = new Border(), ShowInTaskbar = false };
        owner.Show();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (owner.OwnedWindows.Count == 0) return;
            timer.Stop(); owner.Close();
        };
        timer.Start();
        try
        {
            var answer = XCL2.App.Views.OwnedMessageDialog.Show(owner, "Owner is closing", "Owner-close regression", MessageBoxButton.YesNoCancel);
            Assert(answer == MessageBoxResult.Cancel, "Owner shutdown must release modal waiting without granting an affirmative result.");
        }
        finally { timer.Stop(); if (owner.IsVisible) owner.Close(); }
    }

    private static void OwnedPromptsIgnoreUnavailableOwners()
    {
        var owner = new Window { Width = 320, Height = 220, ShowInTaskbar = false };
        var pending = XCL2.App.Views.OwnedMessageDialog.Show(owner, "Owner not shown", "Unavailable owner", MessageBoxButton.YesNoCancel);
        Assert(pending == MessageBoxResult.Cancel && !owner.IsVisible,
            "An unshown owner must neither be resurrected nor grant a confirmation.");
        owner.Show();
        owner.Close();
        var answer = XCL2.App.Views.OwnedMessageDialog.Show(owner, "Late async result", "Closed owner", MessageBoxButton.YesNoCancel);
        Assert(answer == MessageBoxResult.Cancel, "A completed async operation must not attach a new dialog to a closed owner.");
        Assert(!XCL2.App.Views.OwnedMessageDialog.ConfirmCustom(owner, "Late confirmation", "Closed owner", "继续使用", "确认关闭"),
            "A closed owner must never implicitly confirm the custom destructive action.");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);

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
