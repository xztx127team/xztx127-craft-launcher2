using System.IO;
using System.Text.Json;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>
/// .minecraft 多目录管理：添加/切换文件夹，扫描每个文件夹下 versions/ 里的实例。
/// 对应 PCL 的"文件夹列表 -> 当前文件夹 -> 添加已有文件夹"交互。
/// </summary>
public class FolderService
{
    public List<GameVersion> ScanVersions(string minecraftDir, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<GameVersion>();
        var versionsDir = Path.Combine(minecraftDir, "versions");
        if (!Directory.Exists(versionsDir)) return result;

        foreach (var dir in Directory.EnumerateDirectories(versionsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(dir);
            // 以点开头的文件夹（如 .instance_backups）是启动器/系统的内部目录，不是实例，不放进版本列表。
            if (string.IsNullOrEmpty(id) || id.StartsWith('.')) continue;
            
            if (!HasAnyJsonFile(dir)) continue;
            // 其余文件夹本身就是列表入口。json 损坏/缺 jar 的实例也保留，供用户修复或删除。
            var version = new GameVersion { Id = id, McVersion = id, FolderPath = dir };

            try
            {
                var jsonPath = ResolveVersionJson(dir, id);
                var detail = jsonPath == null ? null : JsonSerializer.Deserialize<VersionDetail>(File.ReadAllText(jsonPath));

                var info = VersionInfoResolver.Resolve(dir, id, detail);
                version.McVersion = info.McVersion;
                version.ModLoader = info.ModLoader;
                version.ModLoaderVersion = info.ModLoaderVersion;

                if (detail == null)
                {
                    result.Add(version);
                    continue;
                }

                // Use exactly the same inherited client owner as argument construction and preflight checks.
                var inheritance = VersionInheritanceResolver.Load(minecraftDir, id, ResolveVersionJson);
                var client = inheritance.ClientLayer;
                var jarBaseName = string.IsNullOrEmpty(client.Detail.Id) ? client.VersionId : client.Detail.Id;
                var jar = LauncherService.ResolveVersionFile(client.Directory, jarBaseName, "jar");
                version.IsInstalled = !string.IsNullOrWhiteSpace(inheritance.Effective.MainClass) && jar != null && new FileInfo(jar).Length > 0;
            }
            catch { /* 单个实例正在复制或元数据损坏时仍显示为未完成，不影响其他实例。 */ }
            result.Add(version);
        }
        return result.OrderBy(v => v.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>目录顶层是否至少有一个 .json 文件；目录正在被复制/无法访问时视为有，避免列表闪烁。</summary>
    private static bool HasAnyJsonFile(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly).Any(); }
        catch { return true; }
    }

    internal static string? ResolveVersionJson(string dir, string folderId)
    {
        if (!Directory.Exists(dir)) return null;
        var exact = Path.Combine(dir, $"{folderId}.json");
        if (File.Exists(exact)) return exact;

        var jsonFiles = Directory.GetFiles(dir, "*.json");
        var candidates = new List<string>();
        foreach (var path in jsonFiles.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(id.GetString())) continue;
                var hasMainClass = root.TryGetProperty("mainClass", out var mainClass) &&
                    mainClass.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mainClass.GetString());
                var hasParent = root.TryGetProperty("inheritsFrom", out var parent) &&
                    parent.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(parent.GetString());
                if (!hasMainClass && !hasParent) continue;
                if (string.Equals(id.GetString(), folderId, StringComparison.OrdinalIgnoreCase)) return path;
                candidates.Add(path);
            }
            catch { /* 忽略无关或尚未写完的 json，继续寻找版本描述文件。 */ }
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>扫描 &lt;minecraftDir&gt;/saves/ 下的存档名（用于数据包安装时选择目标存档）。</summary>
    public List<string> ScanSaves(string minecraftDir)
    {
        var savesDir = Path.Combine(minecraftDir, "saves");
        if (!Directory.Exists(savesDir)) return new List<string>();
        return Directory.GetDirectories(savesDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n)
            .ToList();
    }

