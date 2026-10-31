using System.IO.Compression;
using XCL2.App.Services;
var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
void Reject(Action action, string message) { try { action(); } catch (IOException) { checks++; return; } throw new Exception(message); }
var secrets = new[]
{
    ("--accessToken token_fixture_secret", "token_fixture_secret"),
    ("--session token:legacy_session_secret:1234", "legacy_session_secret"),
    ("password=\"unfinished multi word secret\nnext diagnostic line", "multi word secret"),
    ("[HTTP] Authorization: Bearer prefixed_header_secret", "prefixed_header_secret"),
    ("Server: fixture | Set-Cookie: first=x; second=joined_cookie_secret", "joined_cookie_secret"),
    ("Proxy-Authorization: Basic proxy_header_secret", "proxy_header_secret"),
    ("[INFO]: Setting user: private_setting_user", "private_setting_user"),
    ("{\"password\":\"prefix\\\"escaped_password_secret\"}", "escaped_password_secret"),
    ("{\"accessToken\":\"prefix\\\"escaped_token_secret\"}", "escaped_token_secret"),
    ("[启动游戏] 账户=Private Player Fixture 版本=1.20.1", "Private Player Fixture"),
    ("--password=\"quoted password fixture\"", "quoted password fixture"),
    ("{\"refresh_token\":\"refresh_fixture_secret\"}", "refresh_fixture_secret"),
    ("{\"username\":\"private_player_fixture\"}", "private_player_fixture"),
    ("--uuid 1234567890abcdef1234567890abcdef --xuid 1234567890123456", "1234567890abcdef1234567890abcdef"),
    ("--uuid 1234567890abcdef1234567890abcdef --xuid 1234567890123456", "1234567890123456"),
    ("{\"profileId\":\"private-profile-fixture\"}", "private-profile-fixture"),
    ("Authorization: Bearer bearer_fixture_secret", "bearer_fixture_secret"),
    ("Cookie: session=cookie_one_secret; other=cookie_two_secret", "cookie_two_secret"),
    ("https://user:pass_fixture_secret@example.org/files?code=code_fixture_secret", "pass_fixture_secret"),
    ("https://user:pass_fixture_secret@example.org/files?code=code_fixture_secret", "code_fixture_secret"),
    ("https://example.org/files?password=hidden&code=other_query_secret", "other_query_secret"),
    ("https://example.org/files?code=code_fixture_secret&hint=x", "code_fixture_secret"),
    ("api_key: sk-abcxyzsecret123456789", "sk-abcxyzsecret123456789"),
    ("mail player_private_fixture@example.com", "player_private_fixture@example.com"),
    (@"C:\Users\Private User\Minecraft\logs\latest.log", "Private User"),
    ("/home/private_user_fixture/.minecraft/logs/latest.log", "private_user_fixture")
};
foreach (var (input, secret) in secrets)
    Check(!DiagnosticRedactionService.Sanitize(input).Contains(secret, StringComparison.Ordinal), "Secret remained: " + secret);
Check(DiagnosticRedactionService.Sanitize("java.lang.OutOfMemoryError in example.ModClass").Contains("OutOfMemoryError"), "Useful error class was removed");
Check(DiagnosticRedactionService.Sanitize(new string('a', 100000), 1000).Length < 1100, "Redacted input not bounded");
Check(ServerConnectionCopyService.AddressOnly("https://user:secret@example.org:25565/?password=secret") == "example.org:25565", "Address copy leaked credentials/query");
Check(ServerConnectionCopyService.AddressOnly("[::1]:25565") == "[::1]:25565", "IPv6 address changed");
var root = Path.Combine(Path.GetTempPath(), "xcl2-diagnostics-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var entries = DiagnosticsExportService.Prepare(new[] { new DiagnosticExportSource("../title", "password=export_fixture_secret\nexpected-preview"), new DiagnosticExportSource("Not selected", "unselected_fixture_text") });
    var zip = Path.Combine(root, "diagnostic.zip");
    DiagnosticsExportService.Export(zip, new[] { entries[0] });
    using (var archive = ZipFile.OpenRead(zip))
    {
        Check(archive.Entries.All(x => !x.FullName.Contains("..")), "Untrusted title became ZIP path");
        using var reader = new StreamReader(archive.GetEntry("diagnostic-01.txt")!.Open());
        var content = reader.ReadToEnd();
        Check(content.Contains(entries[0].SanitizedText) && !content.Contains("export_fixture_secret") && !content.Contains("unselected_fixture_text"), "Export differed from selected safe preview");
    }
    var screenshots = Path.Combine(root, "screenshots"); Directory.CreateDirectory(screenshots);
    var screenshot = Path.Combine(screenshots, "one.png"); File.WriteAllText(screenshot, "fixture image bytes");
    File.WriteAllText(Path.Combine(screenshots, "not-image.txt"), "do not browse");
    var list = ScreenshotBrowserService.List(root); Check(list.Count == 1, "Non-image screenshot accepted");
    var snapshot = list[0];
    File.AppendAllText(screenshot, "later edit");
    Reject(() => ScreenshotBrowserService.Delete(root, snapshot), "Delete removed changed screenshot");
    Check(File.Exists(screenshot), "Failed delete changed the file");
    var outside = Path.Combine(root, "outside.png"); File.WriteAllText(outside, "outside");
    Reject(() => ScreenshotBrowserService.Validate(root, new(outside, "outside.png", 7, File.GetLastWriteTimeUtc(outside))), "Outside screenshot accepted");
    File.WriteAllText(Path.Combine(root, "server.properties"), "enable-rcon=true\nrcon.password=explicit_only_fixture\nrcon.port=25576\n");
    Check(ServerConnectionCopyService.BuildRconConnection(root, "localhost:25565").Contains("explicit_only_fixture"), "Explicit sensitive copy failed");
    var enabled = true; using var history = TaskResultHistoryService.Initialize(() => enabled);
    for (var i = 0; i < 150; i++) TaskResultHistoryService.Record(TaskResultKind.Backup, TaskResultOutcome.Completed);
    Check(TaskResultHistoryService.Snapshot().Count == 100, "History exceeded cap");
    enabled = false; TaskResultHistoryService.Clear(); TaskResultHistoryService.Record(TaskResultKind.Backup, TaskResultOutcome.Failed);
    Check(TaskResultHistoryService.Snapshot().Count == 0, "Opt-out still recorded task");
    enabled = true;
    var queueItem = new DownloadQueueItem { IsRunning = true }; DownloadQueueService.Instance.Items.Add(queueItem);
    queueItem.Complete(DownloadQueueItemStatus.Completed); queueItem.Complete(DownloadQueueItemStatus.Completed);
    Check(TaskResultHistoryService.Snapshot().Count == 1, "Terminal task recorded twice");
    var waiting = new DownloadQueueItem { IsRunning = false, Status = DownloadQueueItemStatus.Paused };
    DownloadQueueService.Instance.Items.Add(waiting); waiting.ChangeStatus(DownloadQueueItemStatus.Canceled);
    Check(TaskResultHistoryService.Snapshot().Count == 2, "Waiting cancellation was not recorded");
}
finally { Directory.Delete(root, true); TaskResultHistoryService.Clear(); }
Console.WriteLine($"Diagnostic privacy and file safety regression: {checks} checks passed.");
