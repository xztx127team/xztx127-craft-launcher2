using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>AI 执行过程在固定宽度内展开；收起完成后隐藏，快速反向操作从当前高度接续。</summary>
public static class AiDisclosureMotion
{
    private static readonly ConditionalWeakTable<Expander, Motion> Motions = new();
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(AiDisclosureMotion), new PropertyMetadata(false, OnEnabledChanged));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Expander expander) return;
        if (Motions.TryGetValue(expander, out var previous)) { previous.Dispose(); Motions.Remove(expander); }
        if (args.NewValue is true) Motions.Add(expander, new Motion(expander));
    }

    private sealed class Motion : IDisposable
    {
        private readonly Expander _owner;
        private Border? _host;
        private FrameworkElement? _content;
        private RotateTransform? _chevron;
        private int _generation;
        private bool _subscribed;
        private bool _disposed;

        internal Motion(Expander owner)
        {
            _owner = owner;
            owner.Loaded += Loaded;
            owner.Unloaded += Unloaded;
            owner.IsVisibleChanged += VisibilityChanged;
            owner.Expanded += Toggled;
            owner.Collapsed += Toggled;
            if (owner.IsLoaded) Loaded(owner, new RoutedEventArgs());
        }

        private bool ResolveParts()
        {
            _owner.ApplyTemplate();
            _host = _owner.Template?.FindName("ExpansionHost", _owner) as Border;
            _content = _owner.Template?.FindName("ExpandSite", _owner) as FrameworkElement;
            if (_owner.Template?.FindName("DisclosureChevron", _owner) is FrameworkElement arrow
                && arrow.RenderTransform is RotateTransform rotation)
            {
                // Template Freezables may be frozen/shared until an instance needs animation.
                _chevron = rotation.IsFrozen ? rotation.Clone() : rotation;
                if (!ReferenceEquals(rotation, _chevron)) arrow.RenderTransform = _chevron;
            }
            else _chevron = null;
            return _host != null && _content != null;
        }

        private void Loaded(object sender, RoutedEventArgs args)
        {
            if (!_subscribed) { AnimationSettingsService.Changed += PreferencesChanged; _subscribed = true; }
            QueueTransition();
        }

        private void Unloaded(object sender, RoutedEventArgs args)
        {
            if (_subscribed) { AnimationSettingsService.Changed -= PreferencesChanged; _subscribed = false; }
            ++_generation;
            Settle();
        }

        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!_owner.IsLoaded) return;
            if (_owner.IsVisible) QueueTransition();
            else { ++_generation; Settle(); }
        }

        private void PreferencesChanged() { ++_generation; Settle(); }

        private void Toggled(object sender, RoutedEventArgs args)
        {
            // Nested activity rows must not replay the enclosing execution-process transition.
            if (!ReferenceEquals(args.OriginalSource, _owner)) return;
            QueueTransition();
        }

        private void QueueTransition()
        {
            var generation = ++_generation;
            if (!_owner.IsLoaded || !_owner.IsVisible) return;
            _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!_disposed && generation == _generation && _owner.IsLoaded && _owner.IsVisible) Transition(generation);
            }));
        }

        private void StopClocks()
        {
            _host?.BeginAnimation(FrameworkElement.HeightProperty, null);
            _content?.BeginAnimation(UIElement.OpacityProperty, null);
            _chevron?.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        private void Settle()
        {
            if (!ResolveParts()) return;
            StopClocks();
            _host!.Height = _owner.IsExpanded ? double.NaN : 0;
            _host.Visibility = _owner.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            _host.IsHitTestVisible = _owner.IsExpanded;
            _content!.Opacity = 1;
            if (_chevron != null) _chevron.Angle = _owner.IsExpanded ? 90 : 0;
        }

        private void Transition(int generation)
        {
            if (!ResolveParts()) return;
            var strength = AnimationSettingsService.Intensity(AnimationKind.Popup);
            var duration = AnimationSettingsService.Duration(220, AnimationKind.Popup);
            if (strength <= 0 || duration <= TimeSpan.Zero) { Settle(); return; }

            var from = _host!.Visibility == Visibility.Visible ? _host.ActualHeight : 0;
            // Read animated values before removing clocks so quick reversals do not jump to an endpoint.
            if (!double.IsNaN(_host.Height)) from = _host.Height;
            if (!_owner.IsExpanded && from <= 0) { Settle(); return; }
            var opacity = _content!.Opacity;
            var angle = _chevron?.Angle ?? 0;
            StopClocks();
            _host.Visibility = Visibility.Visible;
            _host.IsHitTestVisible = _owner.IsExpanded;
            var target = 0d;
            if (_owner.IsExpanded)
            {
                var width = Math.Max(1, _owner.ActualWidth - 2);
                _content.Measure(new Size(width, double.PositiveInfinity));
                target = Math.Max(0, _content.DesiredSize.Height);
            }
            _host.Height = target;
            _content.Opacity = 1;
            if (_chevron != null) _chevron.Angle = _owner.IsExpanded ? 90 : 0;
            var height = Animate(from, target, duration);
            height.Completed += (_, _) => { if (!_disposed && generation == _generation) Settle(); };
            _host.BeginAnimation(FrameworkElement.HeightProperty, height, HandoffBehavior.SnapshotAndReplace);
            if (_owner.IsExpanded)
                _content.BeginAnimation(UIElement.OpacityProperty,
                    Animate(from <= 0 ? 1 - .65 * strength : opacity, 1, duration), HandoffBehavior.SnapshotAndReplace);
            _chevron?.BeginAnimation(RotateTransform.AngleProperty,
                Animate(angle, _owner.IsExpanded ? 90 : 0, duration), HandoffBehavior.SnapshotAndReplace);
        }

        private static DoubleAnimation Animate(double from, double to, TimeSpan duration) => new(from, to, new Duration(duration))
        { FillBehavior = FillBehavior.HoldEnd, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

        public void Dispose()
        {
            _disposed = true; ++_generation;
            _owner.Loaded -= Loaded; _owner.Unloaded -= Unloaded;
            _owner.IsVisibleChanged -= VisibilityChanged;
            _owner.Expanded -= Toggled; _owner.Collapsed -= Toggled;
            if (_subscribed) AnimationSettingsService.Changed -= PreferencesChanged;
            _subscribed = false;
            Settle();
        }
    }
}
