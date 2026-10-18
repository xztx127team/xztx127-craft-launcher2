namespace XCL2.App.Models;

public enum LauncherTimeFormat { System, TwelveHour, TwentyFourHour }
public enum LauncherCapacityUnit { Automatic, MB, GB }

public sealed class LauncherWindowPlacement
{
    public string Monitor { get; set; } = "";
    public double HorizontalPosition { get; set; } = 0.5;
    public double VerticalPosition { get; set; } = 0.5;
    public double Width { get; set; } = 1000;
    public double Height { get; set; } = 640;
    public bool Maximized { get; set; }
}

public partial class AppConfig
{
    public bool EnableRecentToolHistory { get; set; }
    public List<int> FavoriteToolTabs { get; set; } = new();
    public List<int> RecentToolTabs { get; set; } = new();

    public bool FollowSystemReducedMotion { get; set; } = true;
    public bool HideServerAddresses { get; set; }
    public bool RememberWindowPlacement { get; set; } = true;
    public LauncherWindowPlacement? LauncherWindowPlacement { get; set; }
    public LauncherTimeFormat PreferredTimeFormat { get; set; } = LauncherTimeFormat.System;
    public LauncherCapacityUnit PreferredCapacityUnit { get; set; } = LauncherCapacityUnit.Automatic;
    public bool ShowExactCapacityValues { get; set; }
}
