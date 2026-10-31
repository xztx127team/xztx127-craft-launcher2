namespace XCL2.App.Models
{
    public enum DownloadSource { Official, BMCLAPI }
    public sealed partial class AppConfig
    {
        public DownloadSource Source { get; set; }
        public DownloadMirrorConfig DownloadMirrors { get; set; } = new();
        public int DownloadSpeedLimitKBps { get; set; }
        public bool ReduceDownloadSpeedWhileGaming { get; set; }
        public int GamingDownloadSpeedLimitKBps { get; set; } = 1024;
        public bool EnableMultiThreadDownload { get; set; }
        public int MaxDownloadThreads { get; set; }
        public bool SmartBandwidthThrottle { get; set; }
    }
}
namespace XCL2.App.Services
{
    internal static class CurseForgeNeoForgeCdnService
    {
        internal static Task<string?> TryResolveAsync(string url, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }
    public sealed class ConfigService
    {
        public static ConfigService? Active { get; set; } = new();
        public Models.AppConfig Config { get; } = new();
    }
    public sealed class SmartBandwidthMonitor(DownloadRateLimiter limiter, int limit) : IDisposable
    { public void ReportSelfBytes(int bytes) { } public void Dispose() { } }
    public static class ErrorPresenter { public static void LogFallback(string text, Exception error) { } }
    public static class Loc { public static string T(string key, string fallback) => fallback; }
}
namespace XCL2.App.Services.Plugins
{
    public sealed class PluginFlowCanceledException : Exception { }
    public sealed class Manifest
    {
        public string LatestRelease { get; set; } = "";
        public string LatestSnapshot { get; set; } = "";
        public List<Models.VersionManifestEntry> Versions { get; set; } = new();
    }
    public static class PluginProviderRegistry
    {
        public static Task<Manifest?> GetSelectedManifestAsync(CancellationToken ct) => Task.FromResult<Manifest?>(null);
        public static Task<IReadOnlyList<string>> AddMirrorAsync(string url, string name, string? sha1, IReadOnlyList<string> urls, CancellationToken ct) => Task.FromResult(urls);
    }
    public static class PluginInterceptionRegistry
    {
        public static Task<string> PrepareDownloadAsync(string url, string name, string? sha1, CancellationToken ct) => Task.FromResult(url);
        public static Task ValidateInstanceAsync(string folder, string version, string kind, CancellationToken ct) => Task.CompletedTask;
    }
    public sealed class PluginHostBridge
    {
        public static PluginHostBridge? Current => null;
        public void NotifyInstanceCreated(string folder, string version) { }
    }
}
