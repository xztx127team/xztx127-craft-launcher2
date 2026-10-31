using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>触屏标签长按查看全文；不捕获触摸，移动后让正常滚动继续。</summary>
public static class LongPressToolTipBehavior
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool),
        typeof(LongPressToolTipBehavior), new PropertyMetadata(false, EnabledChanged));
    private static readonly ConditionalWeakTable<FrameworkElement, PressState> States = new();
    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private sealed class PressState
    {
        public required DispatcherTimer Timer;
        public Point Origin;
        public int? TouchId;
        public ToolTip? Popup;
        public bool OriginalPressAndHold;
        public Window? Root;
        public EventHandler<TouchEventArgs>? RootMove;
        public EventHandler<TouchEventArgs>? RootEnd;
        public EventHandler? RootDeactivate;
    }

    private static void EnabledChanged(DependencyObject dependency, DependencyPropertyChangedEventArgs args)
    {
        if (dependency is not FrameworkElement element) return;
        if ((bool)args.NewValue)
        {
            if (States.TryGetValue(element, out _)) return;
            var state = new PressState { Timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) }, OriginalPressAndHold = Stylus.GetIsPressAndHoldEnabled(element) };
            state.Timer.Tick += (_, _) =>
            {
                state.Timer.Stop();
                if (!element.IsLoaded || state.TouchId == null || element.ToolTip == null) return;
                object? content = element.ToolTip is ToolTip original ? original.Content : element.ToolTip;
                if (content is string text) content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 };
                state.Popup = new ToolTip { Content = content, PlacementTarget = element, Placement = PlacementMode.Relative,
                    HorizontalOffset = Math.Max(0, state.Origin.X), VerticalOffset = state.Origin.Y + 18, StaysOpen = true, MaxWidth = 520 };
                state.Popup.IsOpen = true;
            };
            States.Add(element, state);
            Stylus.SetIsPressAndHoldEnabled(element, false);
            element.TouchDown += TouchDown;
            element.TouchMove += TouchMove;
            element.TouchUp += TouchEnd;
            element.LostTouchCapture += TouchEnd;
            element.Unloaded += Unloaded;
        }
        else if (States.TryGetValue(element, out var state))
        {
            Stop(state); Stylus.SetIsPressAndHoldEnabled(element, state.OriginalPressAndHold);
            element.TouchDown -= TouchDown; element.TouchMove -= TouchMove; element.TouchUp -= TouchEnd;
            element.LostTouchCapture -= TouchEnd; element.Unloaded -= Unloaded; States.Remove(element);
        }
    }

    private static void TouchDown(object? sender, TouchEventArgs args)
    {
        if (sender is not FrameworkElement element || !States.TryGetValue(element, out var state) || state.TouchId != null) return;
        state.TouchId = args.TouchDevice.Id; state.Origin = args.GetTouchPoint(element).Position;
        // A finger may move outside the label; watch this window only while pressed so scrolling always cancels.
        state.Root = Window.GetWindow(element);
        if (state.Root != null)
        {
            state.RootMove = (_, move) =>
            {
                if (state.TouchId == move.TouchDevice.Id && (!element.IsLoaded || (move.GetTouchPoint(element).Position - state.Origin).Length > 8)) Stop(state);
            };
            state.RootEnd = (_, end) => { if (state.TouchId == end.TouchDevice.Id) Stop(state); };
            state.Root.AddHandler(UIElement.PreviewTouchMoveEvent, state.RootMove, true);
            state.Root.AddHandler(UIElement.PreviewTouchUpEvent, state.RootEnd, true);
            state.RootDeactivate = (_, _) => Stop(state);
            state.Root.Deactivated += state.RootDeactivate;
        }
        state.Timer.Start();
    }
    private static void TouchMove(object? sender, TouchEventArgs args)
    {
        if (sender is not FrameworkElement element || !States.TryGetValue(element, out var state) || state.TouchId != args.TouchDevice.Id) return;
        if ((args.GetTouchPoint(element).Position - state.Origin).Length > 8) Stop(state);
    }
    private static void TouchEnd(object? sender, TouchEventArgs args)
    {
        if (sender is FrameworkElement element && States.TryGetValue(element, out var state) && state.TouchId == args.TouchDevice.Id) Stop(state);
    }
    private static void Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement element && States.TryGetValue(element, out var state)) Stop(state);
    }
    private static void Stop(PressState state)
    {
        state.Timer.Stop(); state.TouchId = null;
        if (state.Root != null)
        {
            if (state.RootMove != null) state.Root.RemoveHandler(UIElement.PreviewTouchMoveEvent, state.RootMove);
            if (state.RootEnd != null) state.Root.RemoveHandler(UIElement.PreviewTouchUpEvent, state.RootEnd);
            if (state.RootDeactivate != null) state.Root.Deactivated -= state.RootDeactivate;
            state.Root = null; state.RootMove = null; state.RootEnd = null; state.RootDeactivate = null;
        }
        if (state.Popup != null) { state.Popup.IsOpen = false; state.Popup.Content = null; state.Popup = null; }
    }
}
