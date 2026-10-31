namespace XCL2.App { public static class App { public static string DataDir { get; set; } = ""; } }
namespace XCL2.App.Models
{
    public sealed class AppConfig
    {
        public object AiAssistant { get; set; } = new();
        public string? PreLaunchCommand { get; set; }
        public string? CustomJvmArgs { get; set; }
        public string? JavaPath { get; set; }
        public string? SelectedJavaId { get; set; }
        public List<string> InstalledJavas { get; set; } = new();
        public Dictionary<string, int> VersionJavaOverrides { get; set; } = new();
        public Dictionary<string, string> VersionJavaIdOverrides { get; set; } = new();
        public HashSet<string> VersionSkipJavaMismatchPrompt { get; set; } = new();
        public bool AutoStartOnBoot { get; set; }
        public int AutoStartBehavior { get; set; }
        public object TerracottaExecutablePath { get; set; } = new();
        public object TerracottaPublicNodes { get; set; } = new();
        public object MiniWorldClientPaths { get; set; } = new();
        public object MiniWorldCommunityClients { get; set; } = new();
        public object MiniWorldSavedVersions { get; set; } = new();
        public object MiniWorldLocalInstallations { get; set; } = new();
        public object BedrockClients { get; set; } = new();
        public object BedrockServers { get; set; } = new();
        public object BedrockManualInstallDir { get; set; } = new();
        public object DefaultAuthServerApiRoot { get; set; } = new();
        public object SkinApiRoot { get; set; } = new();
        public object DownloadMirrors { get; set; } = new();
        public object Source { get; set; } = new();
        public object IntegrityCheckMode { get; set; } = new();
        public bool EnableInjectionScan { get; set; }
        public object PluginLaunchMode { get; set; } = new();
        public object PluginDownloadProviderId { get; set; } = new();
        public bool EnablePreviewPluginApi { get; set; }
        public bool EnableWorldPluginApi { get; set; }
        public bool NativeBackgroundApprovals { get; set; }
        public bool SettingsAutoSaveWithoutConfirm { get; set; }
        public bool EnableLauncherUpdates { get; set; }
        public bool CheckLauncherUpdatesOnStartup { get; set; }
        public bool EnableUserExperiencePreviewProgram { get; set; }
        public object Folders { get; set; } = new();
        public object SelectedFolderPath { get; set; } = new();
        public object BedrockClientDefaultDownloadDir { get; set; } = new();
        public object BedrockServerDefaultDownloadDir { get; set; } = new();
        public object CustomBackgroundImagePath { get; set; } = new();
        public object CustomBackgroundImageCandidates { get; set; } = new();
        public object AppFontFamily { get; set; } = new();
        public object AppFontFamily_TitleBar { get; set; } = new();
        public object AppFontFamily_Sidebar { get; set; } = new();
        public object AppFontFamily_Content { get; set; } = new();
        public object SaveBackupProfiles { get; set; } = new();
        public object Ux50PickerDirectories { get; set; } = new();
    }
}
namespace XCL2.App.Services
{
    public sealed class InstanceSettings { public string? CustomJvmArgs { get; set; } public string? JavaId { get; set; } }
    public sealed record ProgressInfo(string Stage, long Done, long Total, string CurrentFile);
    public static class ErrorPresenter { public static void LogFallback(string context, Exception? error = null) { } }
    public static class BedrockModService { public static IEnumerable<string> EnumerateGithubSources(string url) { yield return url; } }
    public sealed class ModInstallPlan { }
    public static class ModInstallService
    {
        public static IEnumerable<ModInstallPlan> PrepareBatch(IEnumerable<string> paths, string directory) => throw new NotSupportedException();
        public static void Install(ModInstallPlan plan) => throw new NotSupportedException();
    }
}
namespace XCL2.App.Services.Plugins
{
    public static class PluginHostBridge
    {
        public static Bridge? Current => null;
        public sealed class Bridge { public void NotifyModChange(string path, string kind) { } }
    }
}
