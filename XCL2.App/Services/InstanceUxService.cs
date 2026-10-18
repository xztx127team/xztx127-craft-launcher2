using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>启动器个人元数据：浏览不会写实例目录，也不会启用历史记录。</summary>
public static class InstanceUxService
{
    public static string Key(string folder, string id) => InstanceDeletionService.BuildKey(folder, id);
    public static InstancePersonalization? Get(AppConfig cfg, string folder, string id)
        => cfg.InstancePersonalizations != null && cfg.InstancePersonalizations.TryGetValue(Key(folder, id), out var value) ? value : null;

    public static void Set(AppConfig cfg, string folder, string id, string notes, IEnumerable<string> tags)
    {
        cfg.InstancePersonalizations ??= new(StringComparer.OrdinalIgnoreCase);
        var old = Get(cfg, folder, id);
        cfg.InstancePersonalizations[Key(folder, id)] = new InstancePersonalization
        {
            Notes = (notes ?? "").Trim()[..Math.Min((notes ?? "").Trim().Length, 4000)],
            Tags = (tags ?? Array.Empty<string>()).Where(t => t != null).Select(t => t.Trim()).Where(t => t.Length > 0).Select(t => t[..Math.Min(t.Length, 30)])
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList(),
            IconFileName = old?.IconFileName
        };
    }

    public static bool RecordSuccessfulLaunch(AppConfig cfg, string folder, string id)
    {
        if (!cfg.EnableRecentInstanceHistory || string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(id)) return false;
        cfg.RecentInstances ??= new();
        var key = Key(folder, id);
        cfg.RecentInstances.RemoveAll(r => r == null || Key(r.FolderPath, r.VersionId) == key);
        cfg.RecentInstances.Insert(0, new RecentInstanceEntry { FolderPath = folder, VersionId = id, LaunchedAtUtc = DateTime.UtcNow });
        if (cfg.RecentInstances.Count > 50) cfg.RecentInstances.RemoveRange(50, cfg.RecentInstances.Count - 50);
        return true;
    }

    public static void ClearRecent(AppConfig cfg) => cfg.RecentInstances = new();

    public static void Rename(AppConfig cfg, string folder, string oldId, string newId)
    {
        if (cfg.InstancePersonalizations != null && cfg.InstancePersonalizations.Remove(Key(folder, oldId), out var metadata))
            cfg.InstancePersonalizations[Key(folder, newId)] = metadata;
        if (cfg.FavoriteInstanceKeys != null)
            for (var i = 0; i < cfg.FavoriteInstanceKeys.Count; i++)
                if (string.Equals(cfg.FavoriteInstanceKeys[i], Key(folder, oldId), StringComparison.OrdinalIgnoreCase)) cfg.FavoriteInstanceKeys[i] = Key(folder, newId);
        foreach (var recent in cfg.RecentInstances ?? new())
            if (recent != null && Key(recent.FolderPath, recent.VersionId) == Key(folder, oldId)) recent.VersionId = newId;
    }

    public static bool IsFavorite(AppConfig cfg, string folder, string id)
        => (cfg.FavoriteInstanceKeys ?? new()).Contains(Key(folder, id), StringComparer.OrdinalIgnoreCase);

    public static void ToggleFavorite(AppConfig cfg, string folder, string id)
    {
        cfg.FavoriteInstanceKeys ??= new();
        var key = Key(folder, id);
        if (IsFavorite(cfg, folder, id)) cfg.FavoriteInstanceKeys.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        else cfg.FavoriteInstanceKeys.Add(key);
    }

    public static string GetVersionDirectory(string folder, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0 || Path.IsPathRooted(id))
            throw new InvalidOperationException("实例名称不是有效的单级目录名称。");
        var versions = Path.GetFullPath(Path.Combine(folder, "versions"));
        var path = Path.GetFullPath(Path.Combine(versions, id));
        if (!string.Equals(Path.GetDirectoryName(path), versions, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("实例路径超出 versions 目录。");
        return path;
    }

    public static string GetGameDirectory(AppConfig cfg, string folder, string id)
    {
        // 与启动入口使用同一组隔离覆盖；仅读取，不创建目录。
        var isolated = cfg.VersionIsolationOverrides.TryGetValue(id, out var choice) ? choice : cfg.IsolateVersionsByDefault;
        return isolated ? GetVersionDirectory(folder, id) : Path.GetFullPath(folder);
    }

    public static string? GetIconPath(AppConfig cfg, string folder, string id)
    {
        var file = Get(cfg, folder, id)?.IconFileName;
        if (string.IsNullOrWhiteSpace(file) || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || file != Path.GetFileName(file) || file.Contains('\\') || file.Contains('/') ||
            !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
        var path = Path.Combine(App.DataDir, "instance_icons", file);
        return File.Exists(path) ? path : null;
    }

    public static string CreateIconPath(string folder, string id)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key(folder, id))))[..24] + "-" + Guid.NewGuid().ToString("N") + ".png";
        var directory = Path.Combine(App.DataDir, "instance_icons");
        Directory.CreateDirectory(directory); // Only reached after explicit icon save.
        return Path.Combine(directory, name);
    }

    public static void SetIconFile(AppConfig cfg, string folder, string id, string? fileName)
    {
        cfg.InstancePersonalizations ??= new(StringComparer.OrdinalIgnoreCase);
        var value = Get(cfg, folder, id) ?? new InstancePersonalization();
        value.IconFileName = fileName;
        cfg.InstancePersonalizations[Key(folder, id)] = value;
    }

    /// <summary>显式创建文件夹快捷方式；不覆盖同名桌面文件，不执行 shell 指令。</summary>
    public static string CreateFolderDesktopShortcut(string folder, string id)
    {
        var versionDir = GetVersionDirectory(folder, id);
        if (!Directory.Exists(versionDir)) throw new DirectoryNotFoundException("实例文件夹不存在。");
        if (versionDir.Contains('"')) throw new InvalidOperationException("目录包含无效字符。");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) throw new DirectoryNotFoundException("未找到桌面目录。");
        var name = string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd(' ', '.');
        if (name.Length > 80) name = name[..80];
        if (name.Length == 0) name = "Minecraft 实例";
        string path = Path.Combine(desktop, name + " 文件夹.lnk");
        for (var suffix = 2; File.Exists(path) && suffix < 1000; suffix++) path = Path.Combine(desktop, $"{name} 文件夹 ({suffix}).lnk");
        if (File.Exists(path)) throw new IOException("桌面上已有过多同名快捷方式。");
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("当前系统没有快捷方式组件。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(path);
            try
            {
                shortcut.TargetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                shortcut.Arguments = "\"" + versionDir + "\"";
                shortcut.Description = "打开 Minecraft 实例文件夹：" + id;
                shortcut.WorkingDirectory = versionDir;
                shortcut.Save();
            }
            finally { Marshal.FinalReleaseComObject(shortcut); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return path;
    }
}
