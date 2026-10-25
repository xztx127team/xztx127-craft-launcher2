using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool passed, string message)
{
    if (!passed) throw new InvalidOperationException(message);
    checks++;
}

var defaults = new AppConfig();
Check(defaults.SidebarPlacement == SidebarPlacement.Left, "Existing installations keep their left sidebar.");
Check(defaults.SidebarExpandedWidth == 0 && defaults.SidebarLabelFontSize == 0, "Existing theme metrics remain automatic.");
Check(defaults.SidebarShowNavigationIcons && !defaults.SidebarStartCollapsed, "Old icon/expanded defaults remain intact.");
Check(defaults.SidebarLabels.Count == 0, "No default text is renamed.");

var emptyJson = JsonSerializer.Deserialize<AppConfig>("{}")!;
Check(emptyJson.SidebarShowNavigationIcons && emptyJson.SidebarPlacement == SidebarPlacement.Left, "Old JSON needs no migration.");
var edited = new AppConfig
{
    SidebarPlacement = SidebarPlacement.Right, SidebarExpandedWidth = 270, SidebarLabelFontSize = 18,
    SidebarStartCollapsed = true, SidebarShowNavigationIcons = false,
    SidebarLabels = new() { ["downloads"] = "资源中心", ["plugin:future"] = "Future" }
};
var restored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(edited))!;
Check(restored.SidebarPlacement == SidebarPlacement.Right && restored.SidebarExpandedWidth == 270, "Placement and width survive restart.");
Check(restored.SidebarLabelFontSize == 18 && restored.SidebarStartCollapsed && !restored.SidebarShowNavigationIcons, "Presentation flags survive restart.");
Check(restored.SidebarLabels["DOWNLOADS"] == "资源中心" && restored.SidebarLabels.ContainsKey("plugin:future"), "Labels are case-insensitive and preserve unknown future keys.");
var duplicateCase = JsonSerializer.Deserialize<AppConfig>("{\"SidebarLabels\":{\"HOME\":\"First\",\"home\":\"Last\"}}")!;
Check(duplicateCase.SidebarLabels.Count == 1 && duplicateCase.SidebarLabels["home"] == "Last", "Duplicate case in edited JSON cannot break config loading.");
Check(JsonSerializer.Deserialize<AppConfig>("{\"SidebarLabels\":null}")!.SidebarLabels.Count == 0, "Null label dictionary is safe.");

edited.SidebarPlacement = (SidebarPlacement)99;
edited.SidebarExpandedWidth = int.MaxValue;
edited.SidebarLabelFontSize = double.NaN;
Check(edited.SidebarPlacement == SidebarPlacement.Left && edited.SidebarExpandedWidth == 360 && edited.SidebarLabelFontSize == 0, "Corrupt values normalize before layout.");
edited.SidebarExpandedWidth = 1; edited.SidebarLabelFontSize = 100;
Check(edited.SidebarExpandedWidth == 160 && edited.SidebarLabelFontSize == 22, "Tiny widths and extreme fonts cannot collapse navigation.");
Check(SidebarPreferences.NormalizeLabel(" \r\n资源\t中心 \n") == "资源中心", "Control characters cannot create extra navigation rows.");
var emoji = string.Concat(Enumerable.Repeat("😀", 40));
Check(SidebarPreferences.NormalizeLabel(emoji) == string.Concat(Enumerable.Repeat("😀", 32)), "Label trimming preserves UTF-16 surrogate pairs.");
Check(SidebarPreferences.NormalizeLabel(null) == "" && SidebarPreferences.NormalizeLabel("  ") == "", "Clearing text restores the original localized name.");
Check(SidebarPreferences.ExpandedWidth(0, false, false) == 180 && SidebarPreferences.ExpandedWidth(0, true, false) == 220, "Legacy widths match the original language behavior.");
Check(SidebarPreferences.ExpandedWidth(0, false, true) == 192 && SidebarPreferences.ExpandedWidth(160, false, true) == 160, "Preview width honors explicit preferences.");
Check(SidebarPreferences.SidebarColumn(SidebarPlacement.Right) == 1 && SidebarPreferences.SidebarColumn(SidebarPlacement.Left) == 0, "Placement swaps columns without mirroring content.");
Check(SidebarPreferences.Labels.Select(label => label.Key).Distinct().Count() == SidebarPreferences.Labels.Count, "Navigation identifiers are unique.");
Check(!SidebarPreferences.NeedsCompactLayout(760, 0, false, false) && SidebarPreferences.NeedsCompactLayout(759, 0, false, false), "Default layout retains the original 760-DIP boundary.");
Check(SidebarPreferences.NeedsCompactLayout(820, 360, false, false) && !SidebarPreferences.NeedsCompactLayout(940, 360, false, false), "A wide custom sidebar opens as a drawer before it clips ordinary page controls.");
Check(!SidebarPreferences.NeedsCompactLayout(760, 0, true, false) && !SidebarPreferences.NeedsCompactLayout(760, 0, false, true), "Automatic language and preview widths keep their original compact boundary.");
Console.WriteLine($"Sidebar preferences: {checks} checks passed.");
