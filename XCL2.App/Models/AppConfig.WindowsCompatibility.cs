namespace XCL2.App.Models;

/// <summary>Persist explicit opt-in/opt-out separately from OS-dependent automatic behavior.</summary>
public enum WindowsCompatibilityMode
{
    Auto = 0,
    Enabled = 1,
    Disabled = 2
}

public partial class AppConfig
{
    public WindowsCompatibilityMode WindowsCompatibilityMode { get; set; } = WindowsCompatibilityMode.Auto;
}
