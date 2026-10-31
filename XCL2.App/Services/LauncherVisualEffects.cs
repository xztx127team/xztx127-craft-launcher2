using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using XCL2.App.Views;

namespace XCL2.App.Services;

public static class LauncherVisualEffects
{
    public enum Kind { Rise, Fall, Spin, Pendulum, Orbit, Shake, Breathe, Flip, Skew, Wave, Bounce, Zoom, Confetti, Stars, Matrix }
    public static IReadOnlyList<(Kind Kind, string Title)> Catalog { get; } = new[]
    {
        (Kind.Rise, "反重力上浮"), (Kind.Fall, "重力下坠"), (Kind.Spin, "旋转舞台"),
        (Kind.Pendulum, "钟摆摇晃"), (Kind.Orbit, "环绕飞行"), (Kind.Shake, "地震模式"),
        (Kind.Breathe, "呼吸缩放"), (Kind.Flip, "镜面翻转"), (Kind.Skew, "果冻倾斜"),
        (Kind.Wave, "海浪漂移"), (Kind.Bounce, "弹跳窗口"), (Kind.Zoom, "远近穿梭"),
        (Kind.Confetti, "彩纸雨"), (Kind.Stars, "星际穿行"), (Kind.Matrix, "像素字符雨")
    };
    private static MainWindow? _owner;
    private static FrameworkElement? _surface;
    private static Transform? _original;
    private static Point _origin;
    private static TransformGroup? _group;
    private static Canvas? _decoration;
    private static DispatcherTimer? _timer;
    private static readonly Stopwatch Clock = new();
    private static Kind _kind;
    private static bool _manual;
    private static TimeSpan? _duration;
    public const int DefaultDurationSeconds = 5;
    public static bool IsActive => _timer != null;

    public static void Initialize(MainWindow owner)
    {
        if (ReferenceEquals(_owner, owner)) return;
        Stop(); _owner = owner;
        void EmergencyKey(object sender, PreProcessInputEventArgs args)
        {
            if (args.StagingItem.Input is not KeyEventArgs { Key: Key.F1, IsDown: true } key ||
                (!IsActive && !AprilFoolsService.IsActive)) return;
            Stop(); AprilFoolsService.DismissForToday(); key.Handled = true;
        }
        InputManager.Current.PreProcessInput += EmergencyKey;
        owner.Closed += (_, _) =>
        {
            Stop(); AprilFoolsService.StateChanged -= UpdateAprilEffect;
            InputManager.Current.PreProcessInput -= EmergencyKey; _owner = null;
        };
        AprilFoolsService.StateChanged += UpdateAprilEffect;
        UpdateAprilEffect();
    }

    private static void UpdateAprilEffect()
    {
        if (_owner == null) return;
        if (!_owner.Dispatcher.CheckAccess()) { _owner.Dispatcher.BeginInvoke(new Action(UpdateAprilEffect)); return; }
        var active = Catalog.FirstOrDefault(item => AprilFoolsService.Has((AprilFoolsService.Effect)(1 << (7 + (int)item.Kind))));
        if (active.Title == null) { if (!_manual) Stop(); return; }
        if (IsActive && !_manual && _kind == active.Kind) return;
        Start(active.Kind, false);
    }

