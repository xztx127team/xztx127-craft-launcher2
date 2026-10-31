using System.IO;
using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>Display-only helpers. Never change account, launch arguments, instance contents or isolation.</summary>
public static class InstanceExperienceService
{
    public static string EnvironmentSummary(GameVersion version, bool isolated, int enabledMods, int disabledMods)
    {
        // Only allow known environment fields. User-chosen names, roots, JVM flags and account data are excluded.
        static string Safe(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 80 &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+' or ' ') ? value : "未识别";
        var text = new StringBuilder("Minecraft 环境摘要\n");
        text.AppendLine("Minecraft：" + Safe(version.McVersion));
        text.AppendLine("加载器：" + (string.IsNullOrWhiteSpace(version.ModLoader) ? "原版" : Safe(version.ModLoader)));
        if (!string.IsNullOrWhiteSpace(version.ModLoader)) text.AppendLine("加载器版本：" + Safe(version.ModLoaderVersion));
        text.AppendLine("游戏目录：" + (isolated ? "实例隔离" : "共享根目录"));
        text.AppendLine($"模组：启用 {Math.Max(0, enabledMods)} / 禁用 {Math.Max(0, disabledMods)}");
        return text.ToString().TrimEnd();
    }

    public static bool SharesGameDirectory(AppConfig cfg, string folder, GameVersion target, IEnumerable<GameVersion> versions)
    {
        var targetDirectory = InstanceUxService.GetGameDirectory(cfg, folder, target.Id);
        return versions.Any(other => !string.Equals(other.Id, target.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(InstanceUxService.GetGameDirectory(cfg, folder, other.Id), targetDirectory, StringComparison.OrdinalIgnoreCase));
    }

    public static string FolderLabel(GameFolder folder, IEnumerable<GameFolder> all, bool disambiguate)
    {
        if (!disambiguate || all.Count(f => string.Equals(f.Name, folder.Name, StringComparison.OrdinalIgnoreCase)) < 2) return folder.Name;
        var leaf = Path.GetFileName(folder.Path.TrimEnd('/', '\\'));
        var peers = all.Where(f => string.Equals(f.Name, folder.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Identical final directory names need the parent name as well; full path remains available in the tooltip.
        if (peers.Count(f => string.Equals(Path.GetFileName(f.Path.TrimEnd('/', '\\')), leaf, StringComparison.OrdinalIgnoreCase)) > 1)
            leaf = Path.GetFileName(Path.GetDirectoryName(folder.Path.TrimEnd('/', '\\'))) + Path.DirectorySeparatorChar + leaf;
        if (peers.Count(f => f.Path.EndsWith(leaf, StringComparison.OrdinalIgnoreCase)) > 1) leaf = folder.Path;
        return folder.Name + " · " + leaf;
    }

    public static string VersionPickerLabel(GameVersion version, IEnumerable<GameVersion> choices, IEnumerable<GameFolder> folders, bool disambiguate)
    {
        if (!disambiguate || choices.Count(other => string.Equals(other.Id, version.Id, StringComparison.OrdinalIgnoreCase)) < 2) return version.Id;
        var root = Path.GetDirectoryName(Path.GetDirectoryName(version.FolderPath));
        var configured = folders.FirstOrDefault(folder => string.Equals(folder.Path.TrimEnd('/', '\\'), root?.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase));
        return version.Id + " · " + (configured == null ? root ?? "未知文件夹" : FolderLabel(configured, folders, true));
    }
}
