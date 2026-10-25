using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace XCL2.App.Services;

/// <summary>
/// Clips a surface's actual pixels to a rounded rectangle. Border.CornerRadius
/// only rounds its background; an image inside it otherwise remains square.
/// NaN disables the clip and restores the element's previous local value.
/// </summary>
public static class RoundedClipBehavior
{
    private sealed class ClipState
    {
        public object LocalValue { get; init; } = DependencyProperty.UnsetValue;
        public BindingBase? Binding { get; init; }
        public Geometry? Geometry { get; set; }
    }

    private static readonly ConditionalWeakTable<FrameworkElement, ClipState> States = new();

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClipBehavior),
        new PropertyMetadata(double.NaN, OnRadiusChanged));

    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    private static void OnRadiusChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        if (double.IsFinite((double)args.NewValue) && (double)args.NewValue >= 0)
        {
            if (!States.TryGetValue(element, out _))
            {
                var localValue = element.ReadLocalValue(UIElement.ClipProperty);
                var binding = BindingOperations.GetBindingBase(element, UIElement.ClipProperty);
                // An opaque expression (for example DynamicResource) cannot be
                // reattached through SetValue. Leave that explicit owner intact.
                if (binding == null && localValue != DependencyProperty.UnsetValue
                    && localValue is not null and not Geometry) return;
                States.Add(element, new ClipState
                {
                    LocalValue = localValue,
                    Binding = binding
                });
                element.SizeChanged += OnSizeChanged;
            }
            UpdateClip(element);
        }
        else if (States.TryGetValue(element, out var state))
        {
            element.SizeChanged -= OnSizeChanged;
            // Do not overwrite a clip deliberately replaced by another feature.
            if (ReferenceEquals(element.Clip, state.Geometry))
            {
                if (state.Binding != null) BindingOperations.SetBinding(element, UIElement.ClipProperty, state.Binding);
                else if (state.LocalValue == DependencyProperty.UnsetValue) element.ClearValue(UIElement.ClipProperty);
                else element.SetValue(UIElement.ClipProperty, state.LocalValue);
            }
            States.Remove(element);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs args) => UpdateClip((FrameworkElement)sender);

    private static void UpdateClip(FrameworkElement element)
    {
        if (!States.TryGetValue(element, out var state)) return;
        if (state.Geometry != null && !ReferenceEquals(element.Clip, state.Geometry)) return;
        var radius = Math.Min(GetRadius(element), Math.Min(element.ActualWidth, element.ActualHeight) / 2);
        var clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, element.ActualWidth), Math.Max(0, element.ActualHeight)), radius, radius);
        clip.Freeze();
        state.Geometry = clip;
        element.SetValue(UIElement.ClipProperty, clip);
    }
}
