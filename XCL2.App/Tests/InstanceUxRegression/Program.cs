using XCL2.App.Models;
using XCL2.App.Services;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var temporary = Path.Combine(Path.GetTempPath(), "xcl-instance-ux-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
XCL2.App.App.DataDir = Path.Combine(temporary, "launcher-data");
try
{
    var cfg = new AppConfig();
    var root = Path.Combine(temporary, "game");
    Check(!cfg.EnableRecentInstanceHistory && cfg.RememberInstanceBrowsePosition, "defaults preserve privacy");
    Check(!InstanceUxService.RecordSuccessfulLaunch(cfg, root, "first") && cfg.RecentInstances.Count == 0, "history remains disabled");
    Check(InstanceUxService.GetIconPath(cfg, root, "first") == null && !Directory.Exists(XCL2.App.App.DataDir), "viewing metadata never creates data directory");
    _ = InstanceUxService.GetVersionDirectory(root, "first");
    Check(!Directory.Exists(root), "viewing a directory never creates game folders");
    foreach (var invalid in new[] { "..", "../escape", "a/b", "a\\b", "C:escape", "/absolute" })
    {
        var rejected = false;
        try { InstanceUxService.GetVersionDirectory(root, invalid); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "reject path escape: " + invalid);
    }
    cfg.EnableRecentInstanceHistory = true;
    for (var i = 0; i < 75; i++) Check(InstanceUxService.RecordSuccessfulLaunch(cfg, root, "v" + i), "enabled history updates");
    Check(cfg.RecentInstances.Count == 50 && cfg.RecentInstances[0].VersionId == "v74", "history bounded newest first");
    InstanceUxService.RecordSuccessfulLaunch(cfg, root, "V74");
    Check(cfg.RecentInstances.Count == 50 && cfg.RecentInstances.Count(r => r.VersionId.Equals("v74", StringComparison.OrdinalIgnoreCase)) == 1, "same instance deduplicates case-insensitively");
    cfg.EnableRecentInstanceHistory = false;
    Check(!InstanceUxService.RecordSuccessfulLaunch(cfg, root, "after-off"), "turning history off stops recording");
    InstanceUxService.Set(cfg, root, "V74", new string('n', 5000), new[] { " 测试 ", "测试", "生存" });
    var value = InstanceUxService.Get(cfg, root, "v74")!;
    Check(value.Notes.Length == 4000 && value.Tags.SequenceEqual(new[] { "测试", "生存" }), "notes bounded and tags deduplicated");
    InstanceUxService.SetIconFile(cfg, root, "v74", "../private.png");
    Check(InstanceUxService.GetIconPath(cfg, root, "v74") == null, "malicious icon path ignored");
    InstanceUxService.Rename(cfg, root, "v74", "renamed");
    Check(InstanceUxService.Get(cfg, root, "v74") == null && InstanceUxService.Get(cfg, root, "renamed") != null && cfg.RecentInstances[0].VersionId == "renamed", "rename preserves metadata and recent reference");
    cfg.FavoriteItems.Add(new FavoriteItem { Type = FavoriteItemType.Mod, Source = ModSource.Modrinth, SourceId = "renamed" });
    InstanceUxService.ToggleFavorite(cfg, root, "renamed");
    Check(InstanceUxService.IsFavorite(cfg, root, "renamed"), "installed instance bookmarked");
    Check(!InstanceUxService.IsFavorite(cfg, root + "-other", "renamed"), "bookmarks distinguish same version ID across game roots");
    InstanceUxService.ToggleFavorite(cfg, root, "renamed");
    Check(!InstanceUxService.IsFavorite(cfg, root, "renamed") && cfg.FavoriteItems.Count == 1, "unfavorite preserves download favorites");
    InstanceUxService.ClearRecent(cfg);
    Check(cfg.RecentInstances.Count == 0, "explicit clear works");
    Console.WriteLine("Instance UX regression checks passed.");
}
finally { Directory.Delete(temporary, true); }
