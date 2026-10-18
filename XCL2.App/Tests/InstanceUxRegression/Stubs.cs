namespace XCL2.App { public static class App { public static string DataDir { get; set; } = ""; } }
namespace XCL2.App.Models
{
    public partial class AppConfig
    {
        public List<FavoriteItem> FavoriteItems { get; set; } = new();
        public List<string> FavoriteVersionIds { get; set; } = new();
        public Dictionary<string, bool> VersionIsolationOverrides { get; set; } = new();
        public bool IsolateVersionsByDefault { get; set; } = true;
    }
    public enum FavoriteItemType { Version, Mod }
    public enum ModSource { Combined, Modrinth }
    public sealed class FavoriteItem { public FavoriteItemType Type { get; set; } public ModSource Source { get; set; } public string SourceId { get; set; } = ""; }
}
namespace XCL2.App.Services
{
    public static class InstanceDeletionService
    {
        public static string BuildKey(string folder, string id) => folder.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant() + "|" + id.ToLowerInvariant();
    }
}