    /// <summary>duration 为 null 表示不限时（仅手动停止或按 F1）。</summary>
    public static void Start(Kind kind, bool manual = true, TimeSpan? duration = null)
    {
        Stop();
        if (_owner?.Content is not FrameworkElement surface) return;
        _manual = manual; _kind = kind; _surface = surface; _duration = duration;
        _original = surface.RenderTransform; _origin = surface.RenderTransformOrigin;
        _group = new TransformGroup();
        _group.Children.Add(_original ?? Transform.Identity);
        _group.Children.Add(new ScaleTransform());
        _group.Children.Add(new SkewTransform());
        _group.Children.Add(new RotateTransform());
        _group.Children.Add(new TranslateTransform());
        surface.RenderTransformOrigin = new Point(.5, .5);
        surface.SetCurrentValue(UIElement.RenderTransformProperty, _group);
        if (surface is Panel panel)
        {
            _decoration = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
            Panel.SetZIndex(_decoration, 10000);
            if (panel is Grid grid) { Grid.SetRowSpan(_decoration, Math.Max(1, grid.RowDefinitions.Count)); Grid.SetColumnSpan(_decoration, Math.Max(1, grid.ColumnDefinitions.Count)); }
            panel.Children.Add(_decoration);
            if (kind is Kind.Confetti or Kind.Stars or Kind.Matrix)
                for (int i = 0; i < 52; i++)
                {
                    FrameworkElement particle = kind == Kind.Matrix
                        ? new TextBlock { Text = i % 3 == 0 ? "XCL" : (i % 2).ToString(), FontSize = 15, Foreground = Brushes.MediumSeaGreen }
                        : kind == Kind.Stars
                            ? new Ellipse { Width = 3 + i % 4, Height = 3 + i % 4, Fill = Brushes.Gold }
                            : new Rectangle { Width = 7, Height = 12, Fill = new SolidColorBrush(Color.FromRgb((byte)(90 + i * 37 % 165), (byte)(90 + i * 61 % 165), (byte)(90 + i * 83 % 165))) };
                    _decoration.Children.Add(particle);
                }
        }
        Clock.Restart();
        _timer = new DispatcherTimer(DispatcherPriority.Render, _owner.Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += Tick; _timer.Start();
        ToastService.ShowInfo(Catalog[(int)kind].Title + (duration is { } d ? $" · {d.TotalSeconds:0.#} 秒后自动恢复 · 按 F1 提前恢复" : " · 按 F1 恢复"));
    }

    private static void Tick(object? sender, EventArgs e)
    {
        if (_surface == null || _group == null || _owner == null) return;
        if (_duration is { } limit && Clock.Elapsed >= limit)
        {
            Stop(); ToastService.ShowInfo("特效已结束"); return;
        }
        if (!_owner.IsVisible || _owner.WindowState == WindowState.Minimized) return;
        var t = Clock.Elapsed.TotalSeconds;
        var scale = (ScaleTransform)_group.Children[1]; var skew = (SkewTransform)_group.Children[2];
        var rotate = (RotateTransform)_group.Children[3]; var move = (TranslateTransform)_group.Children[4];
        var w = Math.Max(1, _surface.ActualWidth); var h = Math.Max(1, _surface.ActualHeight);
        switch (_kind)
        {
            case Kind.Rise: scale.ScaleX = scale.ScaleY = .82; move.Y = -h * .15 * (1 - Math.Cos(t * 3)); break;
            case Kind.Fall: scale.ScaleX = scale.ScaleY = .82; move.Y = h * .15 * (1 - Math.Cos(t * 3)); break;
            case Kind.Spin: scale.ScaleX = scale.ScaleY = .62; rotate.Angle = t * 180 % 360; break;
            case Kind.Pendulum: scale.ScaleX = scale.ScaleY = .84; rotate.Angle = Math.Sin(t * 4) * 24; break;
            case Kind.Orbit: scale.ScaleX = scale.ScaleY = .72; move.X = Math.Cos(t * 3) * w * .12; move.Y = Math.Sin(t * 3) * h * .12; break;
            case Kind.Shake: move.X = Math.Sin(t * 34) * 22; move.Y = Math.Cos(t * 29) * 17; break;
            case Kind.Breathe: scale.ScaleX = scale.ScaleY = .84 + Math.Sin(t * 2) * .14; break;
            case Kind.Flip: scale.ScaleX = Math.Cos(t * 2); scale.ScaleY = .9; break;
            case Kind.Skew: scale.ScaleX = scale.ScaleY = .8; skew.AngleX = Math.Sin(t * 3) * 18; skew.AngleY = Math.Cos(t * 2) * 10; break;
            case Kind.Wave: move.X = Math.Sin(t * 3) * 30; move.Y = Math.Sin(t * 6) * 18; rotate.Angle = Math.Sin(t * 2) * 3; break;
            case Kind.Bounce: scale.ScaleX = scale.ScaleY = .82; move.Y = -Math.Abs(Math.Sin(t * 4)) * h * .2; break;
            case Kind.Zoom: scale.ScaleX = scale.ScaleY = .6 + (1 + Math.Sin(t * 3)) * .2; rotate.Angle = Math.Sin(t * 3) * 8; break;
        }
        if (_decoration == null) return;
        for (int i = 0; i < _decoration.Children.Count; i++)
        {
            var particle = (FrameworkElement)_decoration.Children[i];
            if (_kind == Kind.Stars)
            {
                var distance = (t * 100 + i * 47) % Math.Max(w, h);
                Canvas.SetLeft(particle, w / 2 + Math.Cos(i * 2.4) * distance);
                Canvas.SetTop(particle, h / 2 + Math.Sin(i * 2.4) * distance);
            }
            else
            {
                Canvas.SetLeft(particle, (i * 73 + (_kind == Kind.Confetti ? Math.Sin(t + i) * 24 : 0) + w) % w);
                Canvas.SetTop(particle, (t * (70 + i % 6 * 15) + i * 37) % (h + 30) - 30);
            }
        }
    }

    public static void Stop()
    {
        if (_timer != null) { _timer.Stop(); _timer.Tick -= Tick; _timer = null; }
        if (_surface != null)
        {
            if (ReferenceEquals(_surface.RenderTransform, _group)) _surface.SetCurrentValue(UIElement.RenderTransformProperty, _original ?? Transform.Identity);
            _surface.RenderTransformOrigin = _origin;
            if (_surface is Panel panel && _decoration != null) panel.Children.Remove(_decoration);
        }
        _manual = false; _duration = null; _surface = null; _decoration = null; _group = null; Clock.Reset();
    }
}
