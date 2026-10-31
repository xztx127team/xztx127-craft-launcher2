using System.IO;
using System.IO.Compression;

namespace XCL2.App.Services;

/// <summary>
/// 实例级备份/还原服务。
///
/// 设计出发点：加载器的增删/升级/降级/重装本质上都是"把 versions/&lt;id&gt; 该实例目录
/// 里的内容整体替换"，而存档、模组(mods)、资源包(resourcepacks)、配置(config) 只要用户是
/// 独立实例(隔离)模式，也都放在这同一个目录树下。所以备份粒度就定在"整个实例目录打包成一个
/// zip"——既不会漏掉存档/模组/资源包，也不需要针对"加载器专属文件"单独识别哪些该备份、
/// 哪些不该备份，最简单也最不容易出错。
///
/// 备份 zip 统一放在 versions/.instance_backups 独立文件夹下，避免：
///   1. 打包过程中把自己正在写的 zip 也一起扫描进去（自引用）；
///   2. 被 FolderService.ScanVersions 误扫描成一个新的"版本"出现在版本列表里。
/// </summary>
public static class InstanceBackupService
{
    public class BackupRecord
    {
        public string FilePath { get; set; } = "";
        public string FileName => Path.GetFileName(FilePath);
        public DateTime CreatedAtUtc { get; set; }
        public long SizeBytes { get; set; }
    }

    private static string GetBackupsDir(string folderPath)
        => ArchiveImportSafety.Resolve(Path.Combine(folderPath, "versions"), ".instance_backups");

    private static string ResolveInstance(string folderPath, string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId) || versionId.IndexOfAny(new[] { '/', '\\' }) >= 0 ||
            versionId.Equals(".instance_backups", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("实例名称必须是单个有效目录名。");
        return ArchiveImportSafety.Resolve(Path.Combine(folderPath, "versions"), versionId);
    }

    /// <summary>
    /// 备份 versions/&lt;versionId&gt; 整个目录到 zip。
    /// reason 会拼进文件名里，方便用户在备份列表里一眼看出"这是升级前/降级前/重装前/卸载加载器前"
    /// 哪次操作留下的备份，不用逐个打开才知道用途。
    /// </summary>
    public static async Task<string> CreateBackupAsync(string folderPath, string versionId, string reason,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var sourceDir = ResolveInstance(folderPath, versionId);
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"找不到实例目录：{sourceDir}");

        var backupsDir = GetBackupsDir(folderPath);
        Directory.CreateDirectory(backupsDir);

