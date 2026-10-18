namespace XCL2.App.Models { public sealed class AppConfig { public List<GameFolder> Folders { get; set; } = new(); }
 public sealed class GameFolder { public string Name { get; set; } = ""; public string Path { get; set; } = ""; }
 public sealed class GameVersion { public string Id { get; set; } = ""; public string McVersion { get; set; } = ""; public string FolderPath { get; set; } = ""; public string? ModLoader { get; set; } public string? ModLoaderVersion { get; set; } public bool IsInstalled { get; set; } }
 public enum IntegrityCheckMode { Simple, Strict } public enum ServerCoreType { Forge, NeoForge } }
namespace XCL2.App.Services
{
    public sealed record ProgressInfo(string Stage, int Done, int Total, string CurrentFile);
    public static class Loc { public static string T(string key, string fallback) => fallback; }
    public static class VersionInfoResolver
    {
        public sealed record Info(string McVersion = "1.20.1", string? ModLoader = null, string? ModLoaderVersion = null);
        public static Info Resolve(string path, string id, object? cfg) => new();
        public static bool LooksLikeMcVersion(string? value) => value?.StartsWith("1.", StringComparison.Ordinal) == true;
    }
    public sealed class LauncherService { public List<string> CheckMissingLibraries(string root, string id, string? architecture = null) => new(); public static string? ResolveVersionFile(string dir, string id, string extension) { var path = System.IO.Path.Combine(dir, id + "." + extension); return System.IO.File.Exists(path) ? path : null; } }
    public sealed class CurseForgeKeyService { public string? TryGetKey() => null; }
    public static class DownloadEndpoints
    {
        public static bool PreferMirror => false;
        public static readonly List<string> Written = new();
        public static Task<string> DownloadFileWithFallbackAsync(System.Net.Http.HttpClient http, string url, bool mirror,
            string destination, string message, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default,
            string? expectedSha1 = null, string? expectedSha512 = null, string? expectedMd5 = null, long? expectedSize = null)
            => DownloadFilesWithFallbackAsync(http, new[] { url }, mirror, destination, message, progress, ct, expectedSha1, expectedSha512, expectedMd5, expectedSize);
        public static Task<string> DownloadFilesWithFallbackAsync(System.Net.Http.HttpClient http, IEnumerable<string> urls, bool mirror,
            string destination, string message, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default,
            string? expectedSha1 = null, string? expectedSha512 = null, string? expectedMd5 = null, long? expectedSize = null)
        {
            ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, "fixture"); Written.Add(destination); return Task.FromResult(destination);
        }
    }
    public sealed class DownloadService : IDisposable
    {
        public sealed record Entry(string Id);
        public sealed class Manifest { public List<Entry> Versions { get; } = new() { new("1.20.1") }; }
        public static int Installed;
        public static DownloadService CreateFromConfig(XCL2.App.Models.AppConfig config) => new();
        public Task<Manifest> GetVersionManifestAsync(CancellationToken ct) => Task.FromResult(new Manifest());
        public Task InstallVersionAsync(string root, Entry entry, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Installed++;
            var dir = Path.Combine(root, "versions", entry.Id); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, entry.Id + ".json"), "{\"id\":\"" + entry.Id + "\",\"mainClass\":\"fixture.Main\"}");
            File.WriteAllText(Path.Combine(dir, entry.Id + ".jar"), "fixture"); return Task.CompletedTask;
        }
        public void Dispose() { }
    }
    public sealed class ClientLoaderInstallService : IDisposable
    {
        public sealed record Build(bool IsRecommended = true, string DisplayVersion = "0.16");
        public static int Installed;
        public ClientLoaderInstallService(XCL2.App.Models.AppConfig config) { }
        public Task<List<Build>> GetFabricLoaderVersionsAsync(string? version, CancellationToken ct) => Task.FromResult(new List<Build> { new() });
        public Task<List<Build>> GetQuiltLoaderVersionsAsync(string? version, CancellationToken ct) => Task.FromResult(new List<Build> { new() });
        private static Task<string> Install(string root, string mc)
        {
            Installed++; var id = "fabric-loader-fixture-" + mc; var dir = Path.Combine(root, "versions", id); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".json"), "{\"id\":\"" + id + "\",\"mainClass\":\"fixture.Loader\",\"inheritsFrom\":\"" + mc + "\"}");
            File.WriteAllText(Path.Combine(dir, "installer-notes.json"), "{\"fixture\":true}"); return Task.FromResult(id);
        }
        public Task<string> InstallFabricClientAsync(string root, string mc, string version, IProgress<ProgressInfo>? progress, CancellationToken ct) => Install(root, mc);
        public Task<string> InstallQuiltClientAsync(string root, string mc, string version, IProgress<ProgressInfo>? progress, CancellationToken ct) => Install(root, mc);
        public Task<string> InstallForgeOrNeoForgeClientAsync(string root, XCL2.App.Models.ServerCoreType kind, string version, string java, IProgress<ProgressInfo>? progress, CancellationToken ct) => Install(root, "1.20.1");
        public void Dispose() { }
    }
}
namespace XCL2.App.Services.Plugins
{
    public sealed class PluginOperationException : Exception { public PluginOperationException(string code, string message) : base(message) { } }
    public sealed class PluginFlowCanceledException : Exception { }
    public static class PluginInterceptionRegistry
    { public static Action<string, string>? BeforeValidate; public static bool HasInstanceInterceptors => false; public static Task ValidateInstanceAsync(string root, string id, string kind, CancellationToken ct) { BeforeValidate?.Invoke(root, id); return Task.CompletedTask; } }
    public static class PluginHostBridge
    {
        public sealed class Bridge { public void NotifyInstanceCreated(string root, string id) { } }
        public static Bridge? Current => null;
    }
}
