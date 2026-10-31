using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>A settings archive is data, not an authorization to execute commands or grant AI access.</summary>
internal static class ConfigArchivePolicy
{
    internal static void PreserveLocalAuthority(AppConfig incoming, AppConfig current)
    {
        // Keep provider routing together with its grants: attaching existing full
        // disk access to a newly imported endpoint would disclose private files.
        incoming.AiAssistant = current.AiAssistant;
        incoming.PreLaunchCommand = current.PreLaunchCommand;
        incoming.CustomJvmArgs = current.CustomJvmArgs;
        incoming.JavaPath = current.JavaPath;
        incoming.SelectedJavaId = current.SelectedJavaId;
        incoming.InstalledJavas = current.InstalledJavas;
        incoming.VersionJavaOverrides = current.VersionJavaOverrides;
        incoming.VersionJavaIdOverrides = current.VersionJavaIdOverrides;
        incoming.VersionSkipJavaMismatchPrompt = current.VersionSkipJavaMismatchPrompt;
        incoming.AutoStartOnBoot = current.AutoStartOnBoot;
        incoming.AutoStartBehavior = current.AutoStartBehavior;
        incoming.TerracottaExecutablePath = current.TerracottaExecutablePath;
        incoming.TerracottaPublicNodes = current.TerracottaPublicNodes;
        incoming.MiniWorldClientPaths = current.MiniWorldClientPaths;
        incoming.MiniWorldCommunityClients = current.MiniWorldCommunityClients;
        incoming.MiniWorldSavedVersions = current.MiniWorldSavedVersions;
        incoming.MiniWorldLocalInstallations = current.MiniWorldLocalInstallations;
        incoming.BedrockClients = current.BedrockClients;
        incoming.BedrockServers = current.BedrockServers;
        incoming.BedrockManualInstallDir = current.BedrockManualInstallDir;

        // Imported endpoints, mirrors and security switches can redirect future
        // authentication/downloads or bypass a user's existing confirmation policy.
        incoming.DefaultAuthServerApiRoot = current.DefaultAuthServerApiRoot;
        incoming.SkinApiRoot = current.SkinApiRoot;
        incoming.DownloadMirrors = current.DownloadMirrors;
        incoming.Source = current.Source;
        incoming.IntegrityCheckMode = current.IntegrityCheckMode;
        incoming.EnableInjectionScan = current.EnableInjectionScan;
        incoming.PluginLaunchMode = current.PluginLaunchMode;
        incoming.PluginDownloadProviderId = current.PluginDownloadProviderId;
        incoming.EnablePreviewPluginApi = current.EnablePreviewPluginApi;
        incoming.EnableWorldPluginApi = current.EnableWorldPluginApi;
        incoming.NativeBackgroundApprovals = current.NativeBackgroundApprovals;
        incoming.SettingsAutoSaveWithoutConfirm = current.SettingsAutoSaveWithoutConfirm;
        incoming.EnableLauncherUpdates = current.EnableLauncherUpdates;
        incoming.CheckLauncherUpdatesOnStartup = current.CheckLauncherUpdatesOnStartup;
        incoming.EnableUserExperiencePreviewProgram = current.EnableUserExperiencePreviewProgram;

        // A restored path must not silently add a new filesystem/network source.
        incoming.Folders = current.Folders;
        incoming.SelectedFolderPath = current.SelectedFolderPath;
        incoming.BedrockClientDefaultDownloadDir = current.BedrockClientDefaultDownloadDir;
        incoming.BedrockServerDefaultDownloadDir = current.BedrockServerDefaultDownloadDir;
        incoming.CustomBackgroundImagePath = current.CustomBackgroundImagePath;
        incoming.CustomBackgroundImageCandidates = current.CustomBackgroundImageCandidates;
        incoming.AppFontFamily = current.AppFontFamily;
        incoming.AppFontFamily_TitleBar = current.AppFontFamily_TitleBar;
        incoming.AppFontFamily_Sidebar = current.AppFontFamily_Sidebar;
        incoming.AppFontFamily_Content = current.AppFontFamily_Content;
        incoming.SaveBackupProfiles = current.SaveBackupProfiles;
        incoming.Ux50PickerDirectories = current.Ux50PickerDirectories;
    }

    internal static string? ResolveInstanceDirectory(string key, IEnumerable<string> registeredRoots)
    {
        var parts = key.Split('|');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || !Path.IsPathFullyQualified(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]) || parts[1].Contains('/') || parts[1].Contains('\\'))
            throw new InvalidDataException("配置归档包含无效的实例路径。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parts[0]));
        // Compare strings before any Exists/GetAttributes call. Imported folder
        // entries cannot authorize a network probe or a write to an arbitrary root.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!registeredRoots.Any(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Equals(root, comparison))) return null;
        return ArchiveImportSafety.Resolve(Path.Combine(root, "versions"), parts[1]);
    }

    internal static void PreserveInstanceAuthority(InstanceSettings incoming, InstanceSettings? current)
    {
        incoming.CustomJvmArgs = current?.CustomJvmArgs;
        incoming.JavaId = current?.JavaId;
    }
}
