using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using XCL2.App.Services;
using XCL2.App.Views;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);
    private const uint LeftDown = 0x0002, LeftUp = 0x0004;

    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        GetCursorPos(out var previousPointer);
        var box = new FeatureSearchBox();
        var host = new Grid();
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.Children.Add(box);
        var window = new Window { Width = 800, Height = 500, Content = host, ShowInTaskbar = false,
            Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        try
        {
            window.Show();
            window.Activate();
            box.Apply(host);
            box.SetLayoutEditing(true);
            PumpFor(100);
            Assert(!Stylus.GetIsPressAndHoldEnabled(box.SearchEntry), "Edit mode must disable the competing stylus long-press menu.");

            var start = box.CurrentLayout;
            PressAt(box, 150, 18);
            PumpFor(530);
            MoveBy(box, 40, 25);
            PumpFor(60);
            Release();
            Assert(Near(box.CurrentLayout.OffsetX, start.OffsetX + 40)
                && Near(box.CurrentLayout.OffsetY, start.OffsetY + 25), "A held center must drag with real mouse capture.");
            Assert(box.CompletedEdits == 1 && !box.SearchEntry.IsMouseCaptured, "Release must commit once and release capture.");

            var unchanged = box.CurrentLayout;
            var beforeShortPress = box.CompletedEdits;
            PressAt(box, 150, 18);
            PumpFor(30);
            MoveBy(box, 5, 3);
            PumpFor(30);
            Release();
            Assert(box.CurrentLayout == unchanged && box.CompletedEdits == beforeShortPress,
                "A short click with jitter must neither move the entry nor create a settings edit.");

            PressAt(box, box.SearchEntry.ActualWidth - 2, 18);
            MoveBy(box, 30, 0);
            PumpFor(60);
            Release();
            Assert(Near(box.CurrentLayout.Width, unchanged.Width + 30), "Resize edges must work immediately without a long press.");

            var beforeCancel = box.CurrentLayout;
            PressAt(box, 150, 18);
            PumpFor(530);
            MoveBy(box, 45, 20);
            PumpFor(60);
            Assert(box.CurrentLayout != beforeCancel && box.CancelEditGesture(), "An active preview must be cancelable by Escape's gesture path.");
            Release();
            Assert(box.CurrentLayout == beforeCancel && !box.SearchEntry.IsMouseCaptured, "Cancel must restore exact geometry and release capture.");

            PressAt(box, 150, 18);
            PumpFor(530);
            MoveBy(box, 25, 15);
            PumpFor(60);
            Mouse.Capture(null);
            PumpFor(30);
            Release();
            Assert(box.CurrentLayout == beforeCancel, "Losing mouse capture must roll back the live preview.");

            var beforeDisable = box.CompletedEdits;
            PressAt(box, 150, 18);
            box.SetLayoutEditing(false);
            PumpFor(550);
            MoveBy(box, 20, 10);
            Release();
            Assert(box.CurrentLayout == beforeCancel && box.CompletedEdits == beforeDisable,
                "Leaving edit mode must cancel the pending hold without a phantom edit.");
            Assert(Stylus.GetIsPressAndHoldEnabled(box.SearchEntry), "Leaving edit mode must restore normal stylus behavior.");

            box.Floating = false;
            box.CurrentLayout = FeatureSearchLayout.Default;
            box.Apply(host);
            box.SetLayoutEditing(true);
            PumpFor(50);
            var hostHeight = host.ActualHeight;
            PressAt(box, 150, 18);
            PumpFor(530);
            MoveBy(box, -40, 20);
            PumpFor(50);
            Assert(box.SearchEntry.IsMouseCaptured && Near(host.ActualHeight, hostHeight),
                "Moving a top-style entry must not treat the Auto row reflow as a host resize.");
            MoveBy(box, -20, 15);
            PumpFor(50);
            Release();
            Assert(Near(box.CurrentLayout.OffsetX, -60) && Near(box.CurrentLayout.OffsetY, 35),
                "Top-style dragging must survive successive vertical moves and retain its right anchor.");
            Console.WriteLine("PASS: Feature search mouse capture, long press, resize, cancel and edit lifecycle.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            Release();
            SetCursorPos(previousPointer.X, previousPointer.Y);
            window.Close();
            Application.Current.Shutdown();
        }
    }

    private static void PressAt(FeatureSearchBox box, double x, double y)
    {
        var point = box.SearchEntry.PointToScreen(new Point(x, y));
        Assert(SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y)), "Cannot move the test pointer.");
        PumpFor(30);
        mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
        PumpFor(20);
        Assert(box.SearchEntry.IsMouseCaptured, "The search entry must capture the real mouse on edit press.");
    }

    private static void MoveBy(FrameworkElement reference, double x, double y)
    {
        GetCursorPos(out var point);
        var local = reference.PointFromScreen(new Point(point.X, point.Y));
        var screen = reference.PointToScreen(new Point(local.X + x, local.Y + y));
        SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
    }

    private static void Release() { mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero); PumpFor(30); }
    private static bool Near(double value, double expected) => Math.Abs(value - expected) <= 2;
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
