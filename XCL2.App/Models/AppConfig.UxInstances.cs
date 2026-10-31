namespace XCL2.App.Models;

public partial class AppConfig
{
    /// <summary>仅用户开启后记录成功启动；默认不记录最近实例。</summary>
    public bool EnableRecentInstanceHistory { get; set; }
    /// <summary>仅本次启动器会话中恢复实例页筛选与滚动位置。</summary>
    public bool RememberInstanceBrowsePosition { get; set; } = true;
    public Dictionary<string, InstancePersonalization> InstancePersonalizations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> FavoriteInstanceKeys { get; set; } = new();
    public List<RecentInstanceEntry> RecentInstances { get; set; } = new();
}

public sealed class InstancePersonalization
{
    public string Notes { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    /// <summary>启动器专用图标的文件名；不接受任意路径。</summary>
    public string? IconFileName { get; set; }
}

public sealed class RecentInstanceEntry
{
    public string FolderPath { get; set; } = "";
    public string VersionId { get; set; } = "";
    public DateTime LaunchedAtUtc { get; set; }
}
