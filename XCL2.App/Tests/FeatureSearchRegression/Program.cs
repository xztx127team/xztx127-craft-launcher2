using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    checks++;
}

var entries = FeatureSearchCatalog.Load();
Check(entries.Count > 400, "catalog includes concrete launcher features, not just page links");
Check(entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() == entries.Count,
    "destination ids are unique");
Check(entries.All(entry => entry.Title.Length > 0 && !entry.Title.StartsWith("{")),
    "resource expressions never appear as titles");
Check(FeatureSearchMatcher.Match(entries, null).Count == 0
    && FeatureSearchMatcher.Match(entries, " \t\n\u3000").Count == 0, "empty search has no arbitrary result");

var launch = FeatureSearchMatcher.Match(entries, "启动游戏");
Check(launch[0].TargetName == "LaunchGameBtn", "exact launch button precedes descriptive settings matches");
Check(FeatureSearchMatcher.Match(entries, " launch\tGAME ").Any(entry => entry.TargetName == "LaunchGameBtn"),
    "case-insensitive multiword English alias");
Check(FeatureSearchMatcher.Match(entries, "下载游戏").Any(entry => entry.TargetName == "CatVersion"),
    "download-game synonym targets the real game download category");
Check(FeatureSearchMatcher.Match(entries, "设置 下载 ＪＡＶＡ").Any(entry => entry.TargetName == "DownloadJavaBtn"),
    "multiple words may match page name and localized control, with full-width normalization");
Check(FeatureSearchMatcher.Match(entries, "设置 下载 ＪＡＶＡ impossible-word").Count == 0,
    "all words are required, rather than unrelated partial matches");
Check(FeatureSearchMatcher.Match(entries, "透明", "settings").Any(entry => entry.TargetName == "GlobalWindowOpacitySlider"),
    "named controls use real nearby labels");

var local = FeatureSearchMatcher.Match(entries, "工具", "toolbox");
Check(local.Count > 0 && local.All(entry => entry.PageKey is "toolbox" or "main"), "local scope excludes other pages");
Check(FeatureSearchMatcher.Match(entries, "启动游戏", "toolbox").Any(entry => entry.PageKey == "main"),
    "main window controls remain reachable from current-page scope");
Check(FeatureSearchMatcher.Match(entries, "显卡", "home").Count == 0,
    "home scope does not silently search the settings page");
Check(FeatureSearchMatcher.Match(entries, "显卡").Any(entry => entry.PageKey == "settings"),
    "all scope crosses page boundaries");

var toolboxTabs = entries.Where(entry => entry.Id.StartsWith("toolbox.tab.", StringComparison.Ordinal)).ToArray();
Check(toolboxTabs.Length == 36 && toolboxTabs.Select(entry => entry.TabIndex).SequenceEqual(Enumerable.Range(0, 36).Select(index => (int?)index)),
    "every toolbox tab has the exact zero-based navigation index");
Check(FeatureSearchMatcher.Match(toolboxTabs, "四则")[0].TabIndex == 5, "calculator synonym points to the calculator tab");
Check(FeatureSearchMatcher.Match(toolboxTabs, "ping")[0].TabIndex == 8, "ping synonym points to the server latency tab");
Check(entries.Single(entry => entry.TargetName == "ModSearchBox").SectionKey == "ModPanel",
    "custom download category metadata retains the correct hidden panel");

var sample = new[]
{
    new FeatureSearchEntry("keyword", "settings", "Other", "memory") { PageTitle = "Settings" },
    new FeatureSearchEntry("prefix", "settings", "Memory usage"),
    new FeatureSearchEntry("exact", "settings", "Memory"),
    new FeatureSearchEntry("stable", "settings", "Other", "memory"),
    new FeatureSearchEntry("excluded", "ai", "Memory")
};
Check(FeatureSearchMatcher.Match(sample, "memory", "settings").Select(entry => entry.Id)
    .SequenceEqual(new[] { "exact", "prefix", "keyword", "stable" }),
    "exact/prefix priority and stable equal-ranked ordering keep next/previous predictable");
Check(FeatureSearchMatcher.Match(sample, "memory MEMORY", " SETTINGS ").Count == 4,
    "repeated words and scope whitespace do not duplicate or lose results");

Check(FeatureSearchMatcher.Match(sample, "other memory", "settings", true).Select(entry => entry.Id)
    .SequenceEqual(new[] { "keyword", "stable" }), "optional multiword argument uses all words across title and aliases");
Check(FeatureSearchMatcher.Match(sample, "other memory", "settings", false).Count == 2,
    "single phrase option still finds a contiguous title-plus-keyword phrase");
Check(FeatureSearchMatcher.Match(sample, "memory other", "settings", false).Count == 0,
    "single phrase option does not reorder separated keywords");
Check(entries.Any(entry => entry.TargetName == "Experience_Ux50SearchHistory" && entry.SectionKey == "ExperienceDetailsGroup"),
    "opt-in history setting has the actual dynamic control route");
Check(entries.Any(entry => entry.TargetName == "Experience_EnableAprilFoolsEasterEggs" && entry.PageKey == "settings"),
    "April Fools master switch is globally searchable");

var raw = new[]
{
    new FeatureSearchEntry("localized", "settings", "下载 Java", "java runtime", "DownloadJavaBtn", "下载 Java", ResourceKey: "download")
    { PageTitle = "设置", SectionTitle = "Java 列表", SectionResourceKey = "section" }
};
var localized = FeatureSearchCatalog.Localize(raw, (key, fallback) => key switch
{
    "download" => "Download Java", "section" => "Java runtimes", "Str_Nav_Settings" => "Settings", _ => fallback
});
Check(localized[0].Title == "Download Java" && localized[0].TargetText == "Download Java"
    && localized[0].DisplayPath == "Settings › Java runtimes", "localized titles, target text and path stay aligned");
