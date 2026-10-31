using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>Recover one transiently lost WPF contact without releasing other fingers' bindings.</summary>
internal static class LegacyTouchContactRecovery
{
    public static void Schedule(TouchDevice contact, FrameworkElement target, Func<bool> isCurrent,
        Func<bool> canRecover, Action cancel)
    {
        _ = target.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            // Up/cancel or an explicit mode change may have removed this contact while queued.
            if (!isCurrent()) return;
            try
            {
                if (!canRecover() || !target.IsVisible || contact.GetTouchPoint(target).Action == TouchAction.Up)
                { cancel(); return; }
                if (ReferenceEquals(contact.Captured, target)) return;
                // An intentional transfer belongs to its new control; never steal it back.
                if (contact.Captured != null || !target.CaptureTouch(contact)) cancel();
            }
            catch (InvalidOperationException) { cancel(); }
        }));
    }
}
