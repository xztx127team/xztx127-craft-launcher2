namespace XCL2.App.Models;

public sealed class SaveBackupProfile
{
    public string WorldPath { get; set; } = "";
    public string WorldName { get; set; } = "";
    public bool Enabled { get; set; }
    public int IntervalHours { get; set; } = 24;
    public int RetentionCount { get; set; } = 5;
    public string DestinationDirectory { get; set; } = "";
}

public partial class AppConfig
{
    // Separate from existing full-instance/lifecycle backups. No work is scheduled until opted in.
    public Dictionary<string, SaveBackupProfile> SaveBackupProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AllowManualModUpdateNetworkChecks { get; set; } = true;
}
