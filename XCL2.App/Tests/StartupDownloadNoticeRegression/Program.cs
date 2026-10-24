using System.Text.Json;
using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}
AppConfig Restart(AppConfig config) => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;

var config = new AppConfig();
var startup = new StartupDownloadNoticePolicy();
Check(startup.TryBegin("zh-Hans", config), "Simplified Chinese startup displays the notice.");
Check(!startup.TryBegin("zh-Hans", config), "Reentrant or repeated startup hooks cannot show it twice.");
var writes = 0;
StartupDownloadNoticePolicy.ApplyChoice(config, StartupDownloadNoticeChoice.ThisStartup, () => writes++);
Check(writes == 0 && config.SuppressedStartupAnnouncementIds.Count == 0,
    "Acknowledgement only dismisses this startup and never persists suppression.");
Check(new StartupDownloadNoticePolicy().TryBegin("zh-Hans", Restart(config)),
    "Acknowledgement is shown again after a serialized restart.");

foreach (var language in new string?[] { "zh-Hant", "zh-Hans-MS", "zh-CN", "en-US", "zh-hans", " zh-Hans ", "", null })
    Check(!new StartupDownloadNoticePolicy().TryBegin(language, config), "The language gate must match zh-Hans exactly: " + language);
var otherLanguageStartup = new StartupDownloadNoticePolicy();
Check(!otherLanguageStartup.TryBegin("en-US", config) && !otherLanguageStartup.TryBegin("zh-Hans", config),
    "Changing language after the startup check does not insert a late startup announcement.");

config.SeenAnnouncementIds.Add(StartupDownloadNoticePolicy.AnnouncementId);
config.SuppressedStartupAnnouncementIds.Add("unrelated-notice");
Check(new StartupDownloadNoticePolicy().TryBegin("zh-Hans", config),
    "Existing read markers and opt-outs for other notices do not suppress this notice.");
string? saved = null;
StartupDownloadNoticePolicy.ApplyChoice(config, StartupDownloadNoticeChoice.Suppress,
    () => { saved = JsonSerializer.Serialize(config); writes++; });
var restarted = JsonSerializer.Deserialize<AppConfig>(saved!)!;
Check(writes == 1 && !new StartupDownloadNoticePolicy().TryBegin("zh-Hans", restarted),
    "Explicit opt-out is persisted and survives restart.");
Check(restarted.SuppressedStartupAnnouncementIds.Contains("unrelated-notice")
    && restarted.SeenAnnouncementIds.SequenceEqual(config.SeenAnnouncementIds),
    "Opt-out preserves unrelated suppression and ordinary read markers.");
StartupDownloadNoticePolicy.ApplyChoice(config, StartupDownloadNoticeChoice.Suppress, () => { });
Check(config.SuppressedStartupAnnouncementIds.Count(id => id == StartupDownloadNoticePolicy.AnnouncementId) == 1,
    "Repeated opt-out does not duplicate the fixed notice ID.");

var failureConfig = new AppConfig();
failureConfig.SuppressedStartupAnnouncementIds.Add("keep-this-id");
var originalList = failureConfig.SuppressedStartupAnnouncementIds;
try
{
    StartupDownloadNoticePolicy.ApplyChoice(failureConfig, StartupDownloadNoticeChoice.Suppress,
        () => throw new IOException("simulated configuration write failure"));
    throw new InvalidOperationException("A persistence failure must reach the dialog.");
}
catch (IOException) { }
Check(ReferenceEquals(failureConfig.SuppressedStartupAnnouncementIds, originalList)
    && originalList.SequenceEqual(new[] { "keep-this-id" }),
    "A failed write rolls back the opt-out without losing unrelated preferences.");
Check(new StartupDownloadNoticePolicy().TryBegin("zh-Hans", Restart(failureConfig)),
    "Failed persistence must not silently stop reminders on restart.");
StartupDownloadNoticePolicy.ApplyChoice(failureConfig, StartupDownloadNoticeChoice.Suppress, () => { });
Check(!new StartupDownloadNoticePolicy().TryBegin("zh-Hans", failureConfig),
    "The user can retry and save after a previous failure.");

var nullListConfig = JsonSerializer.Deserialize<AppConfig>("{\"SuppressedStartupAnnouncementIds\":null}")!;
Check(new StartupDownloadNoticePolicy().TryBegin("zh-Hans", nullListConfig), "A null list in an older or edited config does not suppress the notice.");
StartupDownloadNoticePolicy.ApplyChoice(nullListConfig, StartupDownloadNoticeChoice.Suppress, () => { });
Check(nullListConfig.SuppressedStartupAnnouncementIds.SequenceEqual(new[] { StartupDownloadNoticePolicy.AnnouncementId }),
    "Opt-out repairs a null suppression list.");
Check(new StartupDownloadNoticePolicy().TryBegin("zh-Hans", new AppConfig()),
    "Closing by Esc or background without applying a choice cannot persist suppression.");
Console.WriteLine($"Startup notice policy: {checks} checks passed.");

namespace XCL2.App.Models
{
    // The production partial is linked above; this unrelated field checks independence from ordinary announcements.
    public partial class AppConfig
    {
        public List<string> SeenAnnouncementIds { get; set; } = new();
    }
}
