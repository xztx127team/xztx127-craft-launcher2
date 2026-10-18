using System.Globalization;
using System.Text.RegularExpressions;
using XCL2.App.Services;

var checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    checks++;
}

// 固定模式替换当前候选，重复项/空项与仅取消选择不能产生额外颜色。
var rotation = new List<string> { "Blue", "Pink", "Blue", "", "Forest" };
Check(ThemeSelectionPolicy.FixedCandidates(rotation, "Pink").SequenceEqual(new[] { "Pink" }),
    "fixed mode keeps the explicitly selected replacement color");
Check(rotation.Count == 5, "changing fixed mode does not mutate the existing rotation draft");
Check(ThemeSelectionPolicy.FixedCandidates(rotation, "Missing").SequenceEqual(new[] { "Blue" }),
    "legacy selections fall back deterministically to an existing candidate");
Check(ThemeSelectionPolicy.FixedCandidates(Array.Empty<string>(), "Blue").Count == 0,
    "clearing a list does not invent a replacement candidate");
Check(ThemeSelectionPolicy.IsSameChoice("Custom", "#4c9aff", "Custom", "#4C9AFF"),
    "the same custom color does not trigger a replacement notification");
Check(!ThemeSelectionPolicy.IsSameChoice("Custom", "#4C9AFF", "Custom", "#22C55E"),
    "a second custom color is an actual settings edit");
Check(ThemeSelectionPolicy.IsSameChoice("Blue", "#4C9AFF", "Blue", "#22C55E"),
    "an unused stored custom accent does not change an active preset");
Check(!ThemeSelectionPolicy.IsSameChoice("Blue", null, "Forest", null),
    "changing preset themes is always a settings edit");

var expected = new[] { "Sakura", "Forest", "SeaSalt", "Coffee", "Grape", "Berry", "Dune", "Midnight" };
var catalog = ThemePaletteCatalog.Palettes;
Check(catalog.Count == expected.Length * 2, "all eight new themes have both light and dark palettes");
var fields = typeof(Palette).GetProperties();
Check(fields.Length == 23, "each palette provides every theme resource, not just the accent");

double Luminance(string hex)
{
    double Channel(int offset)
    {
        var value = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
    return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
}
double Contrast(string foreground, string background)
{
    var a = Luminance(foreground);
    var b = Luminance(background);
    return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
}

foreach (var skin in expected)
{
    Check(catalog.TryGetValue((skin, false), out var light) && catalog.TryGetValue((skin, true), out _),
        $"{skin} has a real light/dark implementation");
    var dark = catalog[(skin, true)];
    Check(Luminance(light!.Panel) > Luminance(dark.Panel), $"{skin} dark mode actually darkens the panel");
    foreach (var palette in new[] { light!, dark })
    {
        foreach (var field in fields)
            Check(field.GetValue(palette) is string color && Regex.IsMatch(color, "^#[0-9A-F]{6}$"),
                $"{skin} {field.Name} is a valid opaque RGB theme resource");
        Check(Contrast(palette.TextPrimary, palette.Panel) >= 7, $"{skin} primary panel text stays readable");
        Check(Contrast(palette.TextPrimary, palette.Side) >= 7, $"{skin} primary sidebar text stays readable");
        Check(Contrast(palette.TextSecondary, palette.Panel) >= 4.5, $"{skin} secondary panel text stays readable");
        Check(Contrast(palette.TextSecondary, palette.Side) >= 4.5, $"{skin} secondary sidebar text stays readable");
        Check(Contrast(palette.ButtonForeground, palette.ButtonBackground) >= 4.5, $"{skin} button text stays readable");
        Check(Contrast(palette.ButtonForeground, palette.ButtonHoverBackground) >= 4.5, $"{skin} hovered button text stays readable");
        Check(Contrast(palette.SuccessText, palette.Panel) >= 4.5, $"{skin} success messages stay readable");
        Check(Contrast(palette.WarningText, palette.WarningBanner) >= 4.5, $"{skin} warning banners stay readable");
        Check(Contrast(palette.Danger, palette.Panel) >= 4.5, $"{skin} error messages stay readable");
        Check(Contrast(palette.Accent, palette.Panel) >= 3, $"{skin} accent controls are visible on panels");
    }
}
Check(expected.Select(skin => catalog[(skin, false)].Accent).Distinct().Count() == expected.Length,
    "new theme names map to distinct real palettes");

Console.WriteLine($"Settings appearance regression: {checks} checks passed.");
