using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace XCL2.App.Services.Worlds;

public sealed record WorldRepairCandidate(string Path, string Name, DateTime ModifiedUtc, string Sha256);
public sealed record WorldHealthReport(string Directory, bool MetadataReadable, int RegionFilesChecked,
    IReadOnlyList<string> Findings, IReadOnlyList<WorldRepairCandidate> Candidates);

/// <summary>Conservative Java-save maintenance: inspect region headers; restore only validated metadata.
/// Region/chunk/player files are never removed or rewritten by repair.</summary>
public static partial class WorldArchiveService
{
    public static IReadOnlyList<WorldArchive> DiscoverServerWorlds(IEnumerable<string> serverDirectories, Action<string>? warning = null)
    {
        var result = new List<WorldArchive>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in serverDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                RejectLinks(root);
                if (!Directory.Exists(root)) continue;
                // Includes custom level-name directories and Bukkit multi-world directories, without recursive scanning.
                foreach (var path in new[] { root }.Concat(Directory.EnumerateDirectories(root).Take(2000)))
                {
                    if (!seen.Add(System.IO.Path.GetFullPath(path)) || !File.Exists(System.IO.Path.Combine(path, "level.dat"))) continue;
                    try { result.Add(Read(path) with { Source = "服务器" }); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    { warning?.Invoke(path + "：" + ex.Message + "；可用“修复其他存档”选择该目录。"); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warning?.Invoke(root + "：" + ex.Message); }
        }
        return result;
    }

    public static WorldHealthReport Inspect(string directory, CancellationToken ct = default)
    {
        directory = System.IO.Path.GetFullPath(directory); RejectLinks(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var findings = new List<string>(); var candidates = new List<WorldRepairCandidate>();
        bool readable;
        try { _ = Read(directory); readable = true; findings.Add("✓ level.dat 可读取；世界设置可编辑。"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
        { readable = false; findings.Add("需要处理：level.dat 缺失或无法读取。" + ex.Message); }
        var files = new List<string> { System.IO.Path.Combine(directory, "level.dat_old") };
        var backups = System.IO.Path.Combine(directory, "xcl2-backups"); RejectLinks(backups);
        if (Directory.Exists(backups)) files.AddRange(Directory.EnumerateFiles(backups, "*.dat").Take(1000));
        foreach (var file in files.Where(File.Exists))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                RejectLinks(file); ValidateRepairDocument(NbtDocument.Load(file));
                candidates.Add(new(file, System.IO.Path.GetFileName(file), File.GetLastWriteTimeUtc(file), HashFile(file)));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
            { if (findings.Count < 50) findings.Add("备份不可用：" + System.IO.Path.GetFileName(file) + "（" + ex.Message + "）"); }
        }
        var regions = 0; var examinedDirectories = 0; var scanLimitReached = false; var pending = new Stack<(string Path, int Depth)>(); pending.Push((directory, 0));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested(); var (path, depth) = pending.Pop(); RejectLinks(path);
            if (++examinedDirectories > 10000 || depth > 32) { findings.Add("目录扫描达到保护上限；部分目录未检查。"); break; }
            foreach (var file in Directory.EnumerateFiles(path, "*.mca"))
            {
                ct.ThrowIfCancellationRequested();
                if (++regions > 10000) { findings.Add("区域文件超过 10,000 个，已停止扫描；部分文件未检查。"); pending.Clear(); scanLimitReached = true; break; }
                try { InspectRegionHeader(file); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                { if (findings.Count < 50) findings.Add("区域需人工检查：" + System.IO.Path.GetRelativePath(directory, file) + "（" + ex.Message + "）。请从完整备份恢复，或在副本上使用专用工具检查。"); }
            }
            if (scanLimitReached) break;
            foreach (var child in Directory.EnumerateDirectories(path))
            {
                if (System.IO.Path.GetFileName(child).Equals("xcl2-backups", StringComparison.OrdinalIgnoreCase)) continue;
                if (pending.Count < 10000) pending.Push((child, depth + 1));
            }
        }
        findings.Add($"已检查 {Math.Min(regions, 10000):N0} 个区域文件的文件头。此检查不解码区块内容；不会删除或重新生成区块。");
        findings.Add(candidates.Count == 0 ? "没有可用的本地元数据备份；请通过备份管理恢复完整存档副本。" : $"找到 {candidates.Count} 份可用元数据备份。恢复后世界时间、出生点等回到该备份状态，已有区块保留。");
        return new(directory, readable, Math.Min(regions, 10000), findings, candidates.OrderByDescending(c => c.ModifiedUtc).ToArray());
    }

    private static void InspectRegionHeader(string file)
    {
        RejectLinks(file); using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < 8192) throw new InvalidDataException("区域文件头不足 8 KiB");
        var header = new byte[4096]; stream.ReadExactly(header); var spans = new List<(int Start, int End)>();
        for (var i = 0; i < 1024; i++)
        {
            var entry = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(i * 4, 4));
            if (entry == 0) continue;
            int offset = (int)(entry >> 8), count = (int)(entry & 255);
            if (offset < 2 || count == 0 || ((long)offset + count) * 4096 > stream.Length)
                throw new InvalidDataException($"第 {i} 个区块的扇区位置越界");
            spans.Add((offset, offset + count));
        }
        var ordered = spans.OrderBy(s => s.Start).ToArray();
        for (var i = 1; i < ordered.Length; i++)
            if (ordered[i].Start < ordered[i - 1].End) throw new InvalidDataException("多个区块占用了相同扇区");
    }
    private static void ValidateRepairDocument(NbtDocument document)
    {
        var data = document.Root.Get("Data") ?? throw new InvalidDataException("缺少 Java 世界 Data 标签");
        _ = data.Compound;
        if (data.Get("LevelName") is not { Type: 8 } || data.Get("GameType") is not { Type: 3 })
            throw new InvalidDataException("缺少世界名称或模式，不作为自动恢复来源");
    }
    private static string HashFile(string file)
    { using var input = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(input)); }

