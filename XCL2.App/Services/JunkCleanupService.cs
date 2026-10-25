using System.IO;

namespace XCL2.App.Services;

/// <summary>
/// 「百宝箱」-「清理游戏垃圾」：清理 .minecraft 目录下几类"确定安全删除、不影响存档/
/// 设置/已装 Mod"的临时/缓存文件——
/// - versions/&lt;id&gt;/*.log、crash-reports 之外散落的 .log 临时文件；
/// - logs/ 下超过一定天数的历史日志压缩包(.log.gz)；
/// - 崩溃报告(crash-reports/) 超过一定天数的旧报告；
/// - webcache/webcache2(部分整合包/mod 用到的浏览器缓存目录，纯缓存无状态)；
/// - .mixin.out(Mixin 调试输出，纯调试产物)。
///
/// 刻意不清理：saves/(存档)、resourcepacks/、shaderpacks/、mods/、config/、
/// options.txt 等一切"用户数据/游戏配置"。清理范围偏保守，宁可少清一点，也不能误删
/// 玩家真正在意的东西。
/// </summary>
public static class JunkCleanupService
{
    public record JunkScanResult(List<JunkItem> Items, long TotalBytes);
    public record JunkItem(string Path, long SizeBytes, string Category)
    {
        // Bind deletion to an actual scan and detect files replaced while confirmation was open.
        internal string? ScanRoot { get; init; }
        internal DateTime ScannedLastWriteUtc { get; init; }
    }

    /// <summary>崩溃报告/历史日志超过这个天数才视为"可清理"，避免删掉玩家刚遇到还没来得及
    /// 反馈的崩溃记录。</summary>
    private const int OldFileThresholdDays = 7;

    public static JunkScanResult Scan(string minecraftDir)
    {
        minecraftDir = Path.GetFullPath(minecraftDir);
        ArchiveImportSafety.CheckLinks(minecraftDir);
        var items = new List<JunkItem>();

        void AddIfExists(string path, string category, bool oldOnly = false)
        {
            try
            {
                ArchiveImportSafety.CheckLinks(path);
                var info = new FileInfo(path);
                if (!info.Exists || !IsAllowedCandidate(minecraftDir, path)) return;
                if (oldOnly && info.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-OldFileThresholdDays)) return;
                items.Add(new JunkItem(path, info.Length, category)
                { ScanRoot = minecraftDir, ScannedLastWriteUtc = info.LastWriteTimeUtc });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }

        void ScanDirOldFiles(string dir, string pattern, string category)
        {
            try
            {
                ArchiveImportSafety.CheckLinks(dir);
                if (!Directory.Exists(dir)) return;
                foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                    AddIfExists(file, category, oldOnly: true);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }

        void ScanDirAllFiles(string dir, string category)
        {
            var pending = new Stack<string>(); pending.Push(dir);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                try
                {
                    ArchiveImportSafety.CheckLinks(current);
                    if (!Directory.Exists(current)) continue;
                    foreach (var file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
                        AddIfExists(file, category);
                    foreach (var child in Directory.EnumerateDirectories(current))
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            }
        }

        // 历史压缩日志（logs/2024-01-01-1.log.gz 这类按天归档、当天早就用不上的旧日志）。
        ScanDirOldFiles(Path.Combine(minecraftDir, "logs"), "*.log.gz", "历史日志压缩包");

        // 旧的崩溃报告：保留最近一周，更早的视为"早就处理过/不会再看"。
        ScanDirOldFiles(Path.Combine(minecraftDir, "crash-reports"), "*.txt", "旧崩溃报告");

        // 网页缓存目录（部分整合包/内嵌浏览器组件用的缓存，纯缓存无状态，删除不影响功能，
        // 下次用到时会自动重新生成）。
        ScanDirAllFiles(Path.Combine(minecraftDir, "webcache"), "网页缓存(webcache)");
        ScanDirAllFiles(Path.Combine(minecraftDir, "webcache2"), "网页缓存(webcache2)");

        // Mixin 调试输出目录：纯调试产物，正常游玩不需要。
        ScanDirAllFiles(Path.Combine(minecraftDir, ".mixin.out"), "Mixin调试输出");

        // hs_err_pid*.log：JVM 崩溃时在 .minecraft 根目录生成的原生崩溃转储文件，
        // 排查完一次问题之后基本不会再看，但经常被遗忘在目录里越积越多。
        try
        {
            foreach (var file in Directory.Exists(minecraftDir)
                         ? Directory.EnumerateFiles(minecraftDir, "hs_err_pid*.log", SearchOption.TopDirectoryOnly)
                         : Array.Empty<string>())
                AddIfExists(file, "JVM崩溃转储");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }

        return new JunkScanResult(items, items.Sum(i => i.SizeBytes));
    }

    /// <summary>实际删除扫描出的垃圾文件，返回成功删除的数量和释放的字节数。
    /// 单个文件删除失败（占用中/权限不足）不中断整体流程，跳过继续处理下一个。</summary>
    public static (int deletedCount, long freedBytes) Delete(List<JunkItem> items)
    {
        var deletedCount = 0;
        long freedBytes = 0;
        foreach (var item in items)
        {
            try
            {
                if (string.IsNullOrEmpty(item.ScanRoot) || !IsAllowedCandidate(item.ScanRoot, item.Path)) continue;
                ArchiveImportSafety.CheckLinks(item.Path);
                var current = new FileInfo(item.Path);
                if (!current.Exists || current.Length != item.SizeBytes || current.LastWriteTimeUtc != item.ScannedLastWriteUtc) continue;
                File.Delete(item.Path);
                deletedCount++;
                freedBytes += item.SizeBytes;
            }
            catch { /* 单个文件删除失败（可能正被占用），跳过继续处理其它文件 */ }
        }
        return (deletedCount, freedBytes);
    }

    private static bool IsAllowedCandidate(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (Path.IsPathRooted(relative)) return false;
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(part => part is "" or "." or "..")) return false;
        if (parts.Length == 1)
            return parts[0].StartsWith("hs_err_pid", StringComparison.OrdinalIgnoreCase) &&
                parts[0].EndsWith(".log", StringComparison.OrdinalIgnoreCase);
        if (parts[0].Equals("webcache", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("webcache2", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals(".mixin.out", StringComparison.OrdinalIgnoreCase)) return true;
        return parts.Length == 2 &&
            (parts[0].Equals("logs", StringComparison.OrdinalIgnoreCase) && parts[1].EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase) ||
             parts[0].Equals("crash-reports", StringComparison.OrdinalIgnoreCase) && parts[1].EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return $"{size:0.##} {units[unitIndex]}";
    }
}
