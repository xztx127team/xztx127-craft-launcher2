using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

public sealed class ModUpdateSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Folder { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string State { get; set; } = "Preparing";
    public List<ModSnapshotFile> Files { get; set; } = new();
}
public sealed class ModSnapshotFile
{
    public string Name { get; set; } = "";
    public string? BeforeHash { get; set; }
    public string? AfterHash { get; set; }
    public long Bytes { get; set; }
}

/// <summary>更新前保存原文件字节；恢复前校验备份和当前文件，拒绝覆盖后续手动编辑。</summary>
public sealed class ModUpdateSnapshotStore(string directory)
{
    private static readonly object Gate = new();
    private string Location(string id) => Regex.IsMatch(id, "^[a-f0-9]{32}$")
        ? Path.Combine(directory, id) : throw new ArgumentException("快照 ID 无效。");

    public static string ResolveFile(string folder, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name != Path.GetFileName(name)
            || name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0' }) >= 0 || name.EndsWith(' ') || name.EndsWith('.'))
            throw new IOException("模组文件名无效。");
        var path = Path.GetFullPath(Path.Combine(folder, name));
        CheckLinks(path);
        if (Directory.Exists(path)) throw new IOException("文件目标是目录，已停止修改。");
        return path;
    }
    private static void CheckLinks(string path)
    {
        for (string? part = Path.GetFullPath(path); part != null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("快照不跟随符号链接或目录联接。");
    }
    public static string? Hash(string path)
    {
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    private void Save(ModUpdateSnapshot snapshot)
    {
        var path = Path.Combine(Location(snapshot.Id), "snapshot.json");
        var temp = path + ".tmp";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(output, snapshot); output.Flush(true); }
        File.Move(temp, path, true);
    }
    public IReadOnlyList<ModUpdateSnapshot> List()
    {
        lock (Gate)
        {
            if (!Directory.Exists(directory)) return Array.Empty<ModUpdateSnapshot>();
            var list = new List<ModUpdateSnapshot>();
            foreach (var path in Directory.EnumerateDirectories(directory).Take(1000))
            {
                try
                {
                    var item = JsonSerializer.Deserialize<ModUpdateSnapshot>(File.ReadAllBytes(Path.Combine(path, "snapshot.json")));
                    if (item != null && item.Id == Path.GetFileName(path) && item.Files != null) list.Add(item);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
            return list.OrderByDescending(item => item.CreatedUtc).ToArray();
        }
    }
    public ModUpdateSnapshot Create(string folder, IEnumerable<string> names, int retention, long maxBytes, CancellationToken ct)
    {
        lock (Gate)
        {
            folder = Path.GetFullPath(folder); CheckLinks(folder); CheckLinks(directory);
            var files = names.Distinct(StringComparer.OrdinalIgnoreCase).Select(name =>
            {
                var path = ResolveFile(folder, name);
                return new ModSnapshotFile { Name = name, Bytes = File.Exists(path) ? new FileInfo(path).Length : 0 };
            }).ToList();
            if (files.Count is 0 or > 1000) throw new IOException("快照文件数量无效或超过 1000 个。");
            var bytes = files.Sum(file => file.Bytes);
            if (bytes > maxBytes) throw new IOException("本次快照超过空间上限，请在设置中调整快照空间，或先手动备份。");
            var old = List();
            var total = old.Sum(item => item.Files.Sum(file => file.Bytes));
            var count = old.Count;
            foreach (var item in old.Reverse().Where(item => item.State is "Ready" or "Restored"))
            {
                if (count < Math.Clamp(retention, 1, 100) && total + bytes <= maxBytes) break;
                CheckLinks(Location(item.Id)); Directory.Delete(Location(item.Id), true);
                total -= item.Files.Sum(file => file.Bytes); count--;
            }
            if (count >= Math.Clamp(retention, 1, 100)) throw new IOException("未完成的快照已达到保留上限，请先检查快照目录。");
            if (total + bytes > maxBytes) throw new IOException("未完成的快照占用了保留空间，请先检查快照目录。");
            var snapshot = new ModUpdateSnapshot { Folder = folder, Files = files };
            var target = Location(snapshot.Id); Directory.CreateDirectory(target);
            try
            {
                Save(snapshot);
                for (var i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var source = ResolveFile(folder, files[i].Name);
                    if (!File.Exists(source)) continue;
                    var payload = Path.Combine(target, i + ".bin");
                    using (var input = File.OpenRead(source))
                    using (var output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920]; int read; long copied = 0;
                        while ((read = input.Read(buffer)) > 0)
                        {
                            ct.ThrowIfCancellationRequested(); copied += read;
                            if (copied > files[i].Bytes) throw new IOException("快照期间文件变大，请重新检查更新。");
                            output.Write(buffer, 0, read);
                        }
                        output.Flush(true);
                    }
                    files[i].BeforeHash = files[i].AfterHash = Hash(payload);
                    files[i].Bytes = new FileInfo(payload).Length;
                }
                Save(snapshot); return snapshot;
            }
            catch { Directory.Delete(target, true); throw; }
        }
    }
    public void RecordAfter(ModUpdateSnapshot snapshot, string name, string? hash)
    {
        lock (Gate)
        {
            snapshot.Files.Single(file => string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase)).AfterHash = hash;
            Save(snapshot);
        }
    }
    public void Complete(ModUpdateSnapshot snapshot)
    { lock (Gate) { snapshot.State = "Ready"; Save(snapshot); } }

    public void Restore(string id)
    {
        lock (Gate)
        {
            CheckLinks(Location(id));
            var snapshot = List().Single(item => item.Id == id);
            if (snapshot.State is not ("Ready" or "Restoring")) throw new IOException("此快照尚未完成或已经恢复；可打开目录手动核对。");
            // 先验证全部文件，避免发现后面的冲突时才知道前面的已被覆盖。
            for (var i = 0; i < snapshot.Files.Count; i++)
            {
                var file = snapshot.Files[i]; var current = Hash(ResolveFile(snapshot.Folder, file.Name));
                if (current != file.AfterHash && !(snapshot.State == "Restoring" && current == file.BeforeHash))
                    throw new IOException($"「{file.Name}」有后续修改，未覆盖；请先保存或合并该修改。");
                if (file.BeforeHash != null && Hash(Path.Combine(Location(id), i + ".bin")) != file.BeforeHash)
                    throw new IOException("快照内容校验失败，未恢复任何文件。");
            }
            snapshot.State = "Restoring"; Save(snapshot);
            for (var i = 0; i < snapshot.Files.Count; i++)
            {
                var file = snapshot.Files[i]; var path = ResolveFile(snapshot.Folder, file.Name);
                var current = Hash(path);
                if (current == file.BeforeHash) continue;
                if (current != file.AfterHash) throw new IOException("恢复期间文件发生变化，已停止。");
                if (file.BeforeHash == null) File.Delete(path);
                else
                {
                    var temp = path + ".restore-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.Copy(Path.Combine(Location(id), i + ".bin"), temp);
                        if (Hash(path) != file.AfterHash) throw new IOException("提交恢复前文件发生变化。");
                        File.Move(temp, path, true);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
            }
            snapshot.State = "Restored"; Save(snapshot);
        }
    }
}
