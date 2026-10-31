using System.Text.Json;
using XCL2.App.Models;

// Only UI/configuration and unexercised non-log branches are replaced. The protocol,
// read_log dispatch, consent/revocation decisions, log buffer and sanitizer are production code.
namespace XCL2.App { public static class App { public static string DataDir => Path.GetTempPath(); } }
namespace XCL2.App.Models
{
    public enum DownloadSource { Official, BMCLAPI }
    public sealed class AppConfig
    {
        public AiAssistantConfig AiAssistant { get; } = new();
        public int MinMemoryMb { get; set; } = 512;
        public int MaxMemoryMb { get; set; } = 4096;
        public DownloadSource Source { get; set; }
        public bool EnableMultiThreadDownload { get; set; }
        public int MaxDownloadThreads { get; set; }
        public int DownloadSpeedLimitKBps { get; set; }
        public bool SmartBandwidthThrottle { get; set; }
        public bool EnableInjectionScan { get; set; }
        public bool EnablePageAnimations { get; set; }
        public bool SettingsAutoSaveWithoutConfirm { get; set; }
        public int MouseWheelSensitivityPercent { get; set; }
        public int TextOpacityPercent { get; set; }
        public bool EnforceJavaVersionMatch { get; set; }
        public string GameLanguage { get; set; } = "";
        public string LauncherLanguage { get; set; } = "";
        public bool GuestModeEnabled { get; set; }
        public bool ExperimentalFeaturesUnlocked { get; set; }
        public string LastLaunchCountDate { get; set; } = "";
        public int TodayLauncherStartCount { get; set; }
    }
}
namespace XCL2.App.Services
{
    public sealed class ConfigService
    {
        public static ConfigService? Active { get; set; }
        public AppConfig Config { get; } = new();
        public string ConfigPath => Path.Combine(App.DataDir, "config.json");
        public string MirrorConfigPath => ConfigPath;
        public string LegacyMirrorConfigPath => ConfigPath;
        public string LegacyConfigPath => ConfigPath;
        public void Save() { }
    }
    public static class ErrorPresenter { public static void LogTechnicalDetail(string text) => throw new InvalidOperationException(text); }
    public static class ScrollWheelBehavior { public static void SetSensitivityPercent(int value) => throw new NotSupportedException(); }
    public static class ThemeService { public static void ApplyTextOpacity(int value) => throw new NotSupportedException(); }
    public static class AiAttachmentService
    {
        public static bool IsManagedAttachmentPath(string path) => false;
        public static bool IsReadOnlyAttachmentPath(string path) => false;
        public static IReadOnlyList<string> GetReadOnlyRoots() => Array.Empty<string>();
        public static IDisposable BeginReadScope(IReadOnlyList<string> roots) => new EmptyScope();
        private sealed class EmptyScope : IDisposable { public void Dispose() { } }
    }
    public static partial class AiFileAccessService
    {
        private static string? OptionalString(JsonElement value, string name) =>
            value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        public static Task<(string DisplayText, string? FollowUpForModel)> ProcessReplyAsync(
            string reply, ConfirmCallback confirm, AiWorkModeContext? workMode = null) => throw new NotSupportedException();
    }
}
namespace XCL2.App.Services.Workbench
{
    public static class ProjectFiles
    {
        public static object ReadPage(string root, string file, int start, int count) => throw new NotSupportedException();
    }
}
namespace System.Windows
{
    public sealed class Application
    {
        public static Application? Current => null;
        public Threading.Dispatcher Dispatcher => throw new NotSupportedException();
    }
}
namespace System.Windows.Threading
{
    public sealed class DispatcherFrame { public bool Continue { get; set; } }
    public sealed class Dispatcher
    {
        public bool CheckAccess() => false;
        public void BeginInvoke(Action callback) => throw new NotSupportedException();
        public static void PushFrame(DispatcherFrame frame) => throw new NotSupportedException();
    }
}
