using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
const string request = "Please inspect the log.\n<<XCL2_ACCESS:{\"action\":\"read_log\"}>>";
var config = new ConfigService();
ConfigService.Active = config;
config.Config.AiAssistant.WorkMode = AiWorkMode.Build;
LauncherLogService.AppendLine("[HTTP] Authorization: Bearer tool_bearer_fixture");
LauncherLogService.AppendLine("Cookie: first=tool_cookie_fixture; second=tool_second_cookie_fixture");
LauncherLogService.AppendLine("password=tool_password_fixture");
LauncherLogService.AppendLine("mail tool_user_fixture@example.org");
LauncherLogService.AppendLine(@"C:\Users\ToolPrivateUser\game");
LauncherLogService.AppendLine("Setting user: ToolPrivatePlayer");
LauncherLogService.AppendLine("--session token:tool_session_fixture:uuid");
LauncherLogService.AppendLine("Caused by: example.ToolDiagnostic");
var consentCount = 0;
var approved = AiFileAccessService.ProcessReply(request, (_, _) => { consentCount++; return true; });
Check(consentCount == 1, "Log access must still obtain concrete consent.");
Check(approved.FollowUpForModel?.Contains("example.ToolDiagnostic") == true, "Approved tool lost diagnostic evidence.");
foreach (var secret in new[] { "tool_bearer_fixture", "tool_cookie_fixture", "tool_second_cookie_fixture", "tool_password_fixture", "tool_user_fixture@example.org", "ToolPrivateUser", "ToolPrivatePlayer", "tool_session_fixture" })
    Check(!approved.FollowUpForModel!.Contains(secret), "The actual read_log tool returned private data: " + secret);

var denied = AiFileAccessService.ProcessReply(request, (_, _) => false);
Check(denied.FollowUpForModel?.Contains("example.ToolDiagnostic") != true, "Declined log access returned the log.");
foreach (var permission in new[] { "Read", "Post" })
{
    config.Config.AiAssistant.CommandPermissions.Read = true;
    config.Config.AiAssistant.CommandPermissions.Post = true;
    var revoked = AiFileAccessService.ProcessReply(request, (_, _) =>
    {
        if (permission == "Read") config.Config.AiAssistant.CommandPermissions.Read = false;
        else config.Config.AiAssistant.CommandPermissions.Post = false;
        return true;
    });
    Check(revoked.FollowUpForModel?.Contains("example.ToolDiagnostic") != true,
        "Permission revoked during consent still returned the log: " + permission);
}
config.Config.AiAssistant.CommandPermissions.Read = true;
config.Config.AiAssistant.CommandPermissions.Post = true;
LauncherLogService.AppendLine("password=" + string.Concat(Enumerable.Repeat("CUT_SECRET_FIXTURE", 1000)));
LauncherLogService.AppendLine("FINAL_TOOL_DIAGNOSTIC");
var bounded = AiFileAccessService.ProcessReply(request, (_, _) => true).FollowUpForModel!;
Check(!bounded.Contains("CUT_SECRET_FIXTURE"), "The actual read_log tool returned an unlabelled token suffix.");
Check(bounded.Contains("FINAL_TOOL_DIAGNOSTIC"), "Bounded tool output lost its complete final diagnostic line.");
foreach (var path in new[] { @"\\attacker.invalid\share\file.txt", @"\\?\UNC\attacker.invalid\share\file.txt", @"\\.\C:\file.txt" })
{
    var write = "<<XCL2_ACCESS:" + System.Text.Json.JsonSerializer.Serialize(new
        { action = "write_workspace_file", path, content = "must not write" }) + ">>";
    var confirms = 0;
    var refused = AiFileAccessService.ProcessReply(write, (_, _) => { confirms++; return true; });
    Check(confirms == 0 && refused.FollowUpForModel != null,
        "An unapproved network/device path reached consent or execution.");
}
Console.WriteLine($"AI log tool privacy regression: {checks} checks passed.");
