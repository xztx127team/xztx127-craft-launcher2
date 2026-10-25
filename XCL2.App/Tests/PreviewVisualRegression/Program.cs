using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using XCL2.App.Models;
using XCL2.App.Services;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string scenario)
    {
        ++_checks;
        if (!condition) throw new InvalidOperationException(scenario);
    }

    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources["AppFontFamily"] = new FontFamily("Segoe UI");
        app.Resources["ScreenHeightFractionConverter"] = new ScreenHeightFractionConverter();
        app.Resources["HiddenScrollBarPageButton"] = new Style(typeof(RepeatButton));
        foreach (var key in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "PanelBrush", "SideBrush", "DividerBrush", "BorderBrush2", "BorderHoverBrush", "AccentBrush", "AccentHoverBrush", "GlowSoftBrush", "ButtonBackgroundBrush", "ButtonHoverBackgroundBrush", "ButtonForegroundBrush", "DangerBrush", "GlowBrush" })
            app.Resources[key] = new SolidColorBrush(Colors.SteelBlue);
        var legacyButton = new Style(typeof(Button));
        legacyButton.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 6, 9, 6)));
        app.Resources["PrimaryButton"] = legacyButton;
        var legacyImplicit = new Style(typeof(Button));
        app.Resources[typeof(Button)] = legacyImplicit;

        var root = new StackPanel();
        var staticButton = new Button { Content = "静态按钮", Style = legacyButton };
        var dynamicButton = new Button { Content = "动态按钮" };
        dynamicButton.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        var dynamicReference = dynamicButton.ReadLocalValue(FrameworkElement.StyleProperty);
        root.Children.Add(staticButton);
        root.Children.Add(dynamicButton);
        var localSurfaceStyle = new Style(typeof(Border));
        localSurfaceStyle.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(10)));
        var previewTrigger = new Trigger { Property = PreviewFluentTheme.IsActiveProperty, Value = true };
        previewTrigger.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(18)));
        localSurfaceStyle.Triggers.Add(previewTrigger);
        var page = new Border { Style = localSurfaceStyle };

        for (int iteration = 0; iteration < 3; iteration++)
        {
            PreviewFluentTheme.SetEnabled(true, root);
            Check(!ReferenceEquals(staticButton.Style, legacyButton), "static button adopts the real preview dictionary");
            var capturedPreviewStyle = staticButton.Style;
            Check(ReferenceEquals(dynamicButton.ReadLocalValue(FrameworkElement.StyleProperty), dynamicReference), "dynamic style reference survives enabling preview");
            PreviewFluentTheme.ApplyTo(page); // page was detached when styled
            root.Children.Add(page);
            Check(page.CornerRadius == new CornerRadius(18), "detached page has preview-only local surface styling");
            PreviewFluentTheme.SetEnabled(false, root);
            Check(ReferenceEquals(staticButton.Style, legacyButton), "the exact original static style is restored");
            Check(ReferenceEquals(app.Resources[typeof(Button)], legacyImplicit), "the legacy implicit button resource is restored");
            Check(!PreviewFluentTheme.GetIsActive(page) && page.CornerRadius == new CornerRadius(10), "a previously detached child cannot mask disabling preview");
            Check(ReferenceEquals(dynamicButton.ReadLocalValue(FrameworkElement.StyleProperty), dynamicReference), "dynamic style reference survives disabling preview");
            var delayedRow = new Button { Style = capturedPreviewStyle };
            // A virtualized row can load only after its template's mode changed.
            delayedRow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, delayedRow));
            Check(ReferenceEquals(delayedRow.Style, legacyButton), "a row created from an old preview template adopts the current legacy mode");
            root.Children.Remove(page);
        }

        PreviewFluentTheme.SetEnabled(true, root);
        var cachedPage = new Border { Style = localSurfaceStyle };
        PreviewFluentTheme.ApplyTo(cachedPage);
        PreviewFluentTheme.SetEnabled(false, root);
        cachedPage.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, cachedPage));
        Check(!PreviewFluentTheme.GetIsActive(cachedPage) && cachedPage.CornerRadius == new CornerRadius(10),
            "a detached cached page corrects its old local preview scope when loaded again");

        var layout = new PreviewLayoutOverrides();
        var unsetHeight = new Grid();
        var explicitHeight = new Grid { Width = 280, Height = 410, Margin = new Thickness(1, 2, 3, 4) };
        var heightSource = new Border { Width = 290, Height = 430 };
        var boundHeight = new Grid();
        BindingOperations.SetBinding(boundHeight, FrameworkElement.HeightProperty,
            new Binding(nameof(FrameworkElement.Height)) { Source = heightSource });
        BindingOperations.SetBinding(boundHeight, FrameworkElement.WidthProperty,
            new Binding(nameof(FrameworkElement.Width)) { Source = heightSource });
        for (int iteration = 0; iteration < 3; iteration++)
        {
            Check(layout.TrySet(unsetHeight, FrameworkElement.HeightProperty, 600d), "temporary minimum height can override an unset height");
            layout.TrySet(explicitHeight, FrameworkElement.HeightProperty, 600d);
            layout.TrySet(unsetHeight, FrameworkElement.WidthProperty, 320d);
            layout.TrySet(explicitHeight, FrameworkElement.WidthProperty, 320d);
            layout.TrySet(explicitHeight, FrameworkElement.MarginProperty, new Thickness(12));
            layout.TrySet(boundHeight, FrameworkElement.HeightProperty, 600d);
            layout.TrySet(boundHeight, FrameworkElement.WidthProperty, 320d);
            layout.Restore();
            Check(unsetHeight.ReadLocalValue(FrameworkElement.HeightProperty) == DependencyProperty.UnsetValue,
                "leaving short preview restores unset height instead of leaving a local Auto value");
            Check(explicitHeight.Height == 410 && explicitHeight.Margin == new Thickness(1, 2, 3, 4),
                "leaving compact preview restores the exact original height and margin");
            Check(unsetHeight.ReadLocalValue(FrameworkElement.WidthProperty) == DependencyProperty.UnsetValue
                  && explicitHeight.Width == 280, "leaving a narrow preview restores unset and explicit widths");
            Check(BindingOperations.IsDataBound(boundHeight, FrameworkElement.HeightProperty) && boundHeight.Height == 430,
                "leaving short preview restores an existing height binding");
            Check(BindingOperations.IsDataBound(boundHeight, FrameworkElement.WidthProperty) && boundHeight.Width == 290,
                "leaving a narrow preview restores an existing width binding");
        }
        var resourceWidth = new Grid();
        app.Resources["TestPreviewWidth"] = 420d;
        resourceWidth.SetResourceReference(FrameworkElement.WidthProperty, "TestPreviewWidth");
        var widthReference = resourceWidth.ReadLocalValue(FrameworkElement.WidthProperty);
        Check(!layout.TrySet(resourceWidth, FrameworkElement.WidthProperty, 600d)
              && ReferenceEquals(widthReference, resourceWidth.ReadLocalValue(FrameworkElement.WidthProperty)),
            "opaque layout resource expressions are left intact");

        var image = new Image { Width = 48, Height = 48 };
        image.Measure(new Size(48, 48));
        image.Arrange(new Rect(0, 0, 48, 48));
        RoundedClipBehavior.SetRadius(image, 12);
        var clip = image.Clip;
        Check(clip != null && !clip.FillContains(new Point(1, 1)) && clip.FillContains(new Point(24, 24)), "actual image corner pixels are clipped while the center is visible");
        RoundedClipBehavior.SetRadius(image, double.NaN);
        Check(image.ReadLocalValue(UIElement.ClipProperty) == DependencyProperty.UnsetValue, "disabling preview removes only its own clip");

        var originalClip = new RectangleGeometry(new Rect(0, 0, 30, 30));
        var clipSource = new Border { Clip = originalClip };
        BindingOperations.SetBinding(image, UIElement.ClipProperty, new Binding(nameof(UIElement.Clip)) { Source = clipSource });
        RoundedClipBehavior.SetRadius(image, 10);
        RoundedClipBehavior.SetRadius(image, double.NaN);
        Check(BindingOperations.IsDataBound(image, UIElement.ClipProperty) && ReferenceEquals(image.Clip, originalClip), "an existing clip binding is restored after toggling");
        RoundedClipBehavior.SetRadius(image, 10);
        var replacementClip = new EllipseGeometry(new Point(24, 24), 20, 20);
        image.Clip = replacementClip;
        RoundedClipBehavior.SetRadius(image, double.NaN);
        Check(ReferenceEquals(image.Clip, replacementClip), "disabling preview does not overwrite a clip replaced by another feature");
        Console.WriteLine($"PASS: {_checks} preview resource, scope restoration and true rounded-pixel checks.");
        app.Shutdown();
    }
}
