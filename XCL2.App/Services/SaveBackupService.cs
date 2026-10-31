using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace XCL2.App.Services;

public sealed record SaveBackupRecord(string Id, string WorldPath, string WorldName, DateTime CreatedUtc,
    long SourceBytes, int FileCount, string ArchiveSha256, bool Scheduled);

/// <summary>Save-only archives; restore always creates a new sibling world, never overwrites a save.</summary>
public sealed class SaveBackupService
{
    private const int MaxFiles = 100000;
    private const long MaxBytes = 100L * 1024 * 1024 * 1024;
    private readonly string _root;
    public SaveBackupService(string directory) { _root = Canonical(directory); RejectLinks(_root); }
    public string DirectoryPath => _root;
    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static string Key(string worldPath) => Convert.ToHexString(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Canonical(worldPath).ToUpperInvariant())))[..24];
    public static void RejectLinks(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = new DirectoryInfo(Directory.Exists(full) ? full : Path.GetDirectoryName(full)!); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持链接或重解析目录。");
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持链接文件。");
    }
    private static FileStream AcquireWorldLock(string worldPath)
    {
        var path = Path.Combine(worldPath, "session.lock"); RejectLinks(path);
        FileStream? handle = null;
        try
        {
            handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) handle.Lock(0, 1);
            return handle;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { handle?.Dispose(); throw new IOException("存档正在使用或无法取得存档锁，请先退出游戏或服务器。", ex); }
    }
    private static bool IsSessionLock(string file, string worldPath)
        => string.Equals(Path.GetFullPath(file), Path.Combine(worldPath, "session.lock"), StringComparison.OrdinalIgnoreCase);
    private string Folder(string worldPath) => Path.Combine(_root, Key(worldPath));
    private static string Hash(Stream input, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[81920]; int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        ct.ThrowIfCancellationRequested(); return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string Hash(string path, CancellationToken ct) { using var input = File.OpenRead(path); return Hash(input, ct); }
    public IReadOnlyList<SaveBackupRecord> List(string worldPath)
    {
        var dir = Folder(worldPath); RejectLinks(dir);
        if (!Directory.Exists(dir)) return Array.Empty<SaveBackupRecord>();
        var result = new List<SaveBackupRecord>();
        var manifests = Directory.EnumerateFiles(dir, "*.json").Take(10001).ToArray();
        if (manifests.Length > 10000) throw new IOException("此存档的备份记录超过 10,000 份，请打开备份位置整理或选择新的位置；现有备份均已保留。");
        foreach (var file in manifests)
        {
            try
            {
                RejectLinks(file); if (new FileInfo(file).Length > 65536) continue;
                var record = JsonSerializer.Deserialize<SaveBackupRecord>(File.ReadAllText(file));
                if (record == null || record.FileCount < 1 || record.FileCount > MaxFiles || record.SourceBytes < 0 || record.SourceBytes > MaxBytes || !Guid.TryParseExact(record.Id, "N", out _) || Path.GetFileNameWithoutExtension(file) != record.Id
                    || !string.Equals(Canonical(record.WorldPath), Canonical(worldPath), StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(Path.Combine(dir, record.Id + ".zip"))) continue;
                result.Add(record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
        return result.OrderByDescending(r => r.CreatedUtc).ToArray();
    }
    public SaveBackupRecord Create(string worldPath, string worldName, bool scheduled = false, CancellationToken ct = default)
    {
        worldPath = Canonical(worldPath); RejectLinks(worldPath);
        if (!File.Exists(Path.Combine(worldPath, "level.dat"))) throw new IOException("存档缺少 level.dat，未创建备份。");
        if (_root.Equals(worldPath, StringComparison.OrdinalIgnoreCase) || _root.StartsWith(worldPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("备份位置不能放在正在备份的存档内部。");
        ct.ThrowIfCancellationRequested(); using var worldLock = AcquireWorldLock(worldPath);
        var files = new List<(string Path, long Length, DateTime Changed)>(); long bytes = 0;
        var seenDirectories = 0;
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((worldPath, 0));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested(); var (directory, depth) = pending.Pop(); RejectLinks(directory);
            if (depth > 128 || ++seenDirectories > MaxFiles) throw new IOException("存档目录层级或目录数量超过保护上限，未备份。");
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                ct.ThrowIfCancellationRequested(); if (IsSessionLock(file, worldPath)) continue; RejectLinks(file); var info = new FileInfo(file);
                bytes = checked(bytes + info.Length);
                if (files.Count >= MaxFiles || bytes > MaxBytes) throw new IOException("存档超过备份保护上限（10 万文件 / 100 GB）。");
                files.Add((file, info.Length, info.LastWriteTimeUtc));
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
            { if (pending.Count >= MaxFiles) throw new IOException("存档目录数量超过保护上限。"); pending.Push((child, depth + 1)); }
        }
        var dir = Folder(worldPath); Directory.CreateDirectory(dir); RejectLinks(dir);
        if (Directory.EnumerateFiles(dir, "*.json").Take(10000).Count() >= 10000)
            throw new IOException("此存档已有 10,000 份备份，请先整理或选择新的备份位置；没有删除现有备份。");
        var drive = new DriveInfo(Path.GetPathRoot(dir)!);
        if (drive.IsReady && drive.AvailableFreeSpace < bytes + 64L * 1048576) throw new IOException("备份磁盘可用空间不足，请更换位置或主动清理旧备份。");
        var id = Guid.NewGuid().ToString("N"); var path = Path.Combine(dir, id + ".zip"); var temp = path + ".part";
        var metaTemp = Path.Combine(dir, id + ".json.part");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested(); RejectLinks(file.Path);
                    var entry = archive.CreateEntry(Path.GetRelativePath(worldPath, file.Path).Replace('\\', '/'), CompressionLevel.Fastest);
                    using var source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var target = entry.Open(); Copy(source, target, file.Length, ct);
                    var after = new FileInfo(file.Path);
                    if (after.Length != file.Length || after.LastWriteTimeUtc != file.Changed) throw new IOException("备份期间存档发生变化，请退出游戏后重试。");
                }
            }
            // Detect additions/removals/late writes too; do not present an inconsistent archive as complete.
            var initial = files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var verified = 0;
            pending.Push((worldPath, 0)); seenDirectories = 0;
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested(); var (directory, depth) = pending.Pop(); RejectLinks(directory);
                if (depth > 128 || ++seenDirectories > MaxFiles) throw new IOException("备份期间存档目录结构发生变化。");
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    ct.ThrowIfCancellationRequested(); if (IsSessionLock(file, worldPath)) continue; RejectLinks(file); var info = new FileInfo(file);
                    if (!initial.TryGetValue(file, out var expected) || info.Length != expected.Length || info.LastWriteTimeUtc != expected.Changed)
                        throw new IOException("备份期间存档发生变化，请退出游戏后重试。");
                    verified++;
                }
                foreach (var child in Directory.EnumerateDirectories(directory))
                { if (pending.Count >= MaxFiles) throw new IOException("备份期间存档目录结构发生变化。"); pending.Push((child, depth + 1)); }
            }
            if (verified != files.Count) throw new IOException("备份期间有存档文件被移除。");
            ct.ThrowIfCancellationRequested();
            var record = new SaveBackupRecord(id, worldPath, worldName, DateTime.UtcNow, bytes, files.Count, Hash(temp, ct), scheduled);
            File.WriteAllText(metaTemp, JsonSerializer.Serialize(record)); File.Move(temp, path); File.Move(metaTemp, Path.Combine(dir, id + ".json"));
            return record;
        }
        catch { try { File.Delete(temp); File.Delete(metaTemp); File.Delete(path); } catch { } throw; }
    }
    private static void Copy(Stream source, Stream target, long expected, CancellationToken ct)
    {
        var buffer = new byte[81920]; long total = 0; int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        { ct.ThrowIfCancellationRequested(); total = checked(total + read); if (total > expected) throw new IOException("文件大小已改变或压缩包内容异常。"); target.Write(buffer, 0, read); }
        if (total != expected) throw new IOException("文件未完整读取。");
    }
    private static bool ReservedName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9';
    }
    public static string CopyTarget(string worldPath, string directoryName)
    {
        if (ReservedName(directoryName ?? "") || string.IsNullOrWhiteSpace(directoryName) || directoryName is "." or ".." || directoryName.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0' }) >= 0
            || directoryName.EndsWith(' ') || directoryName.EndsWith('.')) throw new IOException("请输入单独的存档目录名称。");
        var parent = Path.GetDirectoryName(Canonical(worldPath))!; RejectLinks(parent);
        var result = Path.Combine(parent, directoryName);
        if (Directory.Exists(result) || File.Exists(result)) throw new IOException("该目录已经存在，请使用另一个副本名称。");
        return result;
    }
    public string RestoreCopy(string worldPath, string recordId, string directoryName, CancellationToken ct = default)
    {
        var record = List(worldPath).SingleOrDefault(r => r.Id == recordId) ?? throw new IOException("备份记录不存在。");
        worldPath = Canonical(worldPath); ct.ThrowIfCancellationRequested();
        using var worldLock = Directory.Exists(worldPath) ? AcquireWorldLock(worldPath) : null;
        var target = CopyTarget(worldPath, directoryName); var dir = Folder(worldPath);
        var archivePath = Path.Combine(dir, record.Id + ".zip"); RejectLinks(archivePath);
        using var archiveInput = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Hash(archiveInput, ct), record.ArchiveSha256, StringComparison.Ordinal)) throw new IOException("备份校验失败，未写入存档。");
        archiveInput.Position = 0; using var archive = new ZipArchive(archiveInput, ZipArchiveMode.Read);
        var entries = new List<(ZipArchiveEntry Entry, string Relative)>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested(); var relative = entry.FullName.Replace('\\', '/');
            if (relative.EndsWith('/')) continue;
            var parts = relative.Split('/');
            if (parts.Length > 128 || parts.Any(p => ReservedName(p) || string.IsNullOrWhiteSpace(p) || p is "." or ".." || p.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|', '\0' }) >= 0 || p.EndsWith(' ') || p.EndsWith('.'))
                || !names.Add(relative) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new IOException("备份含有不安全的路径或重复文件，未恢复。");
            total = checked(total + entry.Length);
            if (entries.Count >= MaxFiles || total > MaxBytes) throw new IOException("备份内容超出保护上限。");
            entries.Add((entry, Path.Combine(parts)));
        }
        if (total != record.SourceBytes || entries.Count != record.FileCount || !names.Contains("level.dat")) throw new IOException("备份目录与记录不一致。");
        var restoreDrive = new DriveInfo(Path.GetPathRoot(target)!);
        if (restoreDrive.IsReady && restoreDrive.AvailableFreeSpace < total + 64L * 1048576) throw new IOException("恢复磁盘空间不足，未生成副本。");
        var staging = Path.Combine(Path.GetDirectoryName(target)!, ".xcl2-restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging); RejectLinks(staging);
            foreach (var (entry, relative) in entries)
            {
                ct.ThrowIfCancellationRequested(); var path = Path.Combine(staging, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = entry.Open(); using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); Copy(input, output, entry.Length, ct);
            }
            ct.ThrowIfCancellationRequested(); CopyTarget(worldPath, directoryName); Directory.Move(staging, target); return target;
        }
        catch { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } throw; }
    }
    public void TrimScheduled(string worldPath, int keep)
    {
        foreach (var record in List(worldPath).Where(r => r.Scheduled).Skip(Math.Clamp(keep, 1, 100)))
        {
            var dir = Folder(worldPath); RejectLinks(dir);
            var zip = Path.Combine(dir, record.Id + ".zip"); var meta = Path.Combine(dir, record.Id + ".json"); RejectLinks(zip); RejectLinks(meta);
            File.Delete(zip); File.Delete(meta);
        }
    }
}
