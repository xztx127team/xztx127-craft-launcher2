using System.Text.Json;
using XCL2.App.Services;

var checks = 0;
JsonElement Version(string id, string game, string loader, string date = "2026-10-01T00:00:00Z", string project = "example", bool files = true)
    => JsonSerializer.SerializeToElement(new { id, project_id = project, game_versions = game.Split(','), loaders = loader.Split(','), date_published = date, files = files ? new[] { new { filename = "mod.jar" } } : Array.Empty<object>() });
void Select(JsonElement current, string game, string? loader, string? expected, bool isMod, params JsonElement[] versions)
{
    var found = ModUpdateSelectionPolicy.TrySelect(current, JsonSerializer.SerializeToElement(versions), game, loader, isMod, out var actual);
    if (found != (expected != null) || found && actual.GetProperty("id").GetString() != expected)
        throw new Exception($"Expected {expected ?? "no update"}, got {(found ? actual.GetProperty("id").GetString() : "none")}");
    checks++;
}
var old = Version("old", "26.2", "fabric", "2026-09-01T00:00:00Z");
Select(old, "26.2", "Fabric", "good", true, Version("wrong-game", "26.3", "fabric"), Version("good", "26.2", "fabric"));
Select(old, "26.2", "Fabric", null, true, Version("wrong-game", "26.3", "fabric"));
Select(old, "26.2", "Fabric", null, true, Version("wrong-loader", "26.2", "neoforge"));
Select(old, "26.2", "Fabric", "newest", true, Version("older", "26.2", "fabric"), Version("newest", "26.2", "fabric", "2026-10-02T00:00:00Z"));
Select(old, "26.2", "Fabric", null, true, Version("other-project", "26.2", "fabric", project: "unrelated"));
Select(old, "26.2", "Fabric", null, true, Version("no-files", "26.2", "fabric", files: false));
Select(old, "26.2", "Fabric", null, true, old);
Select(old, "26.2", "Fabric", null, true, Version("downgrade", "26.2", "fabric", "2026-08-01T00:00:00Z"));
Select(old, "26.3", "Fabric", "same-version-number-other-game", true, Version("same-version-number-other-game", "26.3", "fabric", "2026-08-01T00:00:00Z"));
Select(old, "26.2", "Forge", "bridge-fabric", true, Version("forge-conversion", "26.2", "forge"), Version("bridge-fabric", "26.2", "fabric"));
Select(old, "26.2", null, "known-loader", true, Version("known-loader", "26.2", "fabric"));
Select(Version("old", "26.2", "fabric,neoforge"), "26.2", null, null, true, Version("unsafe-choice", "26.2", "fabric"));
Select(Version("old", "26.2", "neoforge", "2026-09-01T00:00:00Z"), "26.2", "NeoForge", "nf", true, Version("f", "26.2", "fabric"), Version("nf", "26.2", "neoforge"));
Select(old, "", "Fabric", null, true, Version("good", "26.2", "fabric"));
Select(Version("old", "26.2", "minecraft", "2026-09-01T00:00:00Z"), "26.2", null, "resource", false, Version("resource", "26.2", "minecraft"));
Select(old, " 26.2 ", " Fabric ", "trimmed", true, Version("trimmed", "26.2", "fabric"));
void Filter(bool expected, string[]? games, string[]? loaders, string? game, string[]? accepted)
{
    if (ModUpdateSelectionPolicy.MatchesFilters(games, loaders, game, accepted) != expected)
        throw new Exception("Incorrect local version-list filter");
    checks++;
}
Filter(false, new[] { "1.20.1" }, new[] { "fabric" }, "1.21.1", new[] { "fabric" });
Filter(false, new[] { "1.21.1" }, null, "1.21.1", new[] { "fabric" });
Filter(false, new[] { "1.21.1" }, Array.Empty<string>(), "1.21.1", new[] { "fabric" });
Filter(true, new[] { "1.21.1" }, new[] { "fabric", "quilt" }, "1.21.1", new[] { "fabric" });
Filter(true, new[] { "1.21.1" }, new[] { "fabric" }, "1.21.1", new[] { "quilt", "fabric" });
Filter(true, null, null, null, null);
var memory = new DownloadDirectoryMemory();
var root = Path.Combine(Path.GetTempPath(), "XCL2-memory-fixture");
var forgeMods = Path.Combine(root, "Forge", "mods");
var neoMods = Path.Combine(root, "NeoForge", "mods");
var resourcePacks = Path.Combine(root, "Forge", "resourcepacks");
var custom = Path.Combine(root, "MyMods");
memory.Remember(forgeMods, custom);
if (memory.Resolve(forgeMods + Path.DirectorySeparatorChar) != custom || memory.Resolve(neoMods) != neoMods ||
    memory.Resolve(resourcePacks) != resourcePacks) throw new Exception("Download destination leaked across instance/category");
checks += 3;
Console.WriteLine($"PASS {checks} mod update compatibility checks");
