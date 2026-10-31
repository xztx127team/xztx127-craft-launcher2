using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using XCL2.App.Views;

namespace XCL2.App.Services;

internal sealed class LaunchButtonDodge
{
    private readonly MainWindow _owner;
    private readonly Grid _host;
    private readonly Button _original;
    private readonly Func<bool> _canDodge;
    private readonly DispatcherTimer _timer;
    private Canvas? _layer;
    private Button? _proxy;
    private double _opacity;
    private bool _hitTest;

    public LaunchButtonDodge(MainWindow owner, Grid host, Button button, Func<bool> canDodge)
    {
        _owner = owner; _host = host; _original = button; _canDodge = canDodge;
        _timer = new DispatcherTimer(DispatcherPriority.Input, owner.Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += Tick;
        AprilFoolsService.StateChanged += Apply;
        owner.Closed += (_, _) => { _timer.Stop(); _timer.Tick -= Tick; AprilFoolsService.StateChanged -= Apply; Restore(); };
        Apply();
    }
    private void Apply()
    {
        if (!_owner.Dispatcher.CheckAccess()) { _owner.Dispatcher.BeginInvoke(new Action(Apply)); return; }
        if (AprilFoolsService.Has(AprilFoolsService.Effect.DodgeLaunchButton)) _timer.Start();
        else { _timer.Stop(); Restore(); }
    }
    private void Tick(object? sender, EventArgs e)
    {
        if (!_canDodge() || !_original.IsVisible || !_original.IsEnabled || !_owner.IsActive || OverlayDialogService.HasActiveOverlay)
        { Restore(); return; }
        if (_host.ActualWidth <= 0 || _host.ActualHeight <= 0) return;
        var mouse = Mouse.GetPosition(_host);
        var origin = _original.TranslatePoint(new Point(), _host);
        var bounds = _proxy == null
            ? new AprilFoolsMotionPolicy.Box(origin.X, origin.Y, _original.ActualWidth, _original.ActualHeight)
            : new AprilFoolsMotionPolicy.Box(Canvas.GetLeft(_proxy), Canvas.GetTop(_proxy), _proxy.Width, _proxy.Height);
        double gap = Math.Clamp(ConfigService.Active?.Config.AprilFoolsMouseAvoidanceDistance ?? 240, 240, 500);
        if (AprilFoolsMotionPolicy.Distance(new(mouse.X, mouse.Y), bounds) > gap) return;
        if (_proxy == null)
        {
            _opacity = _original.Opacity; _hitTest = _original.IsHitTestVisible;
            _layer = new Canvas(); Grid.SetRowSpan(_layer, Math.Max(1, _host.RowDefinitions.Count));
            Grid.SetColumnSpan(_layer, Math.Max(1, _host.ColumnDefinitions.Count)); Panel.SetZIndex(_layer, 500);
            _proxy = new Button { Content = _original.Content is string text ? text : "启动游戏", Style = _original.Style,
                Width = Math.Max(80, _original.ActualWidth), Height = Math.Max(36, _original.ActualHeight), ToolTip = "F1 恢复；点到即可正常启动" };
            _proxy.Click += (_, _) => _original.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, _original));
            _layer.Children.Add(_proxy); _host.Children.Add(_layer);
            _original.SetCurrentValue(UIElement.OpacityProperty, 0d);
            _original.SetCurrentValue(UIElement.IsHitTestVisibleProperty, false);
        }
        var area = new AprilFoolsMotionPolicy.Box(6, 38, Math.Max(0, _host.ActualWidth - 12), Math.Max(0, _host.ActualHeight - 44));
        var target = AprilFoolsMotionPolicy.Evade(new(bounds.X, bounds.Y, _proxy.Width, _proxy.Height), area, new(mouse.X, mouse.Y), gap, Random.Shared);
        _proxy.Width = target.Width; _proxy.Height = target.Height;
        Canvas.SetLeft(_proxy, target.X); Canvas.SetTop(_proxy, target.Y);
    }
    private void Restore()
    {
        if (_proxy == null) return;
        _host.Children.Remove(_layer!); _layer = null; _proxy = null;
        _original.SetCurrentValue(UIElement.OpacityProperty, _opacity);
        _original.SetCurrentValue(UIElement.IsHitTestVisibleProperty, _hitTest);
    }
}
