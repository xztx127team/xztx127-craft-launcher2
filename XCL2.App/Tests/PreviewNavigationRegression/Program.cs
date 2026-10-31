using XCL2.App.Services;

var passed = 0;
void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); passed++; }
var state = new PreviewNavigationState();
Check(PreviewNavigationState.Categories.Select(item => item.Id).Distinct().Count() == 5, "five distinct top-level groups");
foreach (var page in new[] { "home", "instances", "worlds", "mods", "bedrock", "downloads", "multiplayer", "servers", "ai", "toolbox", "logs", "accounts", "settings", "plugins", "api", "experimental", "about", "more", "plugin:custom-page" })
    Check(PreviewNavigationState.CategoryForPage(page) != null, "existing page assigned to category: " + page);
Check(state.Category == "launch", "initial launch group");
state.ObservePage("worlds");
Check(state.Category == "launch", "worlds belongs to launch");
Check(state.SelectCategory("tools", new[] { "toolbox", "ai", "logs" }) == "toolbox", "tools defaults to original toolbox page");
state.ObservePage("ai");
state.SelectCategory("settings", new[] { "settings", "accounts" }); state.ObservePage("settings");
Check(state.SelectCategory("tools", new[] { "toolbox", "ai", "logs" }) == "ai", "remember last actual tools page");
Check(state.SelectCategory("launch", new[] { "home", "worlds" }) == "worlds", "remember actual world page across groups");
Check(state.SelectCategory("tools", new[] { "toolbox", "logs" }) == "toolbox", "hidden or disabled remembered page falls back to available default");
Check(state.SelectCategory("settings", new[] { "accounts" }) == "accounts", "hidden default falls back to allowed entry");
Check(state.SelectCategory("downloads", Array.Empty<string>()) == null, "no available page produces no navigation");
state.ObservePage("settings");
state.SelectCategory("launch", new[] { "home" });
state.ObservePage("settings");
Check(state.Category == "settings", "cancelled unsaved navigation follows actual old page");
state.SelectCategory("multiplayer", new[] { "servers", "multiplayer" }); state.ObservePage(null);
Check(state.Category == "multiplayer", "async page loading retains requested category");
state.ObservePage("plugin:custom-page");
Check(state.Category == "settings", "live plugin page uses settings group");
state.SelectCategory("launch", new[] { "home" });
Check(state.SelectCategory("settings", new[] { "settings", "plugin:custom-page" }) == "plugin:custom-page", "live refreshed plugin page retains stable selection");
Check(state.SelectCategory("settings", new[] { "settings" }) == "settings", "removed plugin page falls back to settings");
state.ObservePage("unknown");
Check(state.Category == "settings", "unknown content does not change category");
Check(state.SelectCategory("unknown", new[] { "home" }) == null && state.Category == "settings", "unknown category does not navigate or mutate selection");
Console.WriteLine($"PASS {passed} preview navigation checks");
