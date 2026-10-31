namespace XCL2.App.Models
{
    public enum DownloadSource { Official, BMCLAPI }
    public sealed class AppConfig
    {
        public bool EnableMultiThreadDownload { get; set; } = true;
        public int MaxDownloadThreads { get; set; } = 4;
        public DownloadSource Source { get; set; }
        public DownloadMirrorConfig DownloadMirrors { get; set; } = new();
        public int DownloadSpeedLimitKBps { get; set; }
        public bool ReduceDownloadSpeedWhileGaming { get; set; }
        public int GamingDownloadSpeedLimitKBps { get; set; } = 1024;
    }
}
namespace XCL2.App.Services
{
    internal static class CurseForgeNeoForgeCdnService
    {
        internal static Func<string, CancellationToken, Task<string?>>? Resolver { get; set; }
        internal static Task<string?> TryResolveAsync(string url, CancellationToken ct = default)
            => Resolver?.Invoke(url, ct) ?? Task.FromResult<string?>(null);
    }
    public sealed class ConfigService
    {
        public static ConfigService? Active { get; set; } = new();
        public Models.AppConfig Config { get; } = new();
    }
    public record ProgressInfo(string Stage, int Done, int Total, string CurrentFile) { public string Unit { get; init; } = "项"; }
}
namespace XCL2.App.Services.Plugins
{
    public sealed class PluginFlowCanceledException : Exception { }
    public static class PluginProviderRegistry
    {
        public static Task<IReadOnlyList<string>> AddMirrorAsync(string url, string name, string? sha1, IReadOnlyList<string> urls, CancellationToken ct)
            => Task.FromResult(urls);
    }
    public static class PluginInterceptionRegistry
    {
        internal static Func<string, string>? Interceptor { get; set; }
        public static Task<string> PrepareDownloadAsync(string url, string name, string? sha1, CancellationToken ct)
            => Task.FromResult(Interceptor?.Invoke(url) ?? url);
    }
}
