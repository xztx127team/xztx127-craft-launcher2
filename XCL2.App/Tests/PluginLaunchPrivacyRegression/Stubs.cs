using System.Diagnostics;
using XCL2.App.Models;

// Only unrelated process, GPU, plugin and UI boundaries are stubbed. BuildArguments itself is production code.
namespace XCL2.App { internal static class App { public static string DataDir => Path.GetTempPath(); } }
namespace XCL2.App.Services
{
    internal static class FolderService
    {
        internal static string? ResolveVersionJson(string directory, string id)
        {
            var exact = Path.Combine(directory, id + ".json");
            if (File.Exists(exact)) return exact;
            var matches = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json") : Array.Empty<string>();
            return matches.Length == 1 ? matches[0] : null;
        }
    }
    internal static class ErrorPresenter
    { public static void LogFallback(string message, Exception error) { } public static void LogTechnicalDetail(string message) { } }
    internal static class HighPerformanceGpuService
    { public static void ApplyForLaunch(ProcessStartInfo start, string java, bool enabled) { } }
    internal static class TouchVersionPolicy { public static bool? Resolve(string directory, string version) => null; }
    internal static class GlfwTouchLaunchCompatibility { public static void RestorePreviousOverride(string directory) { } }
    internal sealed class LegacyTouchOptions : IDisposable
    { public static LegacyTouchOptions Acquire(string directory) => new(); public void Attach(Process process) { } public void Dispose() { } }
    internal static class LauncherLogService { public static void AppendLine(string message) { } }
    internal static class InstanceConfigService
    {
        public const string GraphicsApiGame = "game", GraphicsApiOpenGl = "opengl", GraphicsApiVulkan = "vulkan";
        public sealed class InstanceSettings { public bool OptimizeMemoryBeforeLaunch { get; set; } }
        public static InstanceSettings? TryLoad(string directory) => null;
        public static string GetLaunchScriptPath(string directory) => Path.Combine(directory, "xcl", "launch.bat");
    }
    internal static class WorkingSetOptimizationService
    {
        public sealed record Result(string Summary);
        public static Result Run() => new("No processes changed in launch argument tests.");
    }
    internal static class VersionInfoResolver { public static string ExtractAnyVersion(string version) => version; }
    public partial class LauncherService
    {
        private static void DemandPreparedLaunch(LaunchOptions options) { }
        private static List<string> ApplyPluginLaunchPlan(LaunchOptions options, List<string> args) => args;
    }
}
namespace XCL2.App.Services.Plugins
{
    internal sealed class PluginChain<T> { public T Plan { get; set; } = default!; }
    internal sealed class PluginLaunchPlan { public Dictionary<string, string> EnvironmentVariables { get; } = new(); }
    internal static partial class PluginRuntimeRegistry { private static void QueueRenderersChanged() { } }
    public sealed class PluginContext
    {
        public string PluginId { get; } = Guid.NewGuid().ToString("N");
        public PluginPermissionService Permissions { get; } = new();
        public PluginMetadata Metadata { get; } = new();
        public CancellationToken Stopping => CancellationToken.None;
        public Action<string> Log { get; } = _ => { };
    }
    public sealed class PluginMetadata { public bool IsLegacy => true; }
    public sealed class PluginPermissionService { public void Demand(string permission) { } }
    public static class PluginPermissionNames { public const string UiComponents = "ui.components"; }
    public sealed class PluginOperationException(string code, string message) : Exception(message) { }
    public sealed class PluginRegistration(Action remove) : IDisposable { public void Dispose() => remove(); }
    public delegate object PluginSkinModelRendererFactory(object request);
    internal sealed class PluginHostBridge
    { public static PluginHostBridge? Current => null; public void WatchGame(GameProcessInfo info, string directory) { } }
}
