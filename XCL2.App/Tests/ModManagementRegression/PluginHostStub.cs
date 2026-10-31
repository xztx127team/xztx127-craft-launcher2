namespace XCL2.App.Services.Plugins;
public sealed class PluginHostBridge
{
    public static PluginHostBridge? Current => null;
    public void NotifyModChange(string path, string action, bool? enabled = null) { }
}
