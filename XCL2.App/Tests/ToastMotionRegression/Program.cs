using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;
using XCL2.App.Views;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var key in new[] { "AccentBrush", "SuccessTextBrush", "WarningTextBrush", "DangerBrush", "TextPrimaryBrush", "TextSecondaryBrush" })
            Application.Current.Resources[key] = Brushes.Black;
        Application.Current.Resources["PanelBrush"] = Brushes.White;
        Application.Current.Resources["SecondaryButton"] = new Style(typeof(Button));
        var owner = new MainWindow();
        try
        {
            owner.Show();
            PumpUntil(() => owner.IsLoaded, "The test host must load.");
            ToastService.Register(owner);
            ShortLifetimeStartsAfterEntrance(owner);
            RepeatedActionKeyAnimatesReplacement(owner);
            DismissDuringPreparationCannotReappear(owner);
            DisableDuringEntranceKeepsLifetime(owner);
            DisabledPreferenceChangesDoNotResetLifetime(owner);
            Check(LauncherLogService.Entries.Count == 0, "The actual toast service logged an unexpected failure.");
            Console.WriteLine("PASS: 5 toast motion lifecycle scenarios using production ToastService.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { owner.Close(); Application.Current.Shutdown(); }
    }

    private static void ShortLifetimeStartsAfterEntrance(MainWindow owner)
    {
        AnimationSettingsService.Strength = 1;
        AnimationSettingsService.Seconds = .2;
        ToastService.Show("Short notice", durationSeconds: .05);
        var card = (Border)owner.ToastHost.Items[0];
        Thread.Sleep(350); // Simulate a cold layout lasting longer than both requested times.
        PumpFor(80);
        Check(owner.ToastHost.Items.Contains(card), "A short notice must not expire while its entrance is still running.");
        Check(card.Opacity < .999, "The entrance must still be visible after the blocked first layout resumes.");
        PumpUntil(() => card.Opacity >= .999, "The notice must finish entering before its lifetime starts.");
        Check(owner.ToastHost.Items.Contains(card), "The full notice must be presented for its requested lifetime.");
        PumpUntil(() => !owner.ToastHost.Items.Contains(card), "The notice must then fade out and release its item.");
    }

    private static void RepeatedActionKeyAnimatesReplacement(MainWindow owner)
    {
        ToastService.ShowActionPrompt("First prompt", "OK", () => { }, key: "same");
        var first = (Border)owner.ActionToastHost.Items[0];
        PumpUntil(() => first.Opacity >= .999, "The first keyed prompt enters.");
        ToastService.ShowActionPrompt("Second prompt", "OK", () => { }, key: "same");
        var replacement = (Border)owner.ActionToastHost.Items[0];
        Check(!ReferenceEquals(first, replacement) && owner.ActionToastHost.Items.Count == 1,
            "A repeated key replaces the old message without stacking stale actions.");
        Check(replacement.Opacity < .999, "A replacement message gets its own entrance.");
        PumpUntil(() => replacement.Opacity >= .999, "The replacement prompt enters.");
        ToastService.DismissActionPrompt("same");
        PumpUntil(() => owner.ActionToastHost.Items.Count == 0, "The replacement action dismisses cleanly.");
    }

    private static void DismissDuringPreparationCannotReappear(MainWindow owner)
    {
        ToastService.ShowActionPrompt("Dismiss immediately", "OK", () => { }, key: "cancel");
        ToastService.DismissActionPrompt("cancel");
        PumpUntil(() => owner.ActionToastHost.Items.Count == 0, "A prompt can be dismissed before its first composed frame.");
        PumpFor(300);
        Check(owner.ActionToastHost.Items.Count == 0, "A stale entrance continuation must not reinsert a dismissed prompt.");
    }

    private static void DisableDuringEntranceKeepsLifetime(MainWindow owner)
    {
        ToastService.Show("Disable during entrance", durationSeconds: .05);
        var card = (Border)owner.ToastHost.Items[0];
        AnimationSettingsService.Strength = 0;
        AnimationSettingsService.NotifyChanged();
        Check(card.Opacity == 1, "Disabling motion immediately presents a pending notice.");
        PumpUntil(() => owner.ToastHost.Items.Count == 0, "Settling a pending notice must still start its lifetime timer.");
    }

    private static void DisabledPreferenceChangesDoNotResetLifetime(MainWindow owner)
    {
        ToastService.Show("Already static notice", durationSeconds: .15);
        PumpFor(90);
        AnimationSettingsService.NotifyChanged();
        PumpFor(90);
        Check(owner.ToastHost.Items.Count == 0, "Unrelated settings changes must not restart a static notice lifetime.");
        AnimationSettingsService.Strength = 1;
    }

    private static void PumpUntil(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline) PumpFor(10);
        Check(condition(), failure);
    }
    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