        var safeReason = string.Concat((reason ?? "backup").Take(64)
            .Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c)).TrimEnd(' ', '.');
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fffffff");
        var zipPath = ArchiveImportSafety.Resolve(backupsDir, $"{versionId}_{safeReason}_{timestamp}_{Guid.NewGuid():N}.zip");

        await Task.Run(() =>
        {
            progress?.Report(0);
            ct.ThrowIfCancellationRequested();
            // Check each entry before descending or opening it. A recursive ZIP helper
            // follows directory links and can silently include files outside the instance.
            ArchiveImportSafety.ExportAtomically(zipPath, temporary =>
            {
                using var archive = ZipFile.Open(temporary, ZipArchiveMode.Create);
                var pending = new Stack<(string Directory, int Depth)>();
                pending.Push((sourceDir, 0));
                var entryCount = 0;
                while (pending.Count > 0)
                {
                    var (directory, depth) = pending.Pop();
                    ct.ThrowIfCancellationRequested();
                    ArchiveImportSafety.CheckLinks(directory);
                    if (depth > 128) throw new InvalidDataException("实例目录超过备份层级上限。");
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        ct.ThrowIfCancellationRequested();
                        ArchiveImportSafety.CheckLinks(entry);
                        if (++entryCount > ArchiveImportSafety.MaximumEntries)
                            throw new InvalidDataException("实例超过十万项，请先整理后再备份。");
                        var relative = Path.GetRelativePath(sourceDir, entry).Replace('\\', '/');
                        _ = ArchiveImportSafety.Resolve(sourceDir, relative);
                        if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                        {
                            archive.CreateEntry(relative + "/");
                            pending.Push((entry, depth + 1));
                        }
                        else
                        {
                            using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                            using var output = archive.CreateEntry(relative, CompressionLevel.Optimal).Open();
                            var buffer = new byte[81920]; int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            { ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                        }
                    }
                }
                ct.ThrowIfCancellationRequested();
            });
            progress?.Report(1);
        }, ct);

        return zipPath;
    }

    /// <summary>列出某个实例的所有备份，按时间倒序（最新的排最前面）。</summary>
    public static List<BackupRecord> ListBackups(string folderPath, string versionId)
    {
        _ = ResolveInstance(folderPath, versionId);
        var backupsDir = GetBackupsDir(folderPath);
        if (!Directory.Exists(backupsDir)) return new List<BackupRecord>();

        return Directory.GetFiles(backupsDir, $"{versionId}_*.zip")
            .Where(f => (File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new BackupRecord { FilePath = f.FullName, CreatedAtUtc = f.LastWriteTimeUtc, SizeBytes = f.Length })
            .ToList();
    }

    /// <summary>
    /// 从备份还原：验证并完整解压到临时目录后再替换，失败时保留当前实例。
    /// 调用方必须在还原前先给出一次明确的二次确认（这是破坏性操作，会覆盖当前所有改动），
    /// 本方法不做交互，只负责执行。
    /// </summary>
    public static async Task RestoreBackupAsync(string folderPath, string versionId, string backupZipPath, CancellationToken ct = default)
    {
        ArchiveImportSafety.CheckLinks(backupZipPath);
        if (!File.Exists(backupZipPath))
            throw new FileNotFoundException("备份文件不存在", backupZipPath);

        var targetDir = ResolveInstance(folderPath, versionId);

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var parent = Path.GetDirectoryName(targetDir)!;
            var id = Guid.NewGuid().ToString("N");
            var staging = Path.Combine(parent, ".xcl2-restore-" + id);
            var previous = Path.Combine(parent, ".xcl2-before-restore-" + id);
            var movedPrevious = false;
            try
            {
                ArchiveImportSafety.CreateNewDirectory(staging);
                using (var archive = ZipFile.OpenRead(backupZipPath))
                {
                    ArchiveImportSafety.ValidateArchive(archive, staging);
                    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var relative = entry.FullName.Replace('\\', '/').TrimEnd('/');
                        if (relative.Length == 0 || !paths.Add(relative))
                            throw new InvalidDataException("备份包含空路径或重复条目。");
                        if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                            Directory.CreateDirectory(ArchiveImportSafety.Resolve(staging, relative));
                        else ArchiveImportSafety.Extract(entry, staging, relative, ct);
                    }
                }
                ct.ThrowIfCancellationRequested();
                ArchiveImportSafety.CheckLinks(targetDir);
                if (Directory.Exists(targetDir))
                {
                    Directory.Move(targetDir, previous);
                    movedPrevious = true;
                }
                try { Directory.Move(staging, targetDir); }
                catch
                {
                    // Never overwrite a directory concurrently created by another process.
                    if (movedPrevious && !Directory.Exists(targetDir) && !File.Exists(targetDir))
                    { Directory.Move(previous, targetDir); movedPrevious = false; }
                    throw;
                }
                if (movedPrevious)
                {
                    // A lock may prevent cleanup; retaining the previous data is safe.
                    try { Directory.Delete(previous, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (movedPrevious)
            {
                throw new IOException("还原未完成；原实例已保留在：" + previous, ex);
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            }
        }, ct);
    }

    /// <summary>
    /// 删除一份备份文件。建议调用方遵循"先启动验证，确认游戏能正常进入后，再让用户选择是否
    /// 删除备份"该顺序——本方法本身不做该判断，只负责删除动作，具体的提示时机由 UI 层
    /// （ToastService 通知里的"删除备份"按钮）控制。
    /// </summary>
    public static void DeleteBackup(string backupZipPath)
    {
        ArchiveImportSafety.CheckLinks(backupZipPath);
        if (File.Exists(backupZipPath)) File.Delete(backupZipPath);
    }

    /// <summary>删除某个实例的全部历史备份，用于备份文件夹清理/主动"垃圾清理"功能接入。</summary>
    public static void DeleteAllBackups(string folderPath, string versionId)
    {
        foreach (var b in ListBackups(folderPath, versionId))
            DeleteBackup(b.FilePath);
    }

    /// <summary>
    /// 操作成功后统一弹出的"是否删除本次备份"提示，左下角、不自动消失。
    /// 明确建议用户先启动游戏验证一次、确认没问题了再删除——真出问题时这份备份就是
    /// 唯一的后悔药，不该在操作一完成就诱导立刻删掉。调用方只需要传新鲜出炉的备份 zip
    /// 路径，不需要自己拼文案/处理按钮点击。
    /// </summary>
    public static void NotifyBackupCreated(string backupZipPath, string operationDescription)
    {
        ToastService.ShowActionPrompt(
            message: $"{operationDescription}成功，已自动生成一份操作前备份。",
            hint: "建议先启动游戏验证一切正常后，再删除这份备份。",
            primaryText: "删除备份",
            primaryAction: () =>
            {
                try { DeleteBackup(backupZipPath); ToastService.ShowInfo("备份已删除"); }
                catch { ToastService.ShowWarning("备份删除失败，可稍后在实例设置里手动清理"); }
            },
            secondaryText: "保留",
            secondaryAction: null);
    }
}