    public GameFolder AddFolder(AppConfig config, string path, string? name = null)
    {
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.Length == 0 || (path.Length == 2 && path[1] == ':')) path += Path.DirectorySeparatorChar;
        var existing = config.Folders.FirstOrDefault(item => string.Equals(
            Path.GetFullPath(item.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        Directory.CreateDirectory(path);
        var folder = new GameFolder { Name = name ?? Path.GetFileName(path.TrimEnd('\\', '/')), Path = path };
        config.Folders.Add(folder);
        return folder;
    }

    public void ImportVersionFolder(string minecraftDir, string sourceFolder, string instanceName)
    {
        if (Plugins.PluginInterceptionRegistry.HasInstanceInterceptors) throw new Plugins.PluginOperationException("AsyncRequired", "请使用异步实例导入。");
        ImportVersionFolderCore(minecraftDir, sourceFolder, instanceName);
    }
    public async Task ImportVersionFolderAsync(string minecraftDir, string sourceFolder, string instanceName, CancellationToken ct = default)
    {
        await Plugins.PluginInterceptionRegistry.ValidateInstanceAsync(minecraftDir, instanceName, "folder-import", ct);
        await Task.Run(() => { ct.ThrowIfCancellationRequested(); ImportVersionFolderCore(minecraftDir, sourceFolder, instanceName, ct); }, ct);
    }
    private void ImportVersionFolderCore(string minecraftDir, string sourceFolder, string instanceName, CancellationToken ct = default)
    {
        var versionsDir = Path.Combine(minecraftDir, "versions");
        ArchiveImportSafety.CheckLinks(versionsDir);
        Directory.CreateDirectory(versionsDir);
        if (instanceName.Contains('/') || instanceName.Contains('\\')) throw new InvalidDataException("实例名不能包含目录路径。");
        var targetDir = ArchiveImportSafety.Resolve(versionsDir, instanceName);
        if (Directory.Exists(targetDir))
            throw new InvalidOperationException($"目标文件夹「{instanceName}」已经存在。");

        ArchiveImportSafety.CheckLinks(sourceFolder);
        if (ResolveVersionJson(sourceFolder, Path.GetFileName(sourceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))) == null)
            throw new InvalidDataException("源文件夹顶层没有可识别的版本 JSON，未创建实例。");
        CopyVersionDirectoryAtomically(sourceFolder, targetDir, ct);
        Plugins.PluginHostBridge.Current?.NotifyInstanceCreated(minecraftDir, instanceName);
    }

    /// <summary>"导入实例文件"：解压一个打包成 .zip 的实例（可能是"版本 json 直接在压缩包根目录"，
    /// 也可能是"压缩包里包了一层文件夹再放版本 json"这两种常见打包方式），归拢进
    /// &lt;minecraftDir&gt;/versions/&lt;instanceName&gt;。</summary>
    public void ImportVersionZip(string minecraftDir, string zipPath, string instanceName)
    {
        if (Plugins.PluginInterceptionRegistry.HasInstanceInterceptors) throw new Plugins.PluginOperationException("AsyncRequired", "请使用异步实例导入。");
        ImportVersionZipCore(minecraftDir, zipPath, instanceName);
    }
    public async Task ImportVersionZipAsync(string minecraftDir, string zipPath, string instanceName, CancellationToken ct = default)
    {
        await Plugins.PluginInterceptionRegistry.ValidateInstanceAsync(minecraftDir, instanceName, "zip-import", ct);
        await Task.Run(() => { ct.ThrowIfCancellationRequested(); ImportVersionZipCore(minecraftDir, zipPath, instanceName, ct); }, ct);
    }
    private void ImportVersionZipCore(string minecraftDir, string zipPath, string instanceName, CancellationToken ct = default)
    {
        var versionsDir = Path.Combine(minecraftDir, "versions");
        ArchiveImportSafety.CheckLinks(versionsDir);
        Directory.CreateDirectory(versionsDir);
        if (instanceName.Contains('/') || instanceName.Contains('\\')) throw new InvalidDataException("实例名不能包含目录路径。");
        var targetDir = ArchiveImportSafety.Resolve(versionsDir, instanceName);
        if (Directory.Exists(targetDir))
            throw new InvalidOperationException($"目标文件夹「{instanceName}」已经存在。");

        var tempDir = Path.Combine(Path.GetTempPath(), "XCL2_ImportInstance_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            ArchiveImportSafety.CheckLinks(zipPath);
            using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
            {
                ArchiveImportSafety.ValidateArchive(archive, tempDir);
                foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
                    ArchiveImportSafety.Extract(entry, tempDir, entry.FullName.Replace('\\', '/'), ct);
            }

            // 兼容多层包装及 .minecraft/versions/<版本>，避免元数据被拷到实例的子目录里。
            var sourceDir = tempDir;
            while (ResolveVersionJson(sourceDir, Path.GetFileName(sourceDir)) == null)
            {
                ct.ThrowIfCancellationRequested();
                var minecraftDirInZip = Path.Combine(sourceDir, ".minecraft");
                var versionsDirInZip = Path.Combine(sourceDir, "versions");
                if (Directory.Exists(minecraftDirInZip))
                {
                    sourceDir = minecraftDirInZip;
                    continue;
                }
                if (Directory.Exists(versionsDirInZip))
                {
                    sourceDir = versionsDirInZip;
                    continue;
                }
                var directories = Directory.GetDirectories(sourceDir)
                    .Where(d => !string.Equals(Path.GetFileName(d), "__MACOSX", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (directories.Length != 1) break;
                sourceDir = directories[0];
            }

            if (ResolveVersionJson(sourceDir, Path.GetFileName(sourceDir)) == null)
                throw new InvalidOperationException("压缩包里没有找到版本 json 文件，这可能不是一个完整的实例。");

            CopyVersionDirectoryAtomically(sourceDir, targetDir, ct);
            Plugins.PluginHostBridge.Current?.NotifyInstanceCreated(minecraftDir, instanceName);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 临时文件清理失败不影响导入结果 */ }
        }
    }

    private static void CopyVersionDirectoryAtomically(string sourceDir, string targetDir, CancellationToken ct)
    {
        var source = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(source, target, comparison) || target.StartsWith(source + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("不能把实例导入到源文件夹内部，未创建实例。");
        var parent = Path.GetDirectoryName(targetDir)!;
        var staging = Path.Combine(parent, ".xcl2-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectoryRecursive(sourceDir, staging, ct);
            ct.ThrowIfCancellationRequested(); ArchiveImportSafety.CheckLinks(targetDir);
            // Directory.Move fails if another importer created the target; never merge into or delete it.
            Directory.Move(staging, targetDir);
        }
        finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { } }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir, CancellationToken ct)
    {
        var pending = new Stack<(string Source, string Target)>();
        pending.Push((sourceDir, targetDir));
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            ArchiveImportSafety.CheckLinks(current.Source); ArchiveImportSafety.CheckLinks(current.Target);
            Directory.CreateDirectory(current.Target);
            foreach (var file in Directory.GetFiles(current.Source))
            {
                ct.ThrowIfCancellationRequested(); ArchiveImportSafety.CheckLinks(file);
                var destination = ArchiveImportSafety.Resolve(current.Target, Path.GetFileName(file));
                File.Copy(file, destination, overwrite: false);
            }
            foreach (var directory in Directory.GetDirectories(current.Source))
                pending.Push((directory, ArchiveImportSafety.Resolve(current.Target, Path.GetFileName(directory))));
        }
    }
}
