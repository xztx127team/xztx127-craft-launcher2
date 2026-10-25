namespace XCL2.App.Models
{
    public partial class AppConfig
    {
        public List<string> HiddenInstanceKeys { get; set; } = new();
        public List<string> FavoriteVersionIds { get; set; } = new();
        public List<FavoriteItem> FavoriteItems { get; set; } = new();
        public Dictionary<string, bool> VersionIsolationOverrides { get; set; } = new();
        public Dictionary<string, bool> VersionResourcePackIsolationOverrides { get; set; } = new();
        public Dictionary<string, int> VersionJavaOverrides { get; set; } = new();
        public Dictionary<string, string> VersionJavaIdOverrides { get; set; } = new();
        public Dictionary<string, string> VersionAutoJoinServer { get; set; } = new();
        public HashSet<string> VersionSkipJavaMismatchPrompt { get; set; } = new();
        public string? SelectedFolderPath { get; set; }
        public string? SelectedVersionId { get; set; }
    }
    public enum FavoriteItemType { Version, Mod }
    public sealed class FavoriteItem { public FavoriteItemType Type { get; set; } public string SourceId { get; set; } = ""; }
}
namespace XCL2.App.Services.Plugins
{
    internal sealed class PluginHostBridge
    {
        internal static PluginHostBridge? Current => null;
        internal void NotifyInstanceRenamed(string folder, string oldId, string newId) { }
        internal void NotifyInstanceRemoved(string folder, string id) { }
    }
}