    public static string RestoreMetadata(string directory, WorldRepairCandidate candidate)
    {
        lock (Gate)
        {
            directory = System.IO.Path.GetFullPath(directory); RejectLinks(directory);
            var source = System.IO.Path.GetFullPath(candidate.Path);
            var backups = System.IO.Path.Combine(directory, "xcl2-backups"); RejectLinks(backups);
            if (!source.Equals(System.IO.Path.Combine(directory, "level.dat_old"), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(System.IO.Path.GetDirectoryName(source), backups, StringComparison.OrdinalIgnoreCase))
                throw new IOException("恢复来源必须是此世界的 level.dat_old 或 xcl2-backups 中的备份。");
            RejectLinks(source); using var worldLock = LockWorld(directory);
            if (!string.Equals(HashFile(source), candidate.Sha256, StringComparison.Ordinal)) throw new IOException("备份在检查后发生变化，请重新检查再恢复。");
            ValidateRepairDocument(NbtDocument.Load(source));
            var level = System.IO.Path.Combine(directory, "level.dat"); RejectLinks(level);
            Directory.CreateDirectory(backups);
            var backup = System.IO.Path.Combine(backups, $"pre-repair-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.dat");
            // Keep the exact damaged bytes as evidence and for undo; never overwrite level.dat_old.
            if (File.Exists(level)) File.Copy(level, backup, false);
            else { backup += ".missing.txt"; File.WriteAllText(backup, "恢复之前 level.dat 不存在。区域、玩家、维度文件未修改。"); }
            var temp = level + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temp, false); ValidateRepairDocument(NbtDocument.Load(temp));
                if (!string.Equals(HashFile(temp), candidate.Sha256, StringComparison.Ordinal)) throw new IOException("恢复来源发生变化，未替换原文件。");
                File.Move(temp, level, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return backup;
        }
    }
}
