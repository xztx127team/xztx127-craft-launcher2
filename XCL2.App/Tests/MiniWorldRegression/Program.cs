using XCL2.App.Services;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
void Reject<T>(Action action, string name) where T : Exception
{ try { action(); } catch (T) { checks++; return; } throw new Exception(name); }

Check(MiniWorldCatalog.IsCorrectAnswer("中立群体"), "neutral answer accepted");
Check(!MiniWorldCatalog.IsCorrectAnswer(null) && !MiniWorldCatalog.IsCorrectAnswer("支持某个游戏"), "blank and non-neutral answers rejected");
var answers = MiniWorldCatalog.ReadingQuestions.Select(question => question.CorrectIndex).ToArray();
Check(answers.Length >= 8 && MiniWorldCatalog.AreReadingAnswersCorrect(answers), "all reading questions are required and accepted together");
for (var i = 0; i < answers.Length; i++)
{
    var wrong = answers.ToArray(); wrong[i] = -1;
    Check(!MiniWorldCatalog.AreReadingAnswersCorrect(wrong), "a missing answer cannot pass the gate");
    wrong[i] = (answers[i] + 1) % MiniWorldCatalog.ReadingQuestions[i].Choices.Count;
    Check(!MiniWorldCatalog.AreReadingAnswersCorrect(wrong), "any wrong answer blocks entry");
}
Check(!MiniWorldCatalog.AreReadingAnswersCorrect(Array.Empty<int>()), "empty answer set cannot bypass the gate");
Check(MiniWorldCatalog.Sources.Select(source => source.Id).Distinct().Count() == MiniWorldCatalog.Sources.Count, "unique source identities");
Check(MiniWorldCatalog.Sources.All(source => Uri.TryCreate(source.HomeUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https"), "sources use real HTTPS pages");
Check(MiniWorldCatalog.Sources.Where(source => source.DirectDownloadUrl != null).All(source => source.DownloadFileName?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true), "direct downloads have explicit EXE targets");
var historySource = MiniWorldCatalog.Sources.Single(source => source.Id == "cbl-history");
var communitySource = MiniWorldCatalog.Sources.Single(source => source.Id == "cbl-community");
const string index = """
[{"游戏ID":"001000043","游戏名称":"迷你世界0.43.0","版本号":"0.43.0","修改版本号":"0","游戏包体":"https://file.song3060.top/f/d/RmPsJ/O_MiniWorld000043000_001000043.7z","游戏包体2":"https://mengzee.lanzoul.com/ivNVx3n18eid 1234","简介":"历史版本","详细介绍":[],"相关链接":"https://www.mini1.cn/"},
 {"游戏ID":"001000052","游戏名称":"迷你世界0.52.1","版本号":"0.52.1","游戏包体":"https://file.song3060.top/f/d/7rHn/O_MiniWorld000052001_001000052.7z"}]
""";
var parsedHistory = MiniWorldVersionService.ParseCblIndex(index, historySource);
Check(parsedHistory.Count == 2 && parsedHistory[0].Version == "0.43.0" && parsedHistory[1].Version == "0.52.1", "real CBL schema yields multiple selectable historical versions");
Check(parsedHistory[0].PublishedDate == null && parsedHistory[0].DateLabel.Contains("来源未提供"), "unknown release date remains unknown");
Check(parsedHistory[0].PublicShares.Single().ExtractionCode == "1234", "published backup and extraction code retained");
var parsedCommunity = MiniWorldVersionService.ParseCblIndex(index.Replace("001000043", "002000041").Replace("修改版本号\":\"0", "修改版本号\":\"4"), communitySource);
Check(parsedCommunity[0].Version.Contains("社区修订 4") && parsedCommunity[0].ClientKey != parsedHistory[0].ClientKey, "community revision and launch binding remain separate from original version");
Check(parsedHistory[0].DownloadDirectoryKey != parsedHistory[1].DownloadDirectoryKey && parsedHistory.All(version => version.DownloadDirectoryKey.All(Uri.IsHexDigit)), "versions have distinct safe download directories");
Check(!MiniWorldVersionService.IsSupportedFileName("../escape.7z") && !MiniWorldVersionService.IsSupportedFileName("CON.exe") && !MiniWorldVersionService.IsSupportedFileName("file.exe ") && !MiniWorldVersionService.IsSupportedFileName("a\\b.zip"), "Windows path traversal and reserved file names rejected");
Check(MiniWorldCatalog.ClientKey("a:b", "c") != MiniWorldCatalog.ClientKey("a", "b:c"), "binding keys cannot collide through delimiters");
Reject<System.IO.InvalidDataException>(() => MiniWorldVersionService.ParseCblIndex("{}", historySource), "changed upstream schema is a visible failure");
const string customIndex = """
{"versions":[{"id":"community-044","name":"社区版","version":"0.44.2","url":"https://downloads.example.org/client.7z","mirrors":["https://mirror.example.org/client.7z"],"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","publishedDate":"2026-10-22"}]}
""";
var imported = MiniWorldVersionService.ParseIndex(customIndex, communitySource).Single();
Check(imported.Mirrors.Count == 1 && imported.Sha256.Length == 64 && imported.PublishedDate == "2026-10-22", "open catalog preserves version mirrors hash and date");
Check(MiniWorldVersionService.ParseIndex(index, historySource).Count == 2, "open parser remains compatible with CBL");
Reject<InvalidDataException>(() => MiniWorldVersionService.ParseIndex(customIndex.Replace("https://mirror.example.org/client.7z", "file:///secret.zip"), communitySource), "non-web mirror rejected");
Reject<InvalidDataException>(() => MiniWorldVersionService.ParseIndex(customIndex.Replace("2026-10-22", "2026-02-30"), communitySource), "invalid date rejected without partial import");
Reject<InvalidDataException>(() => MiniWorldVersionService.ParseIndex(customIndex.Replace("\"url\":", "\"fileName\":\"../bad.exe\",\"url\":"), communitySource), "import cannot escape download directory");
var root = Path.Combine(Path.GetTempPath(), "XCL2-MiniWorld-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    Reject<InvalidOperationException>(() => MiniWorldCatalog.ValidateExecutable(null), "unset launch path rejected");
    Reject<FileNotFoundException>(() => MiniWorldCatalog.ValidateExecutable(Path.Combine(root, "missing.exe")), "missing launch target rejected");
    var html = Path.Combine(root, "fake.exe"); File.WriteAllText(html, "<!DOCTYPE html><html>Expired link</html>");
    Reject<InvalidDataException>(() => MiniWorldCatalog.ValidateDownloadedExecutable(html), "HTML masquerading as EXE rejected");
    var invalid = new byte[128]; invalid[0] = (byte)'M'; invalid[1] = (byte)'Z'; BitConverter.GetBytes(int.MaxValue).CopyTo(invalid, 0x3c);
    File.WriteAllBytes(html, invalid); Reject<InvalidDataException>(() => MiniWorldCatalog.ValidateDownloadedExecutable(html), "invalid PE offset rejected");
    BitConverter.GetBytes(64).CopyTo(invalid, 0x3c); invalid[64] = (byte)'P'; invalid[65] = (byte)'E';
    File.WriteAllBytes(html, invalid); MiniWorldCatalog.ValidateDownloadedExecutable(html); checks++;
    Check(MiniWorldCatalog.ValidateExecutable(html) == Path.GetFullPath(html), "existing user-selected EXE resolved without running it");
    var archive = Path.Combine(root, "old.7z"); File.WriteAllBytes(archive, new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c, 0, 4 }); MiniWorldVersionService.ValidateDownloadedPackage(archive); checks++;
    File.WriteAllText(archive, "<html>verification</html>"); Reject<InvalidDataException>(() => MiniWorldVersionService.ValidateDownloadedPackage(archive), "verification page cannot be saved as 7z");
    var second = Path.Combine(root, "old.7z.002"); File.WriteAllText(second, "  <html>verification</html>"); Reject<InvalidDataException>(() => MiniWorldVersionService.ValidateDownloadedPackage(second), "HTML cannot masquerade as a subsequent archive volume");
    File.WriteAllText(second, "{\"error\":\"verification\"}"); Reject<InvalidDataException>(() => MiniWorldVersionService.ValidateDownloadedPackage(second), "JSON cannot masquerade as a subsequent archive volume");
    File.WriteAllBytes(second, new byte[] { 0x82, 0, 0x45, 0x90, 0xf1, 0x14, 0x55, 0x34 }); MiniWorldVersionService.ValidateDownloadedPackage(second); checks++;
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"MiniWorld regression: {checks} checks passed");
