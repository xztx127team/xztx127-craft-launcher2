using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}

Check(SystemThemeDetectionPolicy.TryParseThemeValue(0, out var dark) && dark,
    "A valid DWORD 0 selects dark mode.");
Check(SystemThemeDetectionPolicy.TryParseThemeValue(1, out var light) && !light,
    "A valid DWORD 1 selects light mode.");

// Missing or malformed registry values must be distinguishable from an explicit light mode.
foreach (var invalid in new object?[] { null, "0", "1", 0L, 1L, -1, 2, false, new object() })
{
    var currentDarkMode = true;
    if (SystemThemeDetectionPolicy.TryParseThemeValue(invalid, out var detectedDarkMode))
        currentDarkMode = detectedDarkMode;
    Check(currentDarkMode, $"Registry value {invalid ?? "<missing>"} must preserve the user's dark theme.");
}

Console.WriteLine($"SystemThemeDetectionRegression: {checks} checks passed.");
