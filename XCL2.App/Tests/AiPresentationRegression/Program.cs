using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Window? window = null;
        try
        {
            foreach (var key in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "AccentBrush", "PanelBrush", "SideBrush",
                "DividerBrush", "BorderBrush2", "BorderHoverBrush", "GlowSoftBrush" })
                app.Resources[key] = new SolidColorBrush(Colors.SteelBlue);
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/XCL2;component/Resources/Themes/AiConversation.xaml", UriKind.Relative) });
            var root = new StackPanel { Margin = new Thickness(16) };
            var body = new Border { Height = 180, Background = Brushes.AliceBlue };
            var disclosure = Disclosure("执行过程", body);
            root.Children.Add(disclosure);
            window = new Window { Title = "AI presentation regression", Width = 520, Height = 560,
                ShowInTaskbar = false, Content = root };
            window.Show();
            PumpUntil(() => disclosure.IsLoaded, "window loads");
            var host = Part<Border>(disclosure, "ExpansionHost");
            Check(host.Visibility == Visibility.Collapsed, "collapsed content occupies no space");
            var header = Part<ToggleButton>(disclosure, "HeaderToggle");
            header.IsChecked = true; // Same two-way binding used by mouse/keyboard activation.
            PumpUntil(() => host.Height > 2 && host.Height < 170, "expansion has intermediate visible heights");
            Check(disclosure.IsExpanded && host.IsHitTestVisible, "header operates the real Expander");
            PumpUntil(() => double.IsNaN(host.Height), "expansion completes in auto layout");
            Check(host.ActualHeight >= 180, "expanded content is not clipped");
            var arrow = Part<System.Windows.Shapes.Path>(disclosure, "DisclosureChevron");
            Check(Math.Abs(((RotateTransform)arrow.RenderTransform).Angle - 90) < .01, "expanded arrow points down");

            body.Height = 240;
            window.UpdateLayout();
            Check(host.ActualHeight >= 240, "streamed content can grow after the animation");
            header.IsChecked = false;
            PumpUntil(() => host.Height > 4 && host.Height < 230, "collapse also has intermediate heights");
            Check(!host.IsHitTestVisible, "collapsing content stops accepting clicks");
            header.IsChecked = true; // Reverse before the old collapse completes.
            PumpUntil(() => double.IsNaN(host.Height) && disclosure.IsExpanded, "rapid reversal returns to fully expanded state");
            Check(host.Visibility == Visibility.Visible && host.ActualHeight >= 240, "old completion cannot hide a reopened section");

            header.IsChecked = false;
            PumpUntil(() => host.Height > 4 && host.Height < 230, "start animation before changing preference");
            AnimationSettingsService.Enabled = false;
            AnimationSettingsService.NotifyChanged();
            Check(host.Visibility == Visibility.Collapsed && host.Height == 0, "disabling animation settles a running collapse");
            header.IsChecked = true;
            PumpUntil(() => double.IsNaN(host.Height), "disabled animation opens immediately");
            Check(Part<FrameworkElement>(disclosure, "ExpandSite").Opacity == 1, "disabled animation never leaves transparent text");

            AnimationSettingsService.Enabled = true;
            var inner = Disclosure("读取文件", new Border { Height = 100 });
            var outerBody = new StackPanel();
            outerBody.Children.Add(new TextBlock { Text = "任务详情" });
            outerBody.Children.Add(inner);
            var outer = Disclosure("执行过程（1）", outerBody);
            outer.IsExpanded = true;
            root.Children.Add(outer);
            PumpUntil(() => outer.IsLoaded && double.IsNaN(Part<Border>(outer, "ExpansionHost").Height), "outer section loads and settles");
            var outerHost = Part<Border>(outer, "ExpansionHost");
            inner.IsExpanded = true;
            PumpUntil(() => Part<Border>(inner, "ExpansionHost").Height > 2, "nested activity begins expanding");
            Check(double.IsNaN(outerHost.Height), "a nested expansion does not restart its enclosing section");
            PumpUntil(() => double.IsNaN(Part<Border>(inner, "ExpansionHost").Height), "nested expansion finishes");
            root.Children.Remove(outer);
            PumpUntil(() => AnimationSettingsService.Subscribers == 1, "unloaded disclosures release preference subscriptions");

            root.Children.Remove(disclosure);
            PumpUntil(() => AnimationSettingsService.Subscribers == 0, "detached panel has no static-event subscriptions");
            root.Children.Add(disclosure);
            PumpUntil(() => disclosure.IsLoaded && AnimationSettingsService.Subscribers == 1, "cached panel can load again");
            PumpUntil(() => double.IsNaN(host.Height), "cached expanded section remains readable");

            var actions = new WrapPanel();
            var copy = Action("复制");
            var regenerate = Action("重新生成");
            actions.Children.Add(copy); actions.Children.Add(regenerate); root.Children.Add(actions);
            window.UpdateLayout();
            var size = actions.RenderSize;
            var clicks = 0;
            copy.Click += (_, _) => ++clicks;
            copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, copy));
            copy.Focus(); window.UpdateLayout();
            Check(clicks == 1 && actions.RenderSize == size, "persistent actions remain clickable without shifting layout on focus");
            foreach (var button in new[] { copy, regenerate })
            {
                var outline = Part<Border>(button, "ActionOutline");
                Check(button.IsVisible && outline.Background is SolidColorBrush brush && brush.Color.A == 0,
                    "copy and regenerate are visible with transparent backgrounds");
            }
            Console.WriteLine($"PASS: {_checks} AI presentation checks.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { window?.Close(); app.Shutdown(); }
    }

    private static Expander Disclosure(string title, object content) => new()
    { Header = title, Content = content, Style = (Style)Application.Current.FindResource("AiDisclosureStyle"), Margin = new Thickness(0, 0, 0, 8) };

    private static Button Action(string label) => new()
    { Content = label, Style = (Style)Application.Current.FindResource("AiMessageAction") };

    private static T Part<T>(Control owner, string name) where T : FrameworkElement
    {
        owner.ApplyTemplate();
        return owner.Template.FindName(name, owner) as T ?? throw new InvalidOperationException($"Missing {name} on {owner.GetType().Name}");
    }

    private static void Check(bool condition, string scenario)
    { ++_checks; if (!condition) throw new InvalidOperationException(scenario); }

    private static void PumpUntil(Func<bool> predicate, string scenario)
    {
        var deadline = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (predicate() || deadline.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Check(predicate(), scenario);
    }
}
