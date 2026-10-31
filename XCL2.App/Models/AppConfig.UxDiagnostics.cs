namespace XCL2.App.Models;

public partial class AppConfig
{
    /// <summary>Only bounded current-session operation kind/outcome; no commands, chat or credentials.</summary>
    public bool RememberTaskResults { get; set; } = true;
}

public partial class AppConfig
{
    public List<string> FavoriteServerIds { get; set; } = new();
    public bool EnableRecentServerHistory { get; set; }
    public List<string> RecentServerIds { get; set; } = new();
}
