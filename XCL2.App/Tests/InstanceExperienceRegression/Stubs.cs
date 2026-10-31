namespace XCL2.App.Models
{
    public partial class AppConfig { public bool IsolateVersionsByDefault { get; set; } = true; public Dictionary<string, bool> VersionIsolationOverrides { get; set; } = new(); }
}
namespace XCL2.App.Services
{
    public static class InstanceUxService
    {
        public static string GetGameDirectory(Models.AppConfig cfg, string folder, string id)
            => InstanceIsolationPolicy.Resolve(folder, id, cfg.IsolateVersionsByDefault, cfg.VersionIsolationOverrides) ? Path.Combine(folder, "versions", id) : Path.GetFullPath(folder);
    }
    public sealed class UpdateCandidate
    {
        public string ProjectId { get; set; } = "";
        public string NewVersionId { get; set; } = "";
        public string NewSha1 { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string NewVersionName { get; set; } = "";
    }
}
