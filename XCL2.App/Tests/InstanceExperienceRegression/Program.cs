using System.IO.Compression;
using XCL2.App.Models;
using XCL2.App.Services;

static void Check(bool condition, string description) { if (!condition) throw new Exception(description); }
var temporary = Path.Combine(Path.GetTempPath(), "xcl-instance-experience-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var cfg = new AppConfig(); var source = Path.Combine(temporary, "source"); var destination = Path.Combine(temporary, "destination");
    Directory.CreateDirectory(source); Directory.CreateDirectory(Path.Combine(source, "config"));
    File.WriteAllText(Path.Combine(source, "config", "sample.txt"), "original");
    var preview = ImportPreviewService.Inspect(source, destination, "test", ImportPreviewKind.InstanceFolder);
    Check(preview.FileCount == 1 && !Directory.Exists(destination), "folder preview counts files without creating destination");
    Directory.CreateDirectory(Path.Combine(destination, "config")); File.WriteAllText(Path.Combine(destination, "config", "sample.txt"), "later");
    preview = ImportPreviewService.Inspect(source, destination, "test", ImportPreviewKind.InstanceFolder);
    Check(preview.ConflictCount == 1 && File.ReadAllText(Path.Combine(destination, "config", "sample.txt")) == "later", "preview counts conflicts without overwriting");
    var archive = Path.Combine(temporary, "pack.mrpack");
    using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
    {
        using (var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open())) writer.Write("{\"files\":[{\"path\":\"mods/a.jar\"},{\"path\":\"mods/server.jar\",\"env\":{\"client\":\"unsupported\"}}]}");
        using var payload = new StreamWriter(zip.CreateEntry("overrides/config/sample.txt").Open()); payload.Write("replacement");
    }
    preview = ImportPreviewService.Inspect(archive, destination, "test", ImportPreviewKind.ModpackArchive);
    Check(preview.FileCount == 2 && preview.ConflictCount == 1, "mrpack includes client downloads and maps overrides to real targets");
    var malicious = Path.Combine(temporary, "bad.zip");
    using (var zip = ZipFile.Open(malicious, ZipArchiveMode.Create)) using (var writer = new StreamWriter(zip.CreateEntry("mods/../../outside.txt").Open())) writer.Write("escape");
    var rejected = false; try { ImportPreviewService.Inspect(malicious, destination, "bad", ImportPreviewKind.ModpackArchive); } catch (InvalidDataException) { rejected = true; }
    Check(rejected && !File.Exists(Path.Combine(temporary, "outside.txt")), "unsafe archive rejected without extraction");
    var link = Path.Combine(source, "linked");
    try
    {
        Directory.CreateSymbolicLink(link, destination); rejected = false;
        try { ImportPreviewService.Inspect(source, destination, "linked", ImportPreviewKind.InstanceFolder); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "source symlink never followed"); Directory.Delete(link);
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException) { Console.WriteLine("Symbolic-link test skipped: platform permission."); }
    var first = new GameVersion { Id = "one", McVersion = "1.21", ModLoader = "Fabric", FolderPath = Path.Combine(source, "versions", "one") };
    var second = new GameVersion { Id = "two" };
    Check(!InstanceExperienceService.SharesGameDirectory(cfg, source, first, new[] { first, second }), "isolated instances not marked shared");
    cfg.IsolateVersionsByDefault = false;
    Check(InstanceExperienceService.SharesGameDirectory(cfg, source, first, new[] { first, second }), "shared roots marked when actual second instance exists");
    Check(!InstanceExperienceService.SharesGameDirectory(cfg, source, first, new[] { first }), "single root alone not falsely shared");
    var summary = InstanceExperienceService.EnvironmentSummary(first, true, 3, 2);
    Check(!summary.Contains(source) && !summary.Contains(first.Id) && summary.Contains("启用 3 / 禁用 2"), "environment summary excludes instance name/path");
    var folders = new[] { new GameFolder { Name = "游戏", Path = Path.Combine(temporary, "alpha", ".minecraft") }, new GameFolder { Name = "游戏", Path = Path.Combine(temporary, "beta", ".minecraft") } };
    Check(InstanceExperienceService.FolderLabel(folders[0], folders, true) != InstanceExperienceService.FolderLabel(folders[1], folders, true), "duplicate root names distinguished");
    var candidate = new UpdateCandidate { ProjectId = "project", NewVersionId = "v1", DisplayName = "Sample", NewVersionName = "1" };
    ModUpdatePreferenceService.Ignore(cfg, source, candidate);
    Check(ModUpdatePreferenceService.IsIgnored(cfg, source, candidate) && !ModUpdatePreferenceService.IsIgnored(cfg, destination, candidate), "ignore scoped to exact instance game directory");
    candidate.NewVersionId = "v2"; Check(!ModUpdatePreferenceService.IsIgnored(cfg, source, candidate), "later versions remain visible");
    cfg.Ux50IgnoredModUpdateVersions.Clear(); candidate.NewVersionId = "v1";
    Check(!ModUpdatePreferenceService.IsIgnored(cfg, source, candidate), "undo restores exact version visibility");
    ModUpdatePreferenceService.Remember(source, new[] { candidate });
    Check(ModUpdatePreferenceService.GetCached(source)?.Candidates.Count == 1 && ModUpdatePreferenceService.GetCached(destination) == null, "cached metadata does not leak across instances");
    Console.WriteLine("Instance experience service regressions passed.");
}
finally { Directory.Delete(temporary, recursive: true); }