Check(FeatureSearchMatcher.Match(localized, "下载 Java").Count == 1,
    "original Chinese aliases still work after changing the launcher language");
Check(raw[0].Title == "下载 Java", "localization does not mutate cached source entries");
Check(FeatureSearchMatcher.Normalize("  ＪＡＶＡ\t架构\n") == "JAVA 架构", "Unicode width and whitespace normalize deterministically");

var searchDefault = FeatureSearchLayoutPolicy.Resolve(FeatureSearchLayout.Default, true, 1000, 600);
Check(searchDefault == new FeatureSearchPlacement(0, -2, 300, 36),
    "default floating entry moves down exactly five WPF pixels from the old -7 margin");
Check(FeatureSearchLayoutPolicy.Resolve(FeatureSearchLayout.Default, false, 1000, 600)
    == new FeatureSearchPlacement(700, 0, 300, 36), "top style remains right anchored by default");
var invalidLayout = new FeatureSearchLayout(double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.NaN, -10).Normalize();
Check(invalidLayout == new FeatureSearchLayout(300, 36, 0, 0, 20),
    "invalid imported dimensions and offsets recover safely and opacity stays visible");
var offscreen = new FeatureSearchLayout(500, 100, 9000, 9000, 70);
var clampedSearch = FeatureSearchLayoutPolicy.Resolve(offscreen, true, 420, 200);
Check(clampedSearch == new FeatureSearchPlacement(0, 100, 420, 100),
    "resizing the window contains the entry inside the available area");
var narrowSearch = FeatureSearchLayoutPolicy.Resolve(FeatureSearchLayout.Default, true, 80, 20);
Check(narrowSearch.Width == 80 && narrowSearch.Height == 20 && narrowSearch.X == 0 && narrowSearch.Y == -2,
    "tiny viewports shrink the rendered entry without losing the saved preferred size");
Check(FeatureSearchLayoutPolicy.Resolve(FeatureSearchLayout.Default with { OffsetX = -999, OffsetY = -999 }, true, 1000, 600)
    == searchDefault, "floating entry preserves its intentional -2 upper bound");
var draggedSearch = FeatureSearchLayoutPolicy.FromPlacement(new FeatureSearchPlacement(100, 50, 320, 48),
    FeatureSearchLayout.Default, true, 1000, 600);
Check(FeatureSearchLayoutPolicy.Resolve(draggedSearch, true, 1000, 600)
    == new FeatureSearchPlacement(100, 50, 320, 48), "dragged placement round trips through persisted offsets");
var draggedTopSearch = FeatureSearchLayoutPolicy.FromPlacement(new FeatureSearchPlacement(100, 50, 320, 48),
    FeatureSearchLayout.Default, false, 1000, 600);
Check(FeatureSearchLayoutPolicy.Resolve(draggedTopSearch, false, 1000, 600)
    == new FeatureSearchPlacement(100, 50, 320, 48), "top style dragging round trips through its right anchor");
Check(offscreen.Width == 500 && FeatureSearchLayoutPolicy.Resolve(offscreen, true, 1200, 900).Width == 500,
    "temporary viewport clamping does not mutate the user's preferred geometry");

var holdDrag = new FeatureSearchLongPressDrag();
holdDrag.Begin(100, 80);
Check(holdDrag.Move(103, 82, TimeSpan.FromMilliseconds(150)) == null && !holdDrag.IsDragging,
    "a short held movement does not move the search box or enter drag mode");
Check(holdDrag.Move(130, 100, TimeSpan.FromMilliseconds(499)) == null,
    "all position movement before the full 500 milliseconds stays in the holding phase");
var delayedTimerDelta = holdDrag.Move(150, 115, TimeSpan.FromMilliseconds(510));
Check(holdDrag.IsDragging && delayedTimerDelta == (20d, 15d),
    "the first move after 500ms starts dragging even when the dispatcher timer has not run");
Check(holdDrag.Move(160, 120, TimeSpan.FromMilliseconds(550)) == (30d, 20d),
    "post-hold deltas stay anchored to unapplied movement rather than accumulating preview geometry");
holdDrag.End();
Check(!holdDrag.IsActive && !holdDrag.TryActivate(TimeSpan.FromSeconds(1))
    && holdDrag.Move(200, 200, TimeSpan.FromSeconds(2)) == null,
    "releasing or canceling a gesture prevents a delayed timer and move from reviving it");
holdDrag.Begin(10, 20);
Check(!holdDrag.TryActivate(TimeSpan.FromMilliseconds(499))
    && holdDrag.TryActivate(TimeSpan.FromMilliseconds(500)), "the exact 500ms boundary is deterministic");
Check(holdDrag.Move(40, 60, TimeSpan.FromMilliseconds(700)) == (30d, 40d),
    "a normal stationary long press keeps the first real drag movement");
holdDrag.End();
holdDrag.Begin(0, 0);
Check(holdDrag.Move(1, 2, TimeSpan.FromMilliseconds(100)) == null
    && holdDrag.Move(2, 1, TimeSpan.FromMilliseconds(200)) == null
    && holdDrag.Move(4, 3, TimeSpan.FromMilliseconds(600)) == (2d, 2d),
    "minor pre-hold pointer jitter never permanently disables dragging and is not replayed as a jump");
Console.WriteLine($"PASS: {checks} feature search regression checks; {entries.Count} indexed destinations.");
