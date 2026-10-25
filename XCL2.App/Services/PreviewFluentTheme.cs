using System.Collections;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace XCL2.App.Services;

/// <summary>Fluent visual resources for existing, fully interactive WPF pages.</summary>
public static class PreviewFluentTheme
{
    // Local page surfaces can opt into the preview without changing legacy
    // values. Inheritance also reaches controls instantiated from templates.
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(PreviewFluentTheme),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);

    private sealed record SavedResource(bool Existed, object? Value);
    private static readonly Dictionary<object, SavedResource> Original = new();
    private static readonly Dictionary<Style, Style> EnabledStyles = new();
    private static readonly Dictionary<Style, Style> DerivedStyles = new();
    private static readonly Dictionary<Style, Style> Replacements = new();
    private static readonly Dictionary<Style, Style> StyleCache = new();
    // Detached cached pages (including the ordinary AI page) can return after
    // several toggles. Keep their original style without retaining dead pages.
    private static readonly ConditionalWeakTable<Style, Style> StyleOrigins = new();
    private static readonly ConditionalWeakTable<DependencyObject, object> ThemeScopes = new();
    private static bool _enabled;
    private static bool _hasPreviewStyles;
    internal static bool IsEnabled => _enabled;

    static PreviewFluentTheme()
    {
        // StaticResource references inside a DataTemplate are captured before
        // its future/recycled rows are created. Migrate each newly loaded local
        // style too, otherwise rows created after a toggle keep the old mode.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnElementLoaded), handledEventsToo: true);
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs args)
    {
        if (!_hasPreviewStyles || sender is not FrameworkElement element
            || !ReferenceEquals(sender, args.OriginalSource)) return;
        if (ThemeScopes.TryGetValue(element, out _)) element.SetCurrentValue(IsActiveProperty, _enabled);
        ApplyLocalStyle(element);
    }

    private static void ApplyLocalStyle(FrameworkElement element)
    {
        // A DynamicResource/Binding expression must remain an expression.
        if (element.ReadLocalValue(FrameworkElement.StyleProperty) is not Style style) return;
        var replacement = Resolve(style);
        if (!ReferenceEquals(style, replacement)) element.Style = replacement;
    }

    internal static void SetEnabled(bool enabled, FrameworkElement root)
    {
        root.Dispatcher.VerifyAccess();
        if (_enabled == enabled) return;
        var application = Application.Current ?? throw new InvalidOperationException("Application is not initialized.");
        var resources = application.Resources;
        Replacements.Clear(); StyleCache.Clear();
        if (enabled)
        {
            var dictionary = new ResourceDictionary { Source = new Uri("/XCL2;component/Resources/Themes/PreviewFluent.xaml", UriKind.Relative) };
            // ThemeService normally adds this rule once. A preview replaces the
            // implicit styles, so retain the same dynamic font rule on each one.
            foreach (var key in dictionary.Keys.Cast<object>().ToArray())
            {
                if (key is Type type && dictionary[key] is Style visualStyle)
                {
                    var fontStyle = new Style(type, visualStyle);
                    fontStyle.Setters.Add(new Setter(System.Windows.Controls.Control.FontFamilyProperty,
                        new DynamicResourceExtension("AppFontFamily")));
                    dictionary[key] = fontStyle;
                }
            }
            Original.Clear(); EnabledStyles.Clear(); DerivedStyles.Clear();
            foreach (DictionaryEntry entry in dictionary)
            {
                bool existed = resources.Contains(entry.Key);
                var previous = existed ? resources[entry.Key] : null;
                Original[entry.Key] = new SavedResource(existed, previous);
                if (previous is Style before && entry.Value is Style after)
                {
                    EnabledStyles[before] = after;
                    if (!StyleOrigins.TryGetValue(after, out _)) StyleOrigins.Add(after, before);
                }
            }
            // App.xaml owns direct entries; merged dictionaries cannot override them.
            foreach (DictionaryEntry entry in dictionary) resources[entry.Key] = entry.Value;
            foreach (var pair in EnabledStyles) Replacements[pair.Key] = pair.Value;
        }
        else
        {
            foreach (var pair in Original)
            {
                if (pair.Value.Existed) resources[pair.Key] = pair.Value.Value;
                else resources.Remove(pair.Key);
            }
            foreach (var pair in EnabledStyles) Replacements[pair.Value] = pair.Key;
            foreach (var pair in DerivedStyles) Replacements[pair.Key] = pair.Value;
        }
        _enabled = enabled;
        _hasPreviewStyles |= enabled;
        ApplyTo(root);
        foreach (var window in application.Windows.OfType<Window>().ToArray())
            if (!ReferenceEquals(window, root) && window.Dispatcher.CheckAccess()) ApplyTo(window);
    }

    internal static void ApplyTo(DependencyObject root)
    {
        ThemeScopes.GetValue(root, _ => new object());
        root.SetCurrentValue(IsActiveProperty, _enabled);
        var seen = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            // A detail/page can have been a detached ApplyTo root earlier. Its
            // local value must follow the window toggle or it masks inheritance
            // and leaves preview-only surfaces active after switching back.
            if (ThemeScopes.TryGetValue(current, out _)) current.SetCurrentValue(IsActiveProperty, _enabled);
            // Keep dynamic resource references and style bindings intact.
            if (current is FrameworkElement element) ApplyLocalStyle(element);
            if (current is Popup { Child: { } child }) pending.Push(child);
            if (current is Visual or Visual3D)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++) pending.Push(VisualTreeHelper.GetChild(current, i));
            if (current is FrameworkElement or FrameworkContentElement)
                foreach (var logical in LogicalTreeHelper.GetChildren(current))
                    if (logical is DependencyObject dependency) pending.Push(dependency);
        }
    }

    private static Style Resolve(Style style)
    {
        if (Replacements.TryGetValue(style, out var replacement)) return replacement;
        if (StyleCache.TryGetValue(style, out var cached)) return cached;
        if (StyleOrigins.TryGetValue(style, out var original))
        {
            var current = _enabled ? Resolve(original) : original;
            StyleCache[style] = current;
            return current;
        }
        if (style.BasedOn is not { } previousBase) return style;
        var nextBase = Resolve(previousBase);
        if (ReferenceEquals(previousBase, nextBase)) return style;
        var derived = new Style(style.TargetType, nextBase);
        foreach (var setter in style.Setters) derived.Setters.Add(setter);
        foreach (var trigger in style.Triggers) derived.Triggers.Add(trigger);
        foreach (DictionaryEntry resource in style.Resources) derived.Resources[resource.Key] = resource.Value;
        foreach (var dictionary in style.Resources.MergedDictionaries) derived.Resources.MergedDictionaries.Add(dictionary);
        StyleCache[style] = derived;
        if (_enabled)
        {
            DerivedStyles[derived] = style;
            StyleOrigins.Add(derived, style);
        }
        return derived;
    }
}
