using System.Windows;
using System.Windows.Media;

namespace XCL2.App.Services;

/// <summary>Optional blue surfaces scoped to one preview window. Global themes are never mutated.</summary>
internal sealed class PreviewPagePalette
{
    private sealed record SavedResource(bool Existed, object? Value, Brush Applied);
    private readonly Dictionary<string, SavedResource> _original = new(StringComparer.Ordinal);
    private FrameworkElement? _root;
    private ResourceDictionary? _resources;

    // Text accents and filled actions use separate colors so that both light
    // and dark blue pages retain readable text and clear selection states.
    private static readonly (string Key, string Light, string Dark)[] Colors =
    {
        ("PreviewPageBrush", "#EDF5FF", "#101B2D"),
        ("PreviewHeaderBrush", "#F6FAFF", "#142239"),
        ("PreviewSelectedBrush", "#DFECFF", "#233E60"),
        ("PanelBrush", "#FFFFFF", "#1B2F49"),
        ("SideBrush", "#F7FBFF", "#15263E"),
        ("TextPrimaryBrush", "#172D48", "#EAF2FF"),
        ("TextSecondaryBrush", "#526981", "#B3C5DD"),
        ("AccentBrush", "#1E65B9", "#88C0FF"),
        ("AccentHoverBrush", "#16539B", "#A7D1FF"),
        ("GlowBrush", "#2F7BC9", "#88C0FF"),
        ("GlowSoftBrush", "#DFECFF", "#233E60"),
        ("BorderBrush2", "#B8CFE9", "#3B5B7E"),
        ("BorderHoverBrush", "#6C9ED7", "#83B4E6"),
        ("DividerBrush", "#D6E4F4", "#304A69"),
        ("ButtonBackgroundBrush", "#1E65B9", "#245FA5"),
        ("ButtonHoverBackgroundBrush", "#1858A3", "#2B6DB9"),
        ("ButtonForegroundBrush", "#FFFFFF", "#FFFFFF"),
        ("SoftHeaderBrush", "#E2EFFF", "#233E60"),
        ("SoftHeaderForegroundBrush", "#1B4F88", "#D6E8FF")
    };

    internal void Apply(FrameworkElement root, bool enabled, bool blue, bool dark)
    {
        root.Dispatcher.VerifyAccess();
        if (!enabled || !blue)
        {
            Restore();
            return;
        }

        if (!ReferenceEquals(_root, root) || !ReferenceEquals(_resources, root.Resources))
        {
            Restore();
            _root = root;
            _resources = root.Resources;
        }

        foreach (var (key, light, darkColor) in Colors)
        {
            // Contains also searches merged dictionaries. Only direct keys are
            // ours to restore; inherited resources must become live again.
            var existed = _resources!.Keys.Cast<object>().Contains(key);
            var current = existed ? _resources[key] : null;
            if (!_original.TryGetValue(key, out var saved) || !ReferenceEquals(current, saved.Applied))
                saved = new SavedResource(existed, current, new SolidColorBrush());

            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkColor : light)!);
            brush.Freeze();
            _original[key] = saved with { Applied = brush };
            _resources[key] = brush;
        }
    }

    internal void Restore()
    {
        _root?.Dispatcher.VerifyAccess();
        if (_resources != null)
        {
            foreach (var (key, saved) in _original)
            {
                // A later local owner may intentionally replace or remove a
                // resource. Disabling preview must not undo that change.
                if (!_resources.Keys.Cast<object>().Contains(key) || !ReferenceEquals(_resources[key], saved.Applied))
                    continue;
                if (saved.Existed) _resources[key] = saved.Value;
                else _resources.Remove(key);
            }
        }
        _original.Clear();
        _root = null;
        _resources = null;
    }
}
