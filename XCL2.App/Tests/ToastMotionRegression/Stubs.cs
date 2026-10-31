using System.Windows;
using System.Windows.Controls;

namespace XCL2.App.Views
{
    public sealed class MainWindow : Window
    {
        public ItemsControl ToastHost { get; } = new();
        public ItemsControl ActionToastHost { get; } = new();
        public MainWindow()
        {
            Width = 700; Height = 500; ShowInTaskbar = false;
            var panel = new StackPanel();
            panel.Children.Add(ToastHost); panel.Children.Add(ActionToastHost);
            Content = panel;
        }
    }
}
namespace XCL2.App.Services
{
    public enum AnimationKind { Toast }
    public static class AnimationSettingsService
    {
        public static double Strength { get; set; } = 1;
        public static double Seconds { get; set; } = .2;
        public static event Action? Changed;
        public static bool IsEnabled(AnimationKind _) => Strength > 0;
        public static double Intensity(AnimationKind _) => Strength;
        public static TimeSpan Duration(int _, AnimationKind kind) => IsEnabled(kind) ? TimeSpan.FromSeconds(Seconds) : TimeSpan.Zero;
        public static void NotifyChanged() => Changed?.Invoke();
    }
    public static class LauncherLogService
    {
        public static List<string> Entries { get; } = new();
        public static void AppendLine(string message) => Entries.Add(message);
    }
}
// System notification delivery is outside these in-app motion tests.
namespace Windows.Data.Xml.Dom
{
    public sealed class XmlDocument { public void LoadXml(string _) { } }
}
namespace Windows.UI.Notifications
{
    public sealed class ToastNotification { public ToastNotification(Windows.Data.Xml.Dom.XmlDocument _) { } }
    public sealed class ToastNotifier { public void Show(ToastNotification _) { } }
    public static class ToastNotificationManager { public static ToastNotifier CreateToastNotifier(string _) => new(); }
}
