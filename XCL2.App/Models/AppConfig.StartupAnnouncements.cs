namespace XCL2.App.Models;

public partial class AppConfig
{
    /// <summary>Only announcements explicitly dismissed with “以后不再提示”; independent of ordinary read announcements.</summary>
    public List<string> SuppressedStartupAnnouncementIds { get; set; } = new();
}
