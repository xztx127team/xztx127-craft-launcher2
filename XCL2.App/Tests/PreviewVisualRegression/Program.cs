using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using XCL2.App.Models;
using XCL2.App.Services;
using XCL2.App.Views;

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

        VerifyPreviewPalette(app);
        VerifyCenteredWrappedRows();

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

        image.Clip = null;
        RoundedClipBehavior.SetRadius(image, 12);
        Check(image.Clip != null, "rounded preview can temporarily replace an explicitly null local clip");
        RoundedClipBehavior.SetRadius(image, double.NaN);
        Check(image.Clip == null && image.ReadLocalValue(UIElement.ClipProperty) == null,
            "disabling preview restores explicit local null instead of changing it to an unset clip");

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

    private static void VerifyCenteredWrappedRows()
    {
        var panel = new RightAlignedWrapPanel { HorizontalSpacing = 10, VerticalSpacing = 6 };
        var first = new Border { Width = 70, Height = 20 };
        var second = new Border { Width = 50, Height = 30 };
        var third = new Border { Width = 90, Height = 18 };
        var collapsed = new Border { Width = 1000, Height = 1000, Visibility = Visibility.Collapsed };
        panel.Children.Add(first); panel.Children.Add(collapsed); panel.Children.Add(second); panel.Children.Add(third);

        void Arrange(HorizontalAlignment alignment)
        {
            panel.RowAlignment = alignment;
            panel.Measure(new Size(150, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, 150, panel.DesiredSize.Height));
        }
        static Point Position(FrameworkElement child, UIElement parent) => child.TranslatePoint(new Point(), parent);
        static bool Near(double left, double right) => Math.Abs(left - right) < 0.01;

        Check(panel.RowAlignment == HorizontalAlignment.Right, "legacy capsule rows keep right alignment by default");
        Arrange(panel.RowAlignment);
        Check(Near(Position(first, panel).X, 80) && Near(Position(second, panel).X, 20)
            && Near(Position(third, panel).X, 60), "default right-to-left row placement remains unchanged");
        Check(Near(panel.DesiredSize.Height, 54), "collapsed controls do not consume row width or height");

        Arrange(HorizontalAlignment.Center);
        Check(Near(Position(first, panel).X, 70) && Near(Position(second, panel).X, 10),
            "the first wrapped row has equal outer space when centered");
        Check(Near(Position(third, panel).X, 30), "a shorter following row centers independently");
        Check(Near(Position(first, panel).Y, 5) && Near(Position(second, panel).Y, 0)
            && Near(Position(third, panel).Y, 36), "center alignment preserves vertical centering and row spacing");

        Arrange(HorizontalAlignment.Right);
        Check(Near(Position(first, panel).X, 80) && Near(Position(third, panel).X, 60),
            "leaving centered preview restores the original arrangement without replacing controls");
    }

    private static void VerifyPreviewPalette(Application app)
    {
        var root = new StackPanel();
        var originalPanel = new SolidColorBrush(Colors.Beige);
        root.Resources["PanelBrush"] = originalPanel;
        var merged = new ResourceDictionary { ["SideBrush"] = new SolidColorBrush(Colors.SlateGray) };
        root.Resources.MergedDictionaries.Add(merged);
        var button = new Button();
        button.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        root.Children.Add(button);
        var globalAccent = app.Resources["AccentBrush"];
        var palette = new PreviewPagePalette();

        palette.Apply(root, enabled: false, blue: true, dark: false);
        Check(ReferenceEquals(root.Resources["PanelBrush"], originalPanel), "blue preference cannot affect the legacy interface");
        for (var iteration = 0; iteration < 3; iteration++)
        {
            palette.Apply(root, enabled: true, blue: true, dark: false);
            Check(ReferenceEquals(button.Background, root.Resources["PanelBrush"]), "preview children resolve their window-scoped palette");
            Check(((SolidColorBrush)root.Resources["PanelBrush"]).Color == Colors.White
                && ((SolidColorBrush)root.Resources["PreviewPageBrush"]).Color == (Color)ColorConverter.ConvertFromString("#EDF5FF")!,
                "light blue mode separates white cards from the blue page background");
            Check(((SolidColorBrush)root.Resources["PanelBrush"]).IsFrozen, "palette brushes cannot accidentally mutate shared theme colors");
            Check(ReferenceEquals(app.Resources["AccentBrush"], globalAccent), "blue preview does not replace an application resource");
            palette.Apply(root, enabled: true, blue: true, dark: true);
            Check(((SolidColorBrush)root.Resources["PanelBrush"]).Color == (Color)ColorConverter.ConvertFromString("#1B2F49")!,
                "switching to dark mode refreshes the blue palette rather than keeping light cards");
            Check(originalPanel.Color == Colors.Beige, "scoped theme updates do not mutate the original local brush");
            palette.Apply(root, enabled: true, blue: false, dark: true);
            Check(ReferenceEquals(root.Resources["PanelBrush"], originalPanel), "following the original theme restores the exact local resource");
            Check(!root.Resources.Keys.Cast<object>().Contains("SideBrush") && ReferenceEquals(root.Resources["SideBrush"], merged["SideBrush"]),
                "a merged resource is revealed without creating a stale direct override");
            Check(!root.Resources.Contains("PreviewPageBrush"), "leaving blue mode removes newly introduced page resources");
        }

        palette.Apply(root, enabled: true, blue: true, dark: false);
        var updatedGlobalAccent = new SolidColorBrush(Colors.Crimson);
        app.Resources["AccentBrush"] = updatedGlobalAccent;
        var updatedMergedSide = new SolidColorBrush(Colors.Lavender);
        merged["SideBrush"] = updatedMergedSide;
        palette.Restore();
        Check(ReferenceEquals(root.TryFindResource("AccentBrush"), updatedGlobalAccent)
            && ReferenceEquals(root.Resources["SideBrush"], updatedMergedSide),
            "restoring preview reveals global and merged theme updates made while blue was enabled");
        app.Resources["AccentBrush"] = globalAccent;

        palette.Apply(root, enabled: true, blue: true, dark: false);
        var anotherRoot = new StackPanel();
        palette.Apply(anotherRoot, enabled: true, blue: true, dark: false);
        Check(ReferenceEquals(root.Resources["PanelBrush"], originalPanel) && anotherRoot.Resources.Contains("PreviewPageBrush"),
            "moving the palette to another scope restores the previous scope first");
        var externalPanel = new SolidColorBrush(Colors.Gold);
        anotherRoot.Resources["PanelBrush"] = externalPanel;
        palette.Restore();
        Check(ReferenceEquals(anotherRoot.Resources["PanelBrush"], externalPanel), "restoring preview preserves a deliberate later local replacement");
    }
}
